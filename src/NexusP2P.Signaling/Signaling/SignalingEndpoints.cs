using System.Net.WebSockets;
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
    /// <para><b>「码不存在」与「位子被占/席位已满」必须用同一句话。</b>任何差异都会
    /// 让九位码有了枚举预言机 —— 攻击者靠错误信息就能筛出活跃房间。</para>
    /// </summary>
    private const string UnavailableMessage = "房间不可用：文件码可能不存在、已失效，或已经有人在接收了。";

    /// <summary>单条信令消息的大小上限。SDP 通常几 KB，给足余量但必须有界。</summary>
    private const int MaxSignalMessageBytes = 256 * 1024;

    /// <summary>
    /// 进房口令的最大长度（字符）。
    ///
    /// <para>口令随查询参数走 WSS（传输中加密，等价于放在消息体里）。
    /// 上限是为了防止把建房请求当成长文缓冲 —— 口令不可能需要这么长。</para>
    /// </summary>
    private const int MaxPasswordLength = 64;

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

        // AD-15：不带 maxReceivers 的旧客户端得到 1，行为与 V1 完全一致。
        // 非法值一律夹回合法区间而不是报错 —— 建房是自己人发起的，没有必要惩罚。
        var maxReceivers = 1;
        if (int.TryParse(context.Request.Query["maxReceivers"], out var requested))
        {
            maxReceivers = Math.Clamp(requested, 1, options.MaxReceiversPerRoom);
        }

        // 可选口令：不设置（默认）时建房行为与从前完全一致。
        // 口令错误信息在这里直接说 —— 建房方是自己人，不存在预言机问题。
        var rawPassword = context.Request.Query["password"].ToString();
        RoomPassword? roomPassword = null;
        if (rawPassword.Length > 0)
        {
            if (rawPassword.Length > MaxPasswordLength)
            {
                using var early = await context.WebSockets.AcceptWebSocketAsync();
                using var earlySink = new WebSocketPeerSink(early);
                await earlySink.SendAsync(
                    ServerMessage.Error($"密码过长（最多 {MaxPasswordLength} 个字符）。"), context.RequestAborted);
                await CloseAsync(early, "密码过长");
                return;
            }

            roomPassword = RoomPassword.Create(rawPassword);
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var sink = new WebSocketPeerSink(socket);

        if (!registry.TryCreate(sink, out var code, out var room, maxReceivers, roomPassword))
        {
            await sink.SendAsync(
                ServerMessage.Error("服务器当前房间过多，请稍后再试。"), context.RequestAborted);
            await CloseAsync(socket, "房间已满");
            return;
        }

        var shareBase = $"{options.PublicOrigin.TrimEnd('/')}/{ShareLinkFactory.RoomPathSegment}";
        await sink.SendAsync(
            ServerMessage.Created(
                code.Digits, shareBase, turn.BuildIceServers($"room-{code.Digits}"), maxReceivers,
                roomPassword is not null),
            context.RequestAborted);

        await PumpSenderAsync(socket, sink, registry, room!, logger, context.RequestAborted);
    }

    private static async Task HandleJoinAsync(HttpContext context)
    {
        var rawCode = context.Request.RouteValues["code"]?.ToString();
        var limiter = context.RequestServices.GetRequiredService<JoinRateLimiter>();
        var registry = context.RequestServices.GetRequiredService<RoomRegistry>();
        var turn = context.RequestServices.GetRequiredService<TurnCredentialService>();
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        var options = context.RequestServices.GetRequiredService<IOptions<SignalingOptions>>().Value;

        // 限速放在 WebSocket 升级<b>之前</b>，这样才能返回一个真正的 429。
        // 升级之后就只能在应用层告知，客户端与中间设备都不认。
        // V2: 仅在 EnableJoinRateLimit=true 时启用限速
        if (options.EnableJoinRateLimit && !limiter.TryRecordAttempt(context.Connection.RemoteIpAddress))
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

        // 可选口令：房间设置了口令时，缺失/错误与「码不存在」返回同一句话 ——
        // 口令不能给九位码引入新的枚举预言机。发送方重连同样要凭口令。
        var password = context.Request.Query["password"].ToString();

        // 码格式不对时也走同一条失败路径。若在这里提前返回 400，
        // 「格式对但不存在」与「格式不对」就有了差异，又成了预言机。
        var parsed = TransferCode.TryParse(rawCode, out var code);

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var sink = new WebSocketPeerSink(socket);

        Room? room = null;
        string? peerId = null;
        var outcome = parsed
            ? registry.TryJoin(code, role, sink, password, out room, out peerId)
            : JoinOutcome.Unavailable;

        if (outcome != JoinOutcome.Joined)
        {
            await sink.SendAsync(ServerMessage.Error(UnavailableMessage), context.RequestAborted);
            await CloseAsync(socket, "房间不可用");
            return;
        }

        var iceServers = turn.BuildIceServers($"room-{code.Digits}");

        if (role == PeerRole.Sender)
        {
            // 发送方重连：应答里带当前在房的接收方列表（AD-12），
            // 晚回来的一方不能干等 peer-joined。
            await sink.SendAsync(
                ServerMessage.SenderJoined(iceServers, room!.ReceiverIds), context.RequestAborted);

            await PumpSenderAsync(socket, sink, registry, room, logger, context.RequestAborted);
            return;
        }

        // 两端几乎同时进房时，发送方可能既把这个接收方算进 peers，
        // 又收到一条 peer-joined。客户端对此是幂等的，这里不需要额外同步。
        await sink.SendAsync(
            ServerMessage.ReceiverJoined(iceServers, room!.Sender is not null, peerId!),
            context.RequestAborted);

        // 只告诉发送方 —— 接收方之间互不可见（AD-12）
        if (room.Sender is { } senderSink)
        {
            await SendToAsync(senderSink, ServerMessage.PeerJoined(peerId!), context.RequestAborted);
        }

        await PumpReceiverAsync(socket, sink, registry, room, peerId!, logger, context.RequestAborted);
    }

    /// <summary>
    /// 发送方的收发循环：<c>signal</c> 按 <c>to</c> 路由到指定接收方；
    /// 不带 <c>to</c> 时只在「房里恰好一个接收方」时可路由（V1 客户端，AD-15）。
    /// <c>to</c> 指向不存在的 peerId 时静默丢弃 —— 正常时序，不是协议违规。
    /// </summary>
    private static async Task PumpSenderAsync(
        WebSocket socket,
        WebSocketPeerSink sink,
        RoomRegistry registry,
        Room room,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in ReadClientMessagesAsync(socket, cancellationToken).ConfigureAwait(false))
            {
                var target = message.To is { Length: > 0 } to ? room.Receiver(to) : room.SoleReceiver;
                if (target is { } receiver)
                {
                    await SendToAsync(receiver, ServerMessage.Signal(message.Payload!.Value), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("房间 {Code} 的发送方连接断开：{Reason}", room.Code, ex.Message);
            }
        }
        finally
        {
            registry.Leave(room, PeerRole.Sender, sink);

            // 发送方走了要通知每个接收方（接收方的对端只有发送方，不需要 peerId）
            foreach (var (_, receiver) in room.Receivers)
            {
                await SendToAsync(receiver, ServerMessage.SenderLeft(), CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 接收方的收发循环：<c>signal</c> 一律只路由到发送方，带 <c>to</c> 也忽略 ——
    /// 接收方之间互不可见（AD-12），多暴露一个面只是白送攻击面。
    /// </summary>
    private static async Task PumpReceiverAsync(
        WebSocket socket,
        WebSocketPeerSink sink,
        RoomRegistry registry,
        Room room,
        string peerId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in ReadClientMessagesAsync(socket, cancellationToken).ConfigureAwait(false))
            {
                if (room.Sender is { } sender)
                {
                    await SendToAsync(
                            sender, ServerMessage.SignalFrom(message.Payload!.Value, peerId), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(
                    "房间 {Code} 的接收方 {PeerId} 连接断开：{Reason}", room.Code, peerId, ex.Message);
            }
        }
        finally
        {
            registry.LeaveReceiver(room, peerId, sink);

            if (room.Sender is { } sender)
            {
                await SendToAsync(sender, ServerMessage.PeerLeft(peerId), CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>逐条读出合法的 <c>signal</c> 消息，其他类型与畸形 JSON 一律跳过。</summary>
    private static async IAsyncEnumerable<ClientMessage> ReadClientMessagesAsync(
        WebSocket socket,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxSignalMessageBytes];

        while (socket.State == WebSocketState.Open)
        {
            var received = await ReceiveFullMessageAsync(socket, buffer, cancellationToken)
                .ConfigureAwait(false);

            if (received is null)
            {
                yield break;   // 对端关闭或消息超限
            }

            if (TryReadSignal(buffer.AsSpan(0, received.Value), out var message))
            {
                yield return message;
            }
        }
    }

    private static Task SendToAsync(IPeerSink sink, ServerMessage message, CancellationToken cancellationToken) =>
        sink.SendAsync(JsonSerializer.Serialize(message, WebSocketPeerSink.JsonOptions), cancellationToken);

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
    /// 只取出 <c>signal</c> 消息（payload + 可选 to）。<b>不解析 payload 本身</b> ——
    /// 它对服务器是不透明的。
    /// </summary>
    private static bool TryReadSignal(ReadOnlySpan<byte> utf8, out ClientMessage message)
    {
        message = null!;

        try
        {
            var parsed = JsonSerializer.Deserialize<ClientMessage>(utf8, WebSocketPeerSink.JsonOptions);

            if (parsed?.Type != "signal" || parsed.Payload is not { } value)
            {
                return false;
            }

            // Clone：JsonElement 默认指向已被释放的 JsonDocument 缓冲区
            message = parsed with { Payload = value.Clone() };
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
