using System.Runtime.InteropServices;
using DataChannelDotnet.Bindings;
using NexusP2P.Transport.WebRtc.Interop;

namespace NexusP2P.Transport.WebRtc;

/// <summary>本地生成的会话描述，要通过信令发给对端。</summary>
public readonly record struct SessionDescription(string Sdp, string Type);

/// <summary>本地收集到的 ICE 候选，要通过信令发给对端。</summary>
public readonly record struct IceCandidate(string Candidate, string? Mid);

/// <summary>选中的候选对类型。瓶颈显示要靠它区分「直连」与「走中继」。</summary>
public enum CandidatePairKind
{
    Unknown,

    /// <summary>同机或同局域网直连。</summary>
    Host,

    /// <summary>打洞成功的公网直连。</summary>
    ServerReflexive,

    /// <summary>走 TURN 中继 —— 速度受服务器上行限制。</summary>
    Relay,
}

/// <summary>
/// libdatachannel 的 PeerConnection 包装：负责信令交换与 ICE，
/// 并把建立好的通道交给上层。
///
/// <para><b>刻意关闭自动协商</b>（<c>disableAutoNegotiation = 1</c>）：
/// 由调用方决定何时生成 offer。自动协商会在创建通道时立刻生成描述，
/// 而我们希望「建通道」和「开始协商」是两个可分开控制的步骤 ——
/// 重连时这个区分很重要。</para>
///
/// <para>生命周期注意事项与 <see cref="WebRtcDataChannel"/> 相同：
/// 原生回调跑在库自己的线程上，释放顺序必须是
/// 清用户指针 → 删原生对象 → 释放 GCHandle。</para>
/// </summary>
public sealed class WebRtcPeerConnection : IAsyncDisposable
{
    private readonly int _pc;
    private readonly GCHandle _self;
    private readonly WebRtcOptions _options;
    private readonly TaskCompletionSource<WebRtcDataChannel> _incomingChannel =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly List<WebRtcDataChannel> _ownedChannels = [];
    private readonly Lock _channelsGate = new();

    private bool _disposed;

    public unsafe WebRtcPeerConnection(WebRtcOptions? options = null)
    {
        _options = options ?? WebRtcOptions.Default;

        RtcRuntime.EnsureInitialized();

        _pc = NativeRtc.CreatePeerConnection(
            _options.IceServers,
            _options.MaxMessageSize,
            _options.PortRangeBegin,
            _options.PortRangeEnd,
            _options.ForceRelay);

        _self = GCHandle.Alloc(this, GCHandleType.Normal);
        NativeRtc.SetUserPointer(_pc, GCHandle.ToIntPtr(_self));

        _ = Rtc.rtcSetLocalDescriptionCallback(_pc, &OnLocalDescriptionThunk);
        _ = Rtc.rtcSetLocalCandidateCallback(_pc, &OnLocalCandidateThunk);
        _ = Rtc.rtcSetStateChangeCallback(_pc, &OnStateChangeThunk);
        _ = Rtc.rtcSetGatheringStateChangeCallback(_pc, &OnGatheringStateThunk);
        _ = Rtc.rtcSetDataChannelCallback(_pc, &OnDataChannelThunk);
    }

    /// <summary>本地描述生成好了，调用方负责通过信令送给对端。</summary>
    public event Action<SessionDescription>? LocalDescriptionReady;

    /// <summary>收集到一个本地候选，调用方负责通过信令送给对端。</summary>
    public event Action<IceCandidate>? LocalCandidateReady;

    /// <summary>连接状态变化。</summary>
    public event Action<rtcState>? StateChanged;

    /// <summary>ICE 候选收集完成。</summary>
    public event Action? GatheringComplete;

    public int NativeId => _pc;

    /// <summary>
    /// 建一条数据通道并触发协商。<b>只有 offerer 调这个</b>；
    /// answerer 应该用 <see cref="WaitForIncomingChannelAsync"/>。
    /// </summary>
    public WebRtcDataChannel CreateDataChannel(string label = "bulk")
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var id = NativeRtc.CreateDataChannel(_pc, label);
        var channel = new WebRtcDataChannel(id, _options.MaxMessageSize, _options.ConnectTimeout);
        Track(channel);

        // 关了自动协商，所以要显式触发。类型传 null 让库自己决定 offer/answer。
        NativeRtc.SetLocalDescription(_pc, null);

        return channel;
    }

    /// <summary>等对端建过来的通道。answerer 用这个。</summary>
    public Task<WebRtcDataChannel> WaitForIncomingChannelAsync(CancellationToken cancellationToken = default) =>
        _incomingChannel.Task.WaitAsync(_options.ConnectTimeout, cancellationToken);

    /// <summary>应用对端的描述。作为 answerer 时会自动生成 answer。</summary>
    public void SetRemoteDescription(SessionDescription description)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        NativeRtc.SetRemoteDescription(_pc, description.Sdp, description.Type);

        // 收到 offer 后要生成 answer。关了自动协商所以显式触发。
        if (string.Equals(description.Type, "offer", StringComparison.OrdinalIgnoreCase))
        {
            NativeRtc.SetLocalDescription(_pc, "answer");
        }
    }

    public void AddRemoteCandidate(IceCandidate candidate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NativeRtc.AddRemoteCandidate(_pc, candidate.Candidate, candidate.Mid);
    }

    /// <summary>
    /// 当前选中的候选对属于哪一类。
    ///
    /// <para>这是「瓶颈说明」的关键输入：走中继意味着速度受服务器上行限制，
    /// 而用户看到慢的时候最需要知道的就是这件事。</para>
    /// </summary>
    public CandidatePairKind GetCandidatePairKind()
    {
        if (_disposed)
        {
            return CandidatePairKind.Unknown;
        }

        var (local, remote) = NativeRtc.GetSelectedCandidatePair(_pc);
        if (local is null || remote is null)
        {
            return CandidatePairKind.Unknown;
        }

        // 只要有一端是 relay，整条路径就是中继
        if (Contains(local, "relay") || Contains(remote, "relay"))
        {
            return CandidatePairKind.Relay;
        }

        if (Contains(local, "srflx") || Contains(remote, "srflx") ||
            Contains(local, "prflx") || Contains(remote, "prflx"))
        {
            return CandidatePairKind.ServerReflexive;
        }

        if (Contains(local, "host") && Contains(remote, "host"))
        {
            return CandidatePairKind.Host;
        }

        return CandidatePairKind.Unknown;

        static bool Contains(string text, string token) =>
            text.Contains(token, StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        // 先清用户指针：之后触发的原生回调会看到 0 并立刻返回。
        // 反序会造成 use-after-free。
        NativeRtc.SetUserPointer(_pc, nint.Zero);
        _disposed = true;

        _incomingChannel.TrySetCanceled();

        WebRtcDataChannel[] channels;
        lock (_channelsGate)
        {
            channels = [.. _ownedChannels];
            _ownedChannels.Clear();
        }

        // 通道必须先于 PeerConnection 释放
        foreach (var channel in channels)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }

        NativeRtc.ClosePeerConnection(_pc);
        NativeRtc.DeletePeerConnection(_pc);

        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    private void Track(WebRtcDataChannel channel)
    {
        lock (_channelsGate)
        {
            _ownedChannels.Add(channel);
        }
    }

    // ---- 原生回调 ----

    private void HandleLocalDescription(string? sdp, string? type)
    {
        if (sdp is not null && type is not null)
        {
            LocalDescriptionReady?.Invoke(new SessionDescription(sdp, type));
        }
    }

    private void HandleLocalCandidate(string? candidate, string? mid)
    {
        if (candidate is not null)
        {
            LocalCandidateReady?.Invoke(new IceCandidate(candidate, mid));
        }
    }

    private void HandleStateChange(rtcState state)
    {
        StateChanged?.Invoke(state);

        if (state is rtcState.RTC_FAILED or rtcState.RTC_CLOSED)
        {
            _incomingChannel.TrySetException(
                new WebRtcException($"PeerConnection 进入 {state} 状态。"));
        }
    }

    private void HandleGatheringState(rtcGatheringState state)
    {
        if (state == rtcGatheringState.RTC_GATHERING_COMPLETE)
        {
            GatheringComplete?.Invoke();
        }
    }

    private void HandleIncomingChannel(int channelId)
    {
        var channel = new WebRtcDataChannel(channelId, _options.MaxMessageSize, _options.ConnectTimeout);
        Track(channel);

        if (!_incomingChannel.TrySetResult(channel))
        {
            // 已经有人接过通道了。本协议一次只用一条，多出来的直接释放。
            // 这里在原生回调里，不能 await —— 用同步的 Dispose。
            channel.Dispose();
        }
    }

    // ---- 静态 thunk。异常一律吞掉：穿回原生栈是未定义行为 ----

    private static WebRtcPeerConnection? Resolve(nint userPointer)
    {
        if (userPointer == nint.Zero)
        {
            return null;
        }

        try
        {
            return GCHandle.FromIntPtr(userPointer).Target as WebRtcPeerConnection;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe void OnLocalDescriptionThunk(int pc, sbyte* sdp, sbyte* type, void* userPointer)
    {
        try
        {
            Resolve((nint)userPointer)?.HandleLocalDescription(
                Marshal.PtrToStringUTF8((nint)sdp), Marshal.PtrToStringUTF8((nint)type));
        }
        catch
        {
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe void OnLocalCandidateThunk(int pc, sbyte* candidate, sbyte* mid, void* userPointer)
    {
        try
        {
            Resolve((nint)userPointer)?.HandleLocalCandidate(
                Marshal.PtrToStringUTF8((nint)candidate), Marshal.PtrToStringUTF8((nint)mid));
        }
        catch
        {
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe void OnStateChangeThunk(int pc, rtcState state, void* userPointer)
    {
        try
        {
            Resolve((nint)userPointer)?.HandleStateChange(state);
        }
        catch
        {
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe void OnGatheringStateThunk(int pc, rtcGatheringState state, void* userPointer)
    {
        try
        {
            Resolve((nint)userPointer)?.HandleGatheringState(state);
        }
        catch
        {
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe void OnDataChannelThunk(int pc, int dc, void* userPointer)
    {
        try
        {
            Resolve((nint)userPointer)?.HandleIncomingChannel(dc);
        }
        catch
        {
        }
    }
}
