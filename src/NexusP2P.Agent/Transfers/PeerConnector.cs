using NexusP2P.Agent.Signaling;
using NexusP2P.Transfer.Protocol;
using NexusP2P.Transport.WebRtc;

namespace NexusP2P.Agent.Transfers;

/// <summary>一条建好的对等连接，以及建立它时得到的信息。</summary>
public sealed class PeerLink : IAsyncDisposable
{
    private readonly SignalingClient _signaling;
    private readonly WebRtcPeerConnection _peer;

    internal PeerLink(
        SignalingClient signaling,
        WebRtcPeerConnection peer,
        ProtocolConnection connection,
        string? code,
        string? shareUrlBase)
    {
        _signaling = signaling;
        _peer = peer;
        Connection = connection;
        Code = code;
        ShareUrlBase = shareUrlBase;
    }

    public ProtocolConnection Connection { get; }

    /// <summary>文件码。只有建房方有。</summary>
    public string? Code { get; }

    /// <summary>分享链接基址。只有建房方有。</summary>
    public string? ShareUrlBase { get; }

    /// <summary>当前走的是直连还是中继。「瓶颈说明」要用。</summary>
    public CandidatePairKind CandidateKind => _peer.GetCandidatePairKind();

    public async ValueTask DisposeAsync()
    {
        await Connection.DisposeAsync().ConfigureAwait(false);
        await _peer.DisposeAsync().ConfigureAwait(false);
        await _signaling.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// 把信令与 WebRTC 接起来，产出一条可用的 <see cref="ProtocolConnection"/>。
///
/// <para><b>信令连接在传输开始后就没用了</b>，但这里仍然保持它打开：
/// ICE 可能在连接建立之后继续交换候选（比如网络切换时），
/// 提前关掉会让这些补充候选丢失。</para>
/// </summary>
public static class PeerConnector
{
    /// <summary>
    /// 建房并等对方进来，返回连好的通道。<b>发送方用这个。</b>
    /// </summary>
    /// <param name="onRoomCreated">
    /// 房间建好、拿到文件码时立刻回调 —— UI 要马上把码显示出来，
    /// 而不是等对方进来之后。
    /// </param>
    /// <param name="onPeerArrived">
    /// 对方进房、开始打洞时回调。<b>这两个阶段的等待时间性质完全不同</b> ——
    /// 「还没人来」可以等几小时，「正在打洞」超过十几秒就说明多半连不上了。
    /// 界面上必须能分开，否则用户和排查的人都无从判断卡在哪。
    /// </param>
    public static async Task<PeerLink> OfferAsync(
        AgentOptions options,
        Action<RoomCreated>? onRoomCreated = null,
        Action? onPeerArrived = null,
        CancellationToken cancellationToken = default)
    {
        var signaling = new SignalingClient(options);
        WebRtcPeerConnection? peer = null;

        try
        {
            var room = await signaling.CreateRoomAsync(cancellationToken).ConfigureAwait(false);
            onRoomCreated?.Invoke(room);

            peer = CreatePeer(options, room.IceServers);
            WireSignaling(signaling, peer, cancellationToken);

            // 等对方进房再开始协商：过早生成 offer，信令服务器没有对端可转发，
            // 那条 offer 就白丢了。
            //
            // 用 WaitForPeerAsync 而不是订阅事件：对端拿到码之后立刻就会进来，
            // 而「建房返回」到「挂上事件处理器」之间有一个窗口 ——
            // 订阅式写法会稳定地丢掉这个通知。
            await signaling.WaitForPeerAsync(cancellationToken).ConfigureAwait(false);
            onPeerArrived?.Invoke();

            var channel = peer.CreateDataChannel();
            await channel.WaitForOpenAsync(cancellationToken).ConfigureAwait(false);

            return new PeerLink(signaling, peer, new ProtocolConnection(channel), room.Code, room.ShareUrlBase);
        }
        catch
        {
            if (peer is not null)
            {
                await peer.DisposeAsync().ConfigureAwait(false);
            }

            await signaling.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// 以发送方身份回到一个<b>已经存在</b>的房间并重新发起协商。
    /// <b>断线重连时发送方用这个。</b>
    ///
    /// <para>与 <see cref="OfferAsync"/> 的区别只有一处：不建新房，
    /// 因而<b>不换文件码</b>。重连换码意味着用户得把新码再念一遍，
    /// 而重连本该是无感的。</para>
    ///
    /// <para>房间必须还在宽限期内，否则会得到与「码不存在」相同的失败 ——
    /// 服务端刻意不区分这两者（防枚举）。</para>
    /// </summary>
    public static async Task<PeerLink> ReofferAsync(
        AgentOptions options,
        string code,
        Action? onPeerArrived = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        var signaling = new SignalingClient(options);
        WebRtcPeerConnection? peer = null;

        try
        {
            var iceServers = await signaling.JoinRoomAsync(code, asSender: true, cancellationToken)
                .ConfigureAwait(false);

            peer = CreatePeer(options, iceServers);
            WireSignaling(signaling, peer, cancellationToken);

            // 对方可能已经先回到房间了 —— 那种情况下 peer-joined 早就发过，
            // 靠的是进房应答里的 peerPresent。这一步不会白等。
            await signaling.WaitForPeerAsync(cancellationToken).ConfigureAwait(false);
            onPeerArrived?.Invoke();

            var channel = peer.CreateDataChannel();
            await channel.WaitForOpenAsync(cancellationToken).ConfigureAwait(false);

            return new PeerLink(signaling, peer, new ProtocolConnection(channel), code, shareUrlBase: null);
        }
        catch
        {
            if (peer is not null)
            {
                await peer.DisposeAsync().ConfigureAwait(false);
            }

            await signaling.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>用文件码进房并等对方建通道过来。<b>接收方用这个。</b></summary>
    public static async Task<PeerLink> AnswerAsync(
        AgentOptions options,
        string code,
        CancellationToken cancellationToken = default)
    {
        var signaling = new SignalingClient(options);
        WebRtcPeerConnection? peer = null;

        try
        {
            var iceServers = await signaling.JoinRoomAsync(code, asSender: false, cancellationToken)
                .ConfigureAwait(false);

            peer = CreatePeer(options, iceServers);
            WireSignaling(signaling, peer, cancellationToken);

            var channel = await peer.WaitForIncomingChannelAsync(cancellationToken).ConfigureAwait(false);
            await channel.WaitForOpenAsync(cancellationToken).ConfigureAwait(false);

            return new PeerLink(signaling, peer, new ProtocolConnection(channel), null, null);
        }
        catch
        {
            if (peer is not null)
            {
                await peer.DisposeAsync().ConfigureAwait(false);
            }

            await signaling.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static WebRtcPeerConnection CreatePeer(AgentOptions options, IReadOnlyList<string> iceServers)
    {
        // 服务器下发的 ICE 服务器（含 TURN 时限凭据）优先于本地配置 ——
        // 凭据是有时效的，只能由服务器现算
        var effective = iceServers.Count > 0 ? iceServers : options.IceServers;

        return new WebRtcPeerConnection(new WebRtcOptions { IceServers = effective });
    }

    private static void WireSignaling(
        SignalingClient signaling, WebRtcPeerConnection peer, CancellationToken cancellationToken)
    {
        peer.LocalDescriptionReady += description =>
            _ = signaling.SendDescriptionAsync(description, cancellationToken);

        peer.LocalCandidateReady += candidate =>
            _ = signaling.SendCandidateAsync(candidate, cancellationToken);

        signaling.RemoteDescriptionReceived += peer.SetRemoteDescription;
        signaling.RemoteCandidateReceived += peer.AddRemoteCandidate;

        // 两个处理器都挂好了才开闸，把窗口期攒下的信令补发出来
        signaling.BeginSignalDelivery();
    }
}
