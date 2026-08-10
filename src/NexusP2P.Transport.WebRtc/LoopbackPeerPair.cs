using NexusP2P.Transport.Abstractions;

namespace NexusP2P.Transport.WebRtc;

/// <summary>
/// 把两个 <see cref="WebRtcPeerConnection"/> 在同一进程里直接对接起来。
///
/// <para><b>这是真实的 WebRTC</b>：真 DTLS 握手、真 SCTP、真 ICE 候选交换，
/// 只是把信令消息在内存里直接转交而不经过服务器。所以它能验证
/// 「协议在真实传输上跑得通」，而不需要任何网络基础设施。</para>
///
/// <para>放在产品项目而不是测试项目里，是因为它对**手工联调**也有用 ——
/// 想快速验证一处改动而不启服务器时，这是最短路径。</para>
/// </summary>
public sealed class LoopbackPeerPair : IAsyncDisposable
{
    private readonly WebRtcPeerConnection _offerer;
    private readonly WebRtcPeerConnection _answerer;

    private LoopbackPeerPair(WebRtcPeerConnection offerer, WebRtcPeerConnection answerer)
    {
        _offerer = offerer;
        _answerer = answerer;
    }

    /// <summary>发起方的通道（对应产品里的 exe 发送方）。</summary>
    public IDataChannel Offerer { get; private set; } = null!;

    /// <summary>应答方的通道。</summary>
    public IDataChannel Answerer { get; private set; } = null!;

    /// <summary>
    /// 建立一对已连通的通道。返回时两端都已 Open。
    /// </summary>
    public static async Task<LoopbackPeerPair> ConnectAsync(
        WebRtcOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var effective = options ?? WebRtcOptions.Default;

        var offerer = new WebRtcPeerConnection(effective);
        var answerer = new WebRtcPeerConnection(effective);
        var pair = new LoopbackPeerPair(offerer, answerer);

        try
        {
            // 把信令直接转给对面。真实部署里这两个回调会走信令服务器。
            offerer.LocalDescriptionReady += description => answerer.SetRemoteDescription(description);
            answerer.LocalDescriptionReady += description => offerer.SetRemoteDescription(description);
            offerer.LocalCandidateReady += candidate => answerer.AddRemoteCandidate(candidate);
            answerer.LocalCandidateReady += candidate => offerer.AddRemoteCandidate(candidate);

            var incoming = answerer.WaitForIncomingChannelAsync(cancellationToken);

            // 建通道会触发协商，所以必须在信令转发挂好之后
            var offererChannel = offerer.CreateDataChannel();

            var answererChannel = await incoming.ConfigureAwait(false);

            await Task.WhenAll(
                    offererChannel.WaitForOpenAsync(cancellationToken),
                    answererChannel.WaitForOpenAsync(cancellationToken))
                .ConfigureAwait(false);

            pair.Offerer = offererChannel;
            pair.Answerer = answererChannel;
            return pair;
        }
        catch
        {
            await pair.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>发起方选中的候选对类型。回环下应当是 Host。</summary>
    public CandidatePairKind OffererCandidateKind => _offerer.GetCandidatePairKind();

    public async ValueTask DisposeAsync()
    {
        // PeerConnection 的释放会连带释放它建的通道
        await _offerer.DisposeAsync().ConfigureAwait(false);
        await _answerer.DisposeAsync().ConfigureAwait(false);
    }
}
