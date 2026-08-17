using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using NexusP2P.Transport.WebRtc;

namespace NexusP2P.Agent.Signaling;

/// <summary>
/// 发送方的多接收方信令客户端（V2，AD-12）。
///
/// <para>与 <see cref="SignalingClient"/> 的分工：那个类服务<b>一对一</b>
/// （接收方永远用它；旧发送方路径也用它）；这个类只给一对多的发送方用 ——
/// 建房时声明 <c>maxReceivers</c>，之后按 <c>peerId</c> 路由每个接收方的信令。</para>
///
/// <para>V1 那两个来之不易的教训在这里逐 peer 适用：</para>
/// <list type="bullet">
/// <item><b>peer-joined 不能靠事件等</b> —— 用 Channel 缓冲，订阅前到达的不丢；</item>
/// <item><b>信令要按 peer 攒住</b> —— 每个 peer 的 WebRTC 对象要几百毫秒才建好，
/// 这期间到达的 answer/候选按 peerId 入队，<see cref="BeginSignalDelivery"/> 时补发。</item>
/// </list>
/// </summary>
public sealed class FanOutSignalingClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ClientWebSocket _socket = new();
    private readonly AgentOptions _options;

    /// <summary>出站消息队列：描述必须先于候选到达，单消费者保序（与 V1 同理）。</summary>
    private readonly Channel<string> _outbound = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });

    /// <summary>
    /// 接收方进房事件流。<b>用 Channel 而不是事件</b>：读取方挂上之前
    /// 到达的 peer-joined 不能丢 —— 对端拿到码之后立刻就会进来。
    /// </summary>
    private readonly Channel<string> _arrivals = Channel.CreateUnbounded<string>();

    private readonly Lock _peerGate = new();
    private readonly Dictionary<string, PeerSignalSink> _peers = [];

    private Task? _pumpTask;
    private Task? _sendPump;
    private CancellationTokenSource? _pumpCancellation;
    private bool _disposed;

    /// <summary>一个接收方的信令投递口（含开闸前的缓冲）。</summary>
    private sealed class PeerSignalSink
    {
        public bool Flowing;
        public readonly Queue<JsonNode> Pending = new();
        public Action<SessionDescription>? OnDescription;
        public Action<IceCandidate>? OnCandidate;
    }

    public FanOutSignalingClient(AgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var problems = options.Validate();
        if (problems.Count > 0)
        {
            throw new InvalidOperationException("信令配置不合法：" + string.Join("；", problems));
        }

        _options = options;
    }

    /// <summary>某个接收方离开了。</summary>
    public event Action<string>? ReceiverLeft;

    /// <summary>服务器报告的错误。发生后连接即将关闭。</summary>
    public event Action<string>? ErrorReceived;

    /// <summary>
    /// 依次取出进房的接收方 peerId。<b>没有新接收方时会等</b>；
    /// 连接关闭后返回 null。
    /// </summary>
    public async Task<string?> WaitForReceiverAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _arrivals.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    /// <summary>建房（声明接收方席位数），返回文件码、分享基址与生效席位。</summary>
    public async Task<FanOutRoomCreated> CreateRoomAsync(
        int maxReceivers, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxReceivers, 1);

        var uri = _options.BuildSignalingUri("/signal/create", $"maxReceivers={maxReceivers}");
        try
        {
            await _socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            throw new SignalingException($"连接信令服务器失败：{ex.Message}", ex, retryable: true);
        }

        var message = await ReceiveJsonAsync(cancellationToken).ConfigureAwait(false)
                      ?? throw new SignalingException("建房时连接被关闭，服务器没有给出应答。", retryable: true);

        switch (message["type"]?.GetValue<string>())
        {
            case "created":
                break;
            case "error":
                throw new SignalingException(message["message"]?.GetValue<string>() ?? "建房失败。");
            default:
                throw new SignalingException("建房时收到预期之外的消息。");
        }

        StartPump();

        return new FanOutRoomCreated(
            message["code"]?.GetValue<string>() ?? throw new SignalingException("服务器没有返回文件码。"),
            message["shareUrlBase"]?.GetValue<string>() ?? string.Empty,
            ReadIceServers(message),
            // 旧服务器不回显 maxReceivers：视为 1，调用方据此降级为一对一（AD-15）
            message["maxReceivers"]?.GetValue<int>() ?? 1);
    }

    /// <summary>
    /// 以发送方身份回到已存在的房间（断线重连）。
    /// 返回进房那一刻已在房的接收方列表；它们也会按序进入
    /// <see cref="WaitForReceiverAsync"/> 的队列，调用方照常为它们建链。
    /// </summary>
    public async Task<IReadOnlyList<string>> RejoinAsync(
        string code, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        var uri = _options.BuildSignalingUri($"/signal/join/{code}", "role=sender");
        try
        {
            await _socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            throw new SignalingException(
                $"连接信令服务器失败：{ex.Message}。可能是尝试过于频繁，请稍后再试。", ex, retryable: true);
        }

        var message = await ReceiveJsonAsync(cancellationToken).ConfigureAwait(false)
                      ?? throw new SignalingException("进房时连接被关闭，服务器没有给出应答。", retryable: true);

        switch (message["type"]?.GetValue<string>())
        {
            case "joined":
                break;
            case "error":
                throw new SignalingException(message["message"]?.GetValue<string>() ?? "进房失败。");
            default:
                throw new SignalingException("进房时收到预期之外的消息。");
        }

        var present = new List<string>();
        if (message["peers"] is JsonArray peers)
        {
            foreach (var entry in peers)
            {
                if (entry?.GetValue<string>() is { Length: > 0 } peerId)
                {
                    present.Add(peerId);
                    _ = _arrivals.Writer.TryWrite(peerId);
                }
            }
        }

        StartPump();
        return present;
    }

    /// <summary>
    /// 为一个接收方挂上信令处理器并开闸。<b>两个处理器都挂好了才调</b> ——
    /// 开闸前到达的信令按原顺序补发（与 V1 的 BeginSignalDelivery 同理）。
    /// </summary>
    public void BeginSignalDelivery(
        string peerId, Action<SessionDescription> onDescription, Action<IceCandidate> onCandidate)
    {
        ArgumentNullException.ThrowIfNull(onDescription);
        ArgumentNullException.ThrowIfNull(onCandidate);

        lock (_peerGate)
        {
            var sink = GetOrAddPeerLocked(peerId);
            sink.OnDescription = onDescription;
            sink.OnCandidate = onCandidate;
            sink.Flowing = true;

            while (sink.Pending.TryDequeue(out var payload))
            {
                DeliverSignal(sink, payload);
            }
        }
    }

    /// <summary>链路拆除后忘掉这个 peer 的路由状态。</summary>
    public void ForgetPeer(string peerId)
    {
        lock (_peerGate)
        {
            _peers.Remove(peerId);
        }
    }

    /// <summary>把本地描述送给指定接收方（带 to，AD-12）。</summary>
    public Task SendDescriptionAsync(
        string peerId, SessionDescription description, CancellationToken cancellationToken = default) =>
        SendSignalAsync(peerId, new { sdp = description.Sdp, type = description.Type });

    /// <summary>把本地候选送给指定接收方。</summary>
    public Task SendCandidateAsync(
        string peerId, IceCandidate candidate, CancellationToken cancellationToken = default) =>
        SendSignalAsync(peerId, new { candidate = candidate.Candidate, mid = candidate.Mid });

    private Task SendSignalAsync(string peerId, object payload)
    {
        var json = JsonSerializer.Serialize(new { type = "signal", payload, to = peerId }, JsonOptions);
        _ = _outbound.Writer.TryWrite(json);
        return Task.CompletedTask;
    }

    private void StartPump()
    {
        _pumpCancellation = new CancellationTokenSource();
        var token = _pumpCancellation.Token;
        _pumpTask = Task.Run(() => PumpAsync(token), CancellationToken.None);
        _sendPump = Task.Run(() => SendPumpAsync(token), CancellationToken.None);
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (_socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var message = await ReceiveJsonAsync(cancellationToken).ConfigureAwait(false);
                if (message is null)
                {
                    break;
                }

                Dispatch(message);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or JsonException)
        {
            // 信令连接断开是常态
        }
        finally
        {
            _arrivals.Writer.TryComplete();
        }
    }

    private async Task SendPumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var json in _outbound.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_socket.State != WebSocketState.Open)
                {
                    break;
                }

                await _socket.SendAsync(
                        Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ChannelClosedException)
        {
            // 同上
        }
    }

    private void Dispatch(JsonNode message)
    {
        switch (message["type"]?.GetValue<string>())
        {
            case "peer-joined":
                if (message["peerId"]?.GetValue<string>() is { Length: > 0 } joinedId)
                {
                    _ = _arrivals.Writer.TryWrite(joinedId);
                }

                break;

            case "peer-left":
                if (message["peerId"]?.GetValue<string>() is { Length: > 0 } leftId)
                {
                    ReceiverLeft?.Invoke(leftId);
                }

                break;

            case "error":
                ErrorReceived?.Invoke(
                    message["message"]?.GetValue<string>() ?? "服务器报告了一个错误。");
                break;

            case "signal":
                if (message["from"]?.GetValue<string>() is { Length: > 0 } from
                    && message["payload"] is { } payload)
                {
                    RouteSignal(from, payload);
                }

                break;
        }
    }

    private void RouteSignal(string from, JsonNode payload)
    {
        lock (_peerGate)
        {
            var sink = GetOrAddPeerLocked(from);

            if (!sink.Flowing)
            {
                sink.Pending.Enqueue(payload);
                return;
            }

            DeliverSignal(sink, payload);
        }
    }

    /// <summary>调用方必须已持有 <see cref="_peerGate"/>。</summary>
    private PeerSignalSink GetOrAddPeerLocked(string peerId)
    {
        if (!_peers.TryGetValue(peerId, out var sink))
        {
            sink = new PeerSignalSink();
            _peers.Add(peerId, sink);
        }

        return sink;
    }

    private static void DeliverSignal(PeerSignalSink sink, JsonNode payload)
    {
        var sdp = payload["sdp"]?.GetValue<string>();
        var type = payload["type"]?.GetValue<string>();
        if (sdp is not null && type is not null)
        {
            sink.OnDescription?.Invoke(new SessionDescription(sdp, type));
            return;
        }

        var candidate = payload["candidate"]?.GetValue<string>();
        if (candidate is not null)
        {
            sink.OnCandidate?.Invoke(new IceCandidate(candidate, payload["mid"]?.GetValue<string>()));
        }
    }

    private async Task<JsonNode?> ReceiveJsonAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var offset = 0;

        while (true)
        {
            if (offset >= buffer.Length)
            {
                throw new SignalingException("信令消息超过 64 KiB，拒绝处理。");
            }

            var result = await _socket.ReceiveAsync(buffer.AsMemory(offset), cancellationToken)
                .ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            offset += result.Count;

            if (result.EndOfMessage)
            {
                return JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, offset));
            }
        }
    }

    private static List<string> ReadIceServers(JsonNode message)
    {
        if (message["iceServers"] is not JsonArray array)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var entry in array)
        {
            if (entry?["urls"] is JsonArray urls)
            {
                result.AddRange(urls.Select(u => u?.GetValue<string>()).OfType<string>());
            }
        }

        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _arrivals.Writer.TryComplete();

        if (_pumpCancellation is not null)
        {
            await _pumpCancellation.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "结束", CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
            // 对端可能已经走了
        }

        _outbound.Writer.TryComplete();

        foreach (var pump in new[] { _pumpTask, _sendPump })
        {
            if (pump is null)
            {
                continue;
            }

            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _pumpCancellation?.Dispose();
        _socket.Dispose();
    }
}

/// <summary>建房成功后拿到的东西（V2：多了生效的席位数）。</summary>
public sealed record FanOutRoomCreated(
    string Code,
    string ShareUrlBase,
    IReadOnlyList<string> IceServers,
    int MaxReceivers);
