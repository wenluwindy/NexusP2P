using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusP2P.Transfer.Reconnect;
using NexusP2P.Transport.WebRtc;

namespace NexusP2P.Agent.Signaling;

/// <summary>建房成功后拿到的东西。</summary>
public sealed record RoomCreated(string Code, string ShareUrlBase, IReadOnlyList<string> IceServers);

/// <summary>
/// 信令服务器的客户端：把 <see cref="WebRtcPeerConnection"/> 的
/// 描述与候选送出去，并把对端的送进来。
///
/// <para><b>只有 exe 的后端用它。</b>网页端的 UI 自己就是 WebRTC 端点，
/// 会直接连信令服务器 —— 所以 exe 里<b>不需要</b>把信令代理给 UI
/// （AD-3 里提到的那个代理是多余的：exe 模式下 UI 从头到尾只跟 localhost 说话）。</para>
/// </summary>
public sealed class SignalingClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ClientWebSocket _socket = new();
    private readonly AgentOptions _options;

    /// <summary>
    /// 出站消息队列。
    ///
    /// <para><b>顺序至关重要</b>：ICE 候选必须在会话描述<b>之后</b>到达对端，
    /// 否则会被丢弃（对端还不知道这是哪个会话的候选）。而描述与候选是
    /// 从原生回调线程分别触发的 —— 若各自 fire-and-forget 地发，
    /// 顺序就没有保证。内存回环里握手是同步转交的，天然有序，
    /// 所以这个问题只在接上真实信令后才暴露。</para>
    /// </summary>
    private readonly Channel<string> _outbound = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });

    private Task? _sendPump;

    /// <summary>
    /// 「对端已进房」的锁存。
    ///
    /// <para>不能用事件来等这件事：房间建好到调用方挂上处理器之间有一个窗口，
    /// 对端只要在这个窗口里进来，通知就永久丢了 —— 而对端拿到码之后
    /// <b>正是立刻</b>就会进来，所以这个竞态几乎必然发生。
    /// 用 TCS 在构造时就建好，无论何时 await 都能拿到结果。</para>
    /// </summary>
    private readonly TaskCompletionSource _peerPresent =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// 信令投递的闸门。
    ///
    /// <para><b>进房成功那一刻收发泵就开始跑了，但调用方还要先把
    /// WebRTC 对象建出来（原生库初始化要几百毫秒）才挂得上处理器。</b>
    /// 而对端一收到「有人进来了」就立刻发 offer —— 这条 offer 正好落在
    /// 这个窗口里，投给一个 null 事件，然后永远消失，两端一起等到超时。</para>
    ///
    /// <para>机器越快越容易中招：发送端是热的（原生库已加载），
    /// 接收端却每次都要冷启动。所以在闸门打开之前先把信令攒着，
    /// <see cref="BeginSignalDelivery"/> 时按原顺序补发。</para>
    /// </summary>
    private readonly Lock _signalGate = new();
    private readonly Queue<JsonNode> _pendingSignals = new();
    private bool _signalsFlowing;

    private Task? _pumpTask;
    private CancellationTokenSource? _pumpCancellation;
    private bool _disposed;

    public SignalingClient(AgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var problems = options.Validate();
        if (problems.Count > 0)
        {
            // 配置错了就在这里失败，而不是等用户输完码才说「连不上」
            throw new InvalidOperationException(
                "信令配置不合法：" + string.Join("；", problems));
        }

        _options = options;
    }

    /// <summary>对端加入了房间。</summary>
    public event Action? PeerJoined;

    /// <summary>
    /// 等对端进房。<b>已经进来过就立刻返回</b> —— 见 <c>_peerPresent</c> 的说明。
    /// </summary>
    public Task WaitForPeerAsync(CancellationToken cancellationToken = default) =>
        _peerPresent.Task.WaitAsync(cancellationToken);

    /// <summary>对端离开了。</summary>
    public event Action? PeerLeft;

    /// <summary>服务器报告的错误。发生后连接即将关闭。</summary>
    public event Action<string>? ErrorReceived;

    /// <summary>收到对端的会话描述。</summary>
    public event Action<SessionDescription>? RemoteDescriptionReceived;

    /// <summary>收到对端的 ICE 候选。</summary>
    public event Action<IceCandidate>? RemoteCandidateReceived;

    /// <summary>
    /// 声明 <see cref="RemoteDescriptionReceived"/> 与
    /// <see cref="RemoteCandidateReceived"/> 都已挂好，并把在此之前
    /// 到达的信令按原顺序补发出去。
    ///
    /// <para><b>挂完处理器必须调这个</b>，否则窗口期到达的信令会被丢掉 ——
    /// 见 <c>_signalGate</c> 的说明。重复调用无害。</para>
    /// </summary>
    public void BeginSignalDelivery()
    {
        // 整个补发过程持锁：否则泵线程会在补发途中插进新信令，
        // 让候选跑到描述前面 —— 那样的候选会被 WebRTC 直接丢弃。
        lock (_signalGate)
        {
            _signalsFlowing = true;

            while (_pendingSignals.TryDequeue(out var payload))
            {
                DeliverSignal(payload);
            }
        }
    }

    /// <summary>建房，返回文件码与分享链接基址。</summary>
    public async Task<RoomCreated> CreateRoomAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _socket.ConnectAsync(_options.BuildSignalingUri("/signal/create"), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            // 服务器暂时不可达。网线拔了十秒就是这个样子，值得重试。
            throw new SignalingException(
                $"连接信令服务器失败：{ex.Message}", ex, retryable: true);
        }

        var message = await AwaitHandshakeAsync("created", "建房", cancellationToken).ConfigureAwait(false);

        StartPump();

        return new RoomCreated(
            message["code"]?.GetValue<string>() ?? throw new SignalingException("服务器没有返回文件码。"),
            message["shareUrlBase"]?.GetValue<string>() ?? string.Empty,
            ReadIceServers(message));
    }

    /// <summary>用文件码进房。</summary>
    public async Task<IReadOnlyList<string>> JoinRoomAsync(
        string code, bool asSender = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        var role = asSender ? "sender" : "receiver";
        var uri = _options.BuildSignalingUri($"/signal/join/{code}", $"role={role}");

        try
        {
            await _socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            // 429 会在升级阶段就被拒，这里要给出用户能懂的说法
            throw new SignalingException(
                $"连接信令服务器失败：{ex.Message}。可能是尝试过于频繁，请稍后再试。", ex, retryable: true);
        }

        var message = await AwaitHandshakeAsync("joined", "进房", cancellationToken).ConfigureAwait(false);

        // 重连回到的房间里对端可能已经在了。peer-joined 只在「进来那一刻」
        // 发一次，晚回来的一方等不到它 —— 所以要认服务器给的这个状态位。
        if (message["peerPresent"]?.GetValue<bool>() == true)
        {
            _peerPresent.TrySetResult();
        }

        StartPump();
        return ReadIceServers(message);
    }

    /// <summary>
    /// 读到握手应答（<c>created</c> 或 <c>joined</c>）为止。
    ///
    /// <para><b>握手应答不保证是第一条消息。</b>两端几乎同时进房时，
    /// 对端的 <c>peer-joined</c> 可能先一步到达 —— 重连时两端<b>正是</b>
    /// 同时回来的。把「不是应答」一律当成错误会让重连稳定地失败在这里。</para>
    ///
    /// <para>提前到达的消息不能丢，照常走 <see cref="Dispatch"/>：
    /// <c>peer-joined</c> 会锁存，信令会被攒进 <c>_pendingSignals</c>。</para>
    /// </summary>
    private async Task<JsonNode> AwaitHandshakeAsync(
        string expected, string what, CancellationToken cancellationToken)
    {
        while (true)
        {
            var message = await ReceiveJsonAsync(cancellationToken).ConfigureAwait(false)
                          ?? throw new SignalingException(
                              $"{what}时连接被关闭，服务器没有给出应答。", retryable: true);

            switch (message["type"]?.GetValue<string>())
            {
                case { } type when type == expected:
                    return message;

                case "error":
                    throw new SignalingException(
                        message["message"]?.GetValue<string>() ?? $"{what}失败。");

                case null:
                    throw new SignalingException($"{what}时收到没有类型的消息。");

                default:
                    Dispatch(message);
                    break;
            }
        }
    }

    /// <summary>把本地描述送给对端。</summary>
    public Task SendDescriptionAsync(SessionDescription description, CancellationToken cancellationToken = default) =>
        SendSignalAsync(new { sdp = description.Sdp, type = description.Type }, cancellationToken);

    /// <summary>把本地候选送给对端。</summary>
    public Task SendCandidateAsync(IceCandidate candidate, CancellationToken cancellationToken = default) =>
        SendSignalAsync(new { candidate = candidate.Candidate, mid = candidate.Mid }, cancellationToken);

    private Task SendSignalAsync(object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(new { type = "signal", payload }, JsonOptions);

        // 入队即返回。真正的发送由单消费者按入队顺序完成 —— 见 _outbound 的说明。
        _ = _outbound.Writer.TryWrite(json);
        return Task.CompletedTask;
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
            // 信令连接断开是常态
        }
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
            // 信令连接断开是常态（传输建立后它就没用了），不值得往上抛
        }
    }

    private void Dispatch(JsonNode message)
    {
        switch (message["type"]?.GetValue<string>())
        {
            case "peer-joined":
                _peerPresent.TrySetResult();
                PeerJoined?.Invoke();
                break;

            case "peer-left":
                PeerLeft?.Invoke();
                break;

            case "error":
                var text = message["message"]?.GetValue<string>() ?? "服务器报告了一个错误。";
                // 等对端的调用方也要醒过来，否则会一直等到超时
                _peerPresent.TrySetException(new SignalingException(text));
                ErrorReceived?.Invoke(text);
                break;

            case "signal":
                DispatchSignal(message["payload"]);
                break;
        }
    }

    private void DispatchSignal(JsonNode? payload)
    {
        if (payload is null)
        {
            return;
        }

        lock (_signalGate)
        {
            if (!_signalsFlowing)
            {
                _pendingSignals.Enqueue(payload);
                return;
            }

            DeliverSignal(payload);
        }
    }

    private void DeliverSignal(JsonNode payload)
    {
        // 服务器不解析 payload，所以这里要自己分辨是描述还是候选
        var sdp = payload["sdp"]?.GetValue<string>();
        var type = payload["type"]?.GetValue<string>();
        if (sdp is not null && type is not null)
        {
            RemoteDescriptionReceived?.Invoke(new SessionDescription(sdp, type));
            return;
        }

        var candidate = payload["candidate"]?.GetValue<string>();
        if (candidate is not null)
        {
            RemoteCandidateReceived?.Invoke(new IceCandidate(candidate, payload["mid"]?.GetValue<string>()));
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
        _peerPresent.TrySetCanceled();

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

/// <summary>
/// 信令交互失败。
///
/// <para><see cref="IsRetryable"/> 把「网络抖了一下」与「这个码没用」分开：
/// 前者重试有意义，后者重试只是让用户晚十几秒才看到真正的原因。</para>
/// </summary>
public sealed class SignalingException(string message, Exception? inner = null, bool retryable = false)
    : Exception(message, inner), IRetryableFailure
{
    public bool IsRetryable { get; } = retryable;
}
