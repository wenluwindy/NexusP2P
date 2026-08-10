using NexusP2P.Agent.Signaling;
using NexusP2P.Transfer.Protocol;
using NexusP2P.Transport.WebRtc;

namespace NexusP2P.Agent.Transfers;

/// <summary>
/// 把 <see cref="PeerConnector"/> 包成一个可以反复重建的连接源，
/// 交给 <c>ResilientSession</c> 用（Task 3.5）。
///
/// <para>按 AD-7，重连<b>不做 ICE restart 也不做 SDP 重协商</b>：
/// 每次都丢掉旧的 PeerConnection 建一条全新的，走同一个房间（宽限期内）。
/// 续传的锚点是接收端的位图，不是连接身份 —— 所以「保住这条连接」没有价值，
/// 而重协商的复杂度是实打实的。</para>
///
/// <para><b>发送端第一次是建房，之后是以 sender 身份回同一个房间。</b>
/// 重新建房会换一个文件码，那意味着用户得把新码再念一遍 ——
/// 断线重连本该是用户完全无感的。</para>
/// </summary>
public sealed class ReconnectingPeerSource : IAsyncDisposable
{
    private readonly AgentOptions _options;
    private readonly bool _isSender;
    private readonly Action<RoomCreated>? _onRoomCreated;
    private readonly Action? _onPeerArrived;

    private string? _code;
    private PeerLink? _current;
    private bool _disposed;

    private ReconnectingPeerSource(
        AgentOptions options,
        bool isSender,
        string? code,
        Action<RoomCreated>? onRoomCreated,
        Action? onPeerArrived)
    {
        _options = options;
        _isSender = isSender;
        _code = code;
        _onRoomCreated = onRoomCreated;
        _onPeerArrived = onPeerArrived;
    }

    /// <summary>
    /// 发送端：首次建房拿码，之后每次重连都回同一个房间。
    /// </summary>
    /// <param name="onRoomCreated">
    /// <b>只在第一次建房时回调一次。</b>重连不会换码，界面不该重复弹码。
    /// </param>
    public static ReconnectingPeerSource ForSender(
        AgentOptions options,
        Action<RoomCreated>? onRoomCreated = null,
        Action? onPeerArrived = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new ReconnectingPeerSource(options, isSender: true, code: null, onRoomCreated, onPeerArrived);
    }

    /// <summary>接收端：每次都用同一个文件码进房。</summary>
    public static ReconnectingPeerSource ForReceiver(AgentOptions options, string code)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        return new ReconnectingPeerSource(options, isSender: false, code, onRoomCreated: null, onPeerArrived: null);
    }

    /// <summary>当前这条连接走的是直连还是中继。没有连接时为 null。</summary>
    public CandidatePairKind? CandidateKind => _current?.CandidateKind;

    /// <summary>文件码。发送端在第一次连上之后才有。</summary>
    public string? Code => _code;

    /// <summary>
    /// 建一条新连接。<b>上一条会先被彻底释放</b> ——
    /// 不释放的话每次重连都会漏掉一个 PeerConnection、一条 WebSocket
    /// 和它们背后的原生线程，而重连恰恰是可能发生很多次的事。
    /// </summary>
    public async Task<ProtocolConnection> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await ReleaseCurrentAsync().ConfigureAwait(false);

        var link = _isSender
            ? await ConnectAsSenderAsync(cancellationToken).ConfigureAwait(false)
            : await PeerConnector.AnswerAsync(_options, _code!, cancellationToken).ConfigureAwait(false);

        _current = link;
        return link.Connection;
    }

    private async Task<PeerLink> ConnectAsSenderAsync(CancellationToken cancellationToken)
    {
        if (_code is null)
        {
            var link = await PeerConnector
                .OfferAsync(
                    _options,
                    room =>
                    {
                        _code = room.Code;
                        _onRoomCreated?.Invoke(room);
                    },
                    _onPeerArrived,
                    cancellationToken)
                .ConfigureAwait(false);

            // 建房回调没跑到就拿不到码，之后的重连会退化成重新建房、换新码。
            // 与其埋着这个坑，不如在这里就暴露出来。
            if (_code is null)
            {
                await link.DisposeAsync().ConfigureAwait(false);
                throw new SignalingException("建房成功但没有拿到文件码，无法支持重连。");
            }

            return link;
        }

        return await PeerConnector
            .ReofferAsync(_options, _code, _onPeerArrived, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ReleaseCurrentAsync()
    {
        if (_current is null)
        {
            return;
        }

        var link = _current;
        _current = null;

        try
        {
            await link.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // 正在释放的本来就是一条已经坏掉的连接，报错没有意义
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await ReleaseCurrentAsync().ConfigureAwait(false);
    }
}
