using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NexusP2P.Core.Codes;
using NexusP2P.Signaling.RateLimiting;
using NexusP2P.Signaling.Rooms;
using NexusP2P.Signaling.Turn;

namespace NexusP2P.Signaling.Signaling;

/// <summary>信令的 WebSocket 端点。</summary>
public static class SignalingEndpoints
{
    /// <summary>
    /// 对外统一的入房失败说明。
    ///
    /// <para><b>「码不存在」与「位子被占」必须用同一句话。</b>任何差异都会
    /// 让九位码有了枚举预言机 —— 攻击者靠错误信息就能筛出活跃房间。</para>
    /// </summary>
    private const string UnavailableMessage = "房间不可用：文件码可能不存在、已失效，或已经有人在接收了。";

    /// <summary>单条信令消息的大小上限。SDP 通常几 KB，给足余量但必须有界。</summary>
    private const int MaxSignalMessageBytes = 256 * 1024;

    public static void MapSignaling(this WebApplication app)
    {
        app.Map("/signal/create", HandleCreateAsync);
        app.Map("/signal/join/{code}", HandleJoinAsync);
    }

    private static async Task HandleCreateAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var registry = context.RequestServices.GetRequiredService<RoomRegistry>();
        var options = context.RequestServices.GetRequiredService<IOptions<SignalingOptions>>().Value;
        var turn = context.RequestServices.GetRequiredService<TurnCredentialService>();
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var sink = new WebSocketPeerSink(socket);

        if (!registry.TryCreate(sink, out var code, out var room))
        {
            await sink.SendAsync(
                ServerMessage.Error("服务器当前房间过多，请稍后再试。"), context.RequestAborted);
            await CloseAsync(socket, "房间已满");
            return;
        }

        var shareBase = $"{options.PublicOrigin.TrimEnd('/')}/{ShareLinkFactory.RoomPathSegment}";
        await sink.SendAsync(
            ServerMessage.Created(code.Digits, shareBase, turn.BuildIceServers($"room-{code.Digits}")),
            context.RequestAborted);

        await PumpAsync(socket, sink, registry, room!, PeerRole.Sender, logger, context.RequestAborted);
    }

    private static async Task HandleJoinAsync(HttpContext context)
    {
        var rawCode = context.Request.RouteValues["code"]?.ToString();
        var limiter = context.RequestServices.GetRequiredService<JoinRateLimiter>();
        var registry = context.RequestServices.GetRequiredService<RoomRegistry>();
        var options = context.RequestServices.GetRequiredService<IOptions<SignalingOptions>>().Value;
        var turn = context.RequestServices.GetRequiredService<TurnCredentialService>();
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();

        // 限速放在 WebSocket 升级<b>之前</b>，这样才能返回一个真正的 429。
        // 升级之后就只能在应用层告知，客户端与中间设备都不认。
        if (!limiter.TryRecordAttempt(context.Connection.RemoteIpAddress))
        {
            logger.LogWarning("入房尝试被限速：{Address}", context.Connection.RemoteIpAddress);
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = "60";
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var role = string.Equals(context.Request.Query["role"], "sender", StringComparison.OrdinalIgnoreCase)
            ? PeerRole.Sender
            : PeerRole.Receiver;

        // 码格式不对时也走同一条失败路径。若在这里提前返回 400，
        // 「格式对但不存在」与「格式不对」就有了差异，又成了预言机。
        var parsed = TransferCode.TryParse(rawCode, out var code);

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var sink = new WebSocketPeerSink(socket);

        Room? room = null;
        var outcome = parsed
            ? registry.TryJoin(code, role, sink, out room)
            : JoinOutcome.Unavailable;

        if (outcome != JoinOutcome.Joined)
        {
            await sink.SendAsync(ServerMessage.Error(UnavailableMessage), context.RequestAborted);
            await CloseAsync(socket, "房间不可用");
            return;
        }

        // 两端几乎同时进房时，对端可能既被算进 peerPresent，又给我们发来
        // 一条 peer-joined。客户端对此是幂等的，所以这里不需要额外同步。
        var counterpart = room!.Counterpart(role);

        await sink.SendAsync(
            ServerMessage.Joined(turn.BuildIceServers($"room-{code.Digits}"), counterpart is not null),
            context.RequestAborted);

        // 告诉对端有人来了
        if (counterpart is { } peer)
        {
            await peer.SendAsync(
                JsonSerializer.Serialize(ServerMessage.PeerJoined(), WebSocketPeerSink.JsonOptions),
                context.RequestAborted);
        }

        await PumpAsync(socket, sink, registry, room, role, logger, context.RequestAborted);
    }

    /// <summary>
    /// 收发循环：把客户端发来的 <c>signal</c> 原样转给对端。
    /// 其他类型一律忽略 —— 服务器的职责只有转发。
    /// </summary>
    private static async Task PumpAsync(
        WebSocket socket,
        WebSocketPeerSink sink,
        RoomRegistry registry,
        Room room,
        PeerRole role,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxSignalMessageBytes];

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var received = await ReceiveFullMessageAsync(socket, buffer, cancellationToken)
                    .ConfigureAwait(false);

                if (received is null)
                {
                    break;   // 对端关闭或消息超限
                }

                if (!TryReadSignalPayload(buffer.AsSpan(0, received.Value), out var payload))
                {
                    continue;
                }

                if (room.Counterpart(role) is { } peer)
                {
                    await peer.SendAsync(
                            JsonSerializer.Serialize(
                                ServerMessage.Signal(payload), WebSocketPeerSink.JsonOptions),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("房间 {Code} 的 {Role} 连接断开：{Reason}", room.Code, role, ex.Message);
            }
        }
        finally
        {
            registry.Leave(room, role, sink);

            if (room.Counterpart(role) is { } peer)
            {
                await peer.SendAsync(
                        JsonSerializer.Serialize(ServerMessage.PeerLeft(), WebSocketPeerSink.JsonOptions),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 读一条完整消息。超过上限就断开 —— 一条 SDP 不该有几百 KB，
    /// 而无界读取是让内存被打爆的经典路径。
    /// </summary>
    private static async Task<int?> ReceiveFullMessageAsync(
        WebSocket socket, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;

        while (true)
        {
            if (offset >= buffer.Length)
            {
                return null;   // 撑爆缓冲区，视为协议违规
            }

            var result = await socket
                .ReceiveAsync(buffer.AsMemory(offset), cancellationToken)
                .ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            offset += result.Count;

            if (result.EndOfMessage)
            {
                return offset;
            }
        }
    }

    /// <summary>
    /// 只取出 <c>signal</c> 消息的 payload。<b>不解析 payload 本身</b> ——
    /// 它对服务器是不透明的。
    /// </summary>
    private static bool TryReadSignalPayload(ReadOnlySpan<byte> utf8, out JsonElement payload)
    {
        payload = default;

        try
        {
            var message = JsonSerializer.Deserialize<ClientMessage>(utf8, WebSocketPeerSink.JsonOptions);

            if (message?.Type != "signal" || message.Payload is not { } value)
            {
                return false;
            }

            // Clone：JsonElement 默认指向已被释放的 JsonDocument 缓冲区
            payload = value.Clone();
            return true;
        }
        catch (JsonException)
        {
            // 畸形 JSON 忽略即可，不值得为它断开一条正常的信令连接
            return false;
        }
    }

    private static async Task CloseAsync(WebSocket socket, string reason)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                // 对端已经消失
            }
        }
    }
}
