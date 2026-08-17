using System.Collections.Concurrent;
using NexusP2P.Agent.Signaling;
using NexusP2P.Core.Crypto;
using NexusP2P.Core.Manifest;
using NexusP2P.Transfer;
using NexusP2P.Transfer.Protocol;
using NexusP2P.Transport.WebRtc;

namespace NexusP2P.Agent.Transfers;

/// <summary>一条扇出链路的宿主侧信息（快照 + 直连/中继）。</summary>
public sealed record FanOutPeerStatus(
    string PeerId,
    FanOutLinkState State,
    TransferProgress Progress,
    CandidatePairKind? CandidateKind,
    Exception? Error);

/// <summary>
/// 一对多发送的宿主编排（Task 9.3）：把 <see cref="FanOutSignalingClient"/>、
/// 每接收方一条 <see cref="WebRtcPeerConnection"/> 与 <see cref="SendFanOut"/> 接起来。
///
/// <para>职责边界：<see cref="SendFanOut"/> 只认「一条建好的连接」；
/// 这里负责「接收方进房（peer-joined）→ 建 PeerConnection → 挂信令 → 开链路」
/// 的动态编排，以及 peer-left 时的拆除。</para>
///
/// <para><b>一条链路的失败只拆自己</b>（AD-11）：建连失败、传输失败、
/// 对端离开，都不影响其他链路，也不影响继续接纳新的接收方。</para>
/// </summary>
public sealed class FanOutSender : IAsyncDisposable
{
    private readonly AgentOptions _options;
    private readonly TransferManifest _manifest;
    private readonly TransferSecret _secret;
    private readonly ICipherPieceProvider _cipherProvider;
    private readonly SendFanOut _fanOut;
    private readonly ConcurrentDictionary<string, PeerRuntime> _peers = [];

    private FanOutSignalingClient? _signaling;
    private bool _disposed;

    private sealed record PeerRuntime(WebRtcPeerConnection Peer, ProtocolConnection Connection)
    {
        public CandidatePairKind? LastKnownKind { get; set; }
    }

    public FanOutSender(
        AgentOptions options,
        TransferManifest manifest,
        TransferSecret secret,
        ICipherPieceProvider cipherProvider)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _cipherProvider = cipherProvider ?? throw new ArgumentNullException(nameof(cipherProvider));
        _secret = secret;
        _fanOut = new SendFanOut(manifest, secret, cipherProvider);
    }

    /// <summary>某条链路的状态变了（后台线程触发，宿主自己切线程）。</summary>
    public event Action<FanOutPeerStatus>? PeerStatusChanged;

    /// <summary>当前全部链路的状态。</summary>
    public IReadOnlyList<FanOutPeerStatus> Peers =>
        [.. _fanOut.Links.Select(snapshot => new FanOutPeerStatus(
            snapshot.PeerId,
            snapshot.State,
            snapshot.Progress,
            _peers.TryGetValue(snapshot.PeerId, out var runtime) ? ProbeKind(runtime) : null,
            snapshot.Error))];

    /// <summary>
    /// 建房并持续接纳接收方，直到 <paramref name="until"/> 完成或取消。
    /// 返回建房信息（含生效席位；旧服务器回显 1 时调用方据此降级，AD-15）。
    /// </summary>
    /// <param name="maxReceivers">想要的接收方席位数（服务器可能夹小）。</param>
    /// <param name="onRoomCreated">拿到文件码时立刻回调 —— UI 要马上显示。</param>
    /// <param name="until">
    /// 接纳循环的退出条件（例如「所有已知链路都完成且用户按了结束」）。
    /// null 表示一直接纳到取消为止。
    /// </param>
    public async Task<FanOutRoomCreated> RunAsync(
        int maxReceivers,
        Action<FanOutRoomCreated>? onRoomCreated = null,
        Task? until = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _signaling = new FanOutSignalingClient(_options);
        _signaling.ReceiverLeft += OnReceiverLeft;

        var room = await _signaling.CreateRoomAsync(maxReceivers, cancellationToken).ConfigureAwait(false);
        onRoomCreated?.Invoke(room);

        await AcceptLoopAsync(_signaling, room.IceServers, until, cancellationToken).ConfigureAwait(false);
        return room;
    }

    /// <summary>等全部已开的链路结束（逐链路结果看 <see cref="Peers"/>）。</summary>
    public Task WhenAllLinksSettledAsync() => _fanOut.WhenAllLinksSettledAsync();

    private async Task AcceptLoopAsync(
        FanOutSignalingClient signaling,
        IReadOnlyList<string> iceServers,
        Task? until,
        CancellationToken cancellationToken)
    {
        using var loopCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stop = until ?? Task.Delay(Timeout.Infinite, loopCancellation.Token);

        while (!cancellationToken.IsCancellationRequested)
        {
            var arrival = signaling.WaitForReceiverAsync(loopCancellation.Token);
            var finished = await Task.WhenAny(arrival, stop).ConfigureAwait(false);

            if (finished != arrival)
            {
                loopCancellation.Cancel();   // 放掉挂着的 WaitForReceiverAsync
                break;
            }

            string? peerId;
            try
            {
                peerId = await arrival.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (peerId is null)
            {
                break;   // 信令连接关闭
            }

            // 每个接收方一条独立链路；建连失败只影响它自己
            _ = Task.Run(
                () => OpenLinkSafeAsync(signaling, peerId, iceServers, cancellationToken),
                CancellationToken.None);
        }
    }

    private async Task OpenLinkSafeAsync(
        FanOutSignalingClient signaling,
        string peerId,
        IReadOnlyList<string> iceServers,
        CancellationToken cancellationToken)
    {
        WebRtcPeerConnection? peer = null;
        ProtocolConnection? connection = null;

        try
        {
            var effective = iceServers.Count > 0 ? iceServers : _options.IceServers;
            peer = new WebRtcPeerConnection(new WebRtcOptions { IceServers = effective });

            peer.LocalDescriptionReady += description =>
                _ = signaling.SendDescriptionAsync(peerId, description, cancellationToken);
            peer.LocalCandidateReady += candidate =>
                _ = signaling.SendCandidateAsync(peerId, candidate, cancellationToken);

            // 两个处理器都挂好了才开闸（V1 教训：窗口期的信令不能丢）
            signaling.BeginSignalDelivery(peerId, peer.SetRemoteDescription, peer.AddRemoteCandidate);

            var channel = peer.CreateDataChannel();
            await channel.WaitForOpenAsync(cancellationToken).ConfigureAwait(false);

            connection = new ProtocolConnection(channel);
            var runtime = new PeerRuntime(peer, connection);
            _peers[peerId] = runtime;

            var progress = new SnapshotRelay(this, runtime);
            await _fanOut.RunLinkAsync(peerId, connection, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 建连阶段的失败没进 SendFanOut 的快照，这里补一条
            PeerStatusChanged?.Invoke(new FanOutPeerStatus(
                peerId, FanOutLinkState.Failed, default, null, ex));
        }
        finally
        {
            _peers.TryRemove(peerId, out _);
            signaling.ForgetPeer(peerId);

            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }

            if (peer is not null)
            {
                await peer.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private void OnReceiverLeft(string peerId)
    {
        // 刻意什么都不删：链路层面的关闭由通道断开自己触发（SendSession
        // 失败并把 Failed 进快照），已完成的链路快照要留给 UI 展示
        // 「谁收完了、谁中途走了」。重连回来的 peer 拿的是新 peerId（AD-16），
        // 不会与旧记录冲突。宿主想清掉历史可自行调 SendFanOut.ForgetLink。
    }

    private static CandidatePairKind? ProbeKind(PeerRuntime runtime)
    {
        try
        {
            var kind = runtime.Peer.GetCandidatePairKind();
            runtime.LastKnownKind = kind;
            return kind;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            return runtime.LastKnownKind;
        }
    }

    private sealed class SnapshotRelay(FanOutSender owner, PeerRuntime runtime)
        : IProgress<FanOutLinkSnapshot>
    {
        public void Report(FanOutLinkSnapshot value) =>
            owner.PeerStatusChanged?.Invoke(new FanOutPeerStatus(
                value.PeerId, value.State, value.Progress, ProbeKind(runtime), value.Error));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _fanOut.Dispose();

        foreach (var (_, runtime) in _peers)
        {
            await runtime.Connection.DisposeAsync().ConfigureAwait(false);
            await runtime.Peer.DisposeAsync().ConfigureAwait(false);
        }

        _peers.Clear();

        if (_signaling is not null)
        {
            await _signaling.DisposeAsync().ConfigureAwait(false);
        }
    }
}
