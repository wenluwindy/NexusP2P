using System.Runtime.InteropServices;
using DataChannelDotnet.Bindings;

namespace NexusP2P.Transport.WebRtc.Interop;

/// <summary>
/// libdatachannel C API 的类型化包装：负责字符串编解码与错误码检查。
///
/// <para><b>为什么直接用 <c>Bindings.Rtc</c> 而不用 DataChannelDotnet 的托管封装</b>
/// （见 ADR-001）：那层封装没有暴露 <c>BufferedAmount</c>，也没有低水位回调，
/// 而背压是 <see cref="Abstractions.IDataChannel"/> 的硬要求 ——
/// SIPSorcery 的 spike 已经证明它不是理论风险。我们本来就有自己的抽象，
/// 中间再夹一层别人的封装只是多一层可能漏功能的转译。</para>
///
/// <para>引用 <c>DataChannelDotnet</c> 包是为了两件事：拿到自动生成的绑定，
/// 以及让预编译的 <c>datachannel.dll</c> 被复制到输出目录。</para>
/// </summary>
internal static unsafe class NativeRtc
{
    /// <summary>SDP 与候选串的缓冲区大小。SDP 一般几 KB，给足余量。</summary>
    private const int TextBufferSize = 16 * 1024;

    public static void Preload() => Rtc.rtcPreload();

    public static void Cleanup() => Rtc.rtcCleanup();

    // ---- PeerConnection ----

    public static int CreatePeerConnection(
        IReadOnlyList<string> iceServers,
        int maxMessageSize,
        ushort portRangeBegin,
        ushort portRangeEnd,
        bool forceRelay)
    {
        // ICE 服务器要传 char** —— 逐个分配非托管字符串，用完全部释放
        var pointers = stackalloc nint[Math.Max(iceServers.Count, 1)];
        for (var i = 0; i < iceServers.Count; i++)
        {
            pointers[i] = Marshal.StringToCoTaskMemUTF8(iceServers[i]);
        }

        try
        {
            var config = new rtcConfiguration
            {
                iceServers = (sbyte**)pointers,
                iceServersCount = iceServers.Count,
                certificateType = rtcCertificateType.RTC_CERTIFICATE_DEFAULT,
                iceTransportPolicy = forceRelay
                    ? rtcTransportPolicy.RTC_TRANSPORT_POLICY_RELAY
                    : rtcTransportPolicy.RTC_TRANSPORT_POLICY_ALL,
                enableIceTcp = 1,
                disableAutoNegotiation = 1,   // 我们自己控制何时生成 offer
                portRangeBegin = portRangeBegin,
                portRangeEnd = portRangeEnd,
                maxMessageSize = maxMessageSize,
            };

            return Check(Rtc.rtcCreatePeerConnection(&config), nameof(Rtc.rtcCreatePeerConnection));
        }
        finally
        {
            for (var i = 0; i < iceServers.Count; i++)
            {
                Marshal.FreeCoTaskMem(pointers[i]);
            }
        }
    }

    // 清理路径上的返回码显式丢弃：拆连接时拿到错误码也无事可做，
    // 而为它抛异常会掩盖真正触发清理的那个异常。
    public static void DeletePeerConnection(int pc) => _ = Rtc.rtcDeletePeerConnection(pc);

    public static void ClosePeerConnection(int pc) => _ = Rtc.rtcClosePeerConnection(pc);

    /// <summary>生成本地描述。类型传 null 让库自己决定（offer 或 answer）。</summary>
    public static void SetLocalDescription(int pc, string? type)
    {
        if (type is null)
        {
            Check(Rtc.rtcSetLocalDescription(pc, null), nameof(Rtc.rtcSetLocalDescription));
            return;
        }

        var typePtr = Marshal.StringToCoTaskMemUTF8(type);
        try
        {
            Check(Rtc.rtcSetLocalDescription(pc, (sbyte*)typePtr), nameof(Rtc.rtcSetLocalDescription));
        }
        finally
        {
            Marshal.FreeCoTaskMem(typePtr);
        }
    }

    public static void SetRemoteDescription(int pc, string sdp, string type)
    {
        var sdpPtr = Marshal.StringToCoTaskMemUTF8(sdp);
        var typePtr = Marshal.StringToCoTaskMemUTF8(type);
        try
        {
            Check(
                Rtc.rtcSetRemoteDescription(pc, (sbyte*)sdpPtr, (sbyte*)typePtr),
                nameof(Rtc.rtcSetRemoteDescription));
        }
        finally
        {
            Marshal.FreeCoTaskMem(sdpPtr);
            Marshal.FreeCoTaskMem(typePtr);
        }
    }

    public static void AddRemoteCandidate(int pc, string candidate, string? mid)
    {
        var candidatePtr = Marshal.StringToCoTaskMemUTF8(candidate);
        var midPtr = mid is null ? nint.Zero : Marshal.StringToCoTaskMemUTF8(mid);
        try
        {
            // 候选加不进去不是致命错误：对端可能发来我们这边用不上的候选，
            // 或者在 remote description 之前就到了。ICE 本身会重试，所以丢弃返回码。
            _ = Rtc.rtcAddRemoteCandidate(pc, (sbyte*)candidatePtr, (sbyte*)midPtr);
        }
        finally
        {
            Marshal.FreeCoTaskMem(candidatePtr);
            if (midPtr != nint.Zero)
            {
                Marshal.FreeCoTaskMem(midPtr);
            }
        }
    }

    /// <summary>当前选中的候选对。用于判断走的是直连还是中继（瓶颈显示要用）。</summary>
    public static (string? Local, string? Remote) GetSelectedCandidatePair(int pc)
    {
        var local = stackalloc sbyte[TextBufferSize];
        var remote = stackalloc sbyte[TextBufferSize];

        var result = Rtc.rtcGetSelectedCandidatePair(pc, local, TextBufferSize, remote, TextBufferSize);
        return result < 0
            ? (null, null)
            : (Marshal.PtrToStringUTF8((nint)local), Marshal.PtrToStringUTF8((nint)remote));
    }

    // ---- DataChannel ----

    public static int CreateDataChannel(int pc, string label)
    {
        var labelPtr = Marshal.StringToCoTaskMemUTF8(label);
        try
        {
            return Check(
                Rtc.rtcCreateDataChannel(pc, (sbyte*)labelPtr), nameof(Rtc.rtcCreateDataChannel));
        }
        finally
        {
            Marshal.FreeCoTaskMem(labelPtr);
        }
    }

    public static void DeleteDataChannel(int dc) => _ = Rtc.rtcDeleteDataChannel(dc);

    public static void Close(int id) => _ = Rtc.rtcClose(id);

    public static bool IsClosed(int id) => Rtc.rtcIsClosed(id) != 0;

    /// <summary>
    /// 发一条二进制消息。<c>size &gt;= 0</c> 表示二进制，
    /// <c>size &lt; 0</c> 在 C API 里表示「以 null 结尾的文本」—— 我们只发二进制。
    /// </summary>
    public static void SendMessage(int dc, ReadOnlySpan<byte> data)
    {
        fixed (byte* pointer = data)
        {
            var result = Rtc.rtcSendMessage(dc, (sbyte*)pointer, data.Length);
            if (result < 0)
            {
                throw new WebRtcException($"rtcSendMessage 失败，错误码 {result}。");
            }
        }
    }

    public static long GetBufferedAmount(int dc)
    {
        var value = Rtc.rtcGetBufferedAmount(dc);
        return value < 0 ? 0 : value;   // 负数是错误码（通道已关等）
    }

    /// <summary>
    /// 设置低水位阈值。失败只意味着拿不到低水位事件，会退化为轮询 ——
    /// 不值得因此让整条连接失败，所以丢弃返回码。
    /// </summary>
    public static void SetBufferedAmountLowThreshold(int dc, int amount) =>
        _ = Rtc.rtcSetBufferedAmountLowThreshold(dc, amount);

    // ---- 用户指针：静态回调回到实例的桥 ----

    public static void SetUserPointer(int id, nint pointer) =>
        Rtc.rtcSetUserPointer(id, (void*)pointer);

    public static nint GetUserPointer(int id) => (nint)Rtc.rtcGetUserPointer(id);

    private static int Check(int result, string operation) =>
        result < 0 ? throw new WebRtcException($"{operation} 失败，错误码 {result}。") : result;
}

/// <summary>libdatachannel 的原生调用失败。</summary>
public sealed class WebRtcException(string message, Exception? inner = null)
    : Exception(message, inner);
