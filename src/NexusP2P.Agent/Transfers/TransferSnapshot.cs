using NexusP2P.Transfer.Reconnect;
using NexusP2P.Transport.WebRtc;

namespace NexusP2P.Agent.Transfers;

/// <summary>一次传输所处的阶段。UI 直接照着它显示。</summary>
public enum TransferPhase
{
    /// <summary>正在算校验和（大文件夹可能要一会儿）。</summary>
    Preparing,

    /// <summary>等对方输码进来。</summary>
    WaitingForPeer,

    /// <summary>正在建立连接。</summary>
    Connecting,

    /// <summary>正在传。</summary>
    Transferring,

    /// <summary>正在做落盘后的整体校验。</summary>
    Verifying,

    /// <summary>完成。</summary>
    Completed,

    /// <summary>失败，且自动重连已用尽 —— 等用户手动重连。</summary>
    Failed,

    /// <summary>用户取消。</summary>
    Cancelled,
}

/// <summary>
/// 当前的瓶颈在哪。
///
/// <para>这是刻意的产品决定：用户看到 3 MB/s 时第一反应是「是不是坏了」，
/// 应该直接告诉他为什么，而不是让他对着一个数字猜。</para>
/// </summary>
public enum Bottleneck
{
    Unknown,

    /// <summary>正在算校验和，还没开始传。</summary>
    Hashing,

    /// <summary>走中继中 —— 速度受服务器上行限制。</summary>
    Relay,

    /// <summary>直连，瓶颈是双方的物理带宽 —— 这就是最快了。</summary>
    DirectLink,

    /// <summary>对端处理不过来，发送缓冲堆积。</summary>
    PeerBackpressure,

    /// <summary>正在重连。</summary>
    Reconnecting,
}

/// <summary>传输状态的一次快照。UI 轮询它来刷新界面。</summary>
public sealed record TransferSnapshot
{
    public required string Id { get; init; }

    public required bool IsSending { get; init; }

    public required TransferPhase Phase { get; init; }

    /// <summary>文件码，形如 <c>111-111-111</c>。发送方建房后才有。</summary>
    public string? Code { get; init; }

    /// <summary>完整分享链接（含密钥）。发送方建房后才有。</summary>
    public string? ShareUrl { get; init; }

    public long CompletedBytes { get; init; }

    public long TotalBytes { get; init; }

    /// <summary>最近一秒的速率（字节/秒）。</summary>
    public double BytesPerSecond { get; init; }

    public Bottleneck Bottleneck { get; init; }

    /// <summary>正在重连时的第几次 / 共几次。UI 显示「正在重连 2/3」。</summary>
    public int ReconnectAttempt { get; init; }

    public int ReconnectMaxAttempts { get; init; }

    /// <summary>失败原因。面向用户，要能直接显示。</summary>
    public string? Error { get; init; }

    /// <summary>落地的文件路径。完成后才有。</summary>
    public IReadOnlyList<string> LandedFiles { get; init; } = [];

    public double Fraction => TotalBytes == 0 ? 0 : Math.Min(1.0, (double)CompletedBytes / TotalBytes);

    /// <summary>剩余时间估算。速率为 0 或已完成时为 null。</summary>
    public TimeSpan? Remaining
    {
        get
        {
            if (BytesPerSecond <= 0 || CompletedBytes >= TotalBytes)
            {
                return null;
            }

            return TimeSpan.FromSeconds((TotalBytes - CompletedBytes) / BytesPerSecond);
        }
    }

    /// <summary>把候选对类型翻译成瓶颈判断。</summary>
    public static Bottleneck FromCandidatePair(CandidatePairKind kind) => kind switch
    {
        CandidatePairKind.Relay => Bottleneck.Relay,
        CandidatePairKind.Host or CandidatePairKind.ServerReflexive => Bottleneck.DirectLink,
        _ => Bottleneck.Unknown,
    };

    /// <summary>把重连状态翻译成快照字段。</summary>
    public TransferSnapshot WithReconnect(ReconnectStatus status) => this with
    {
        Phase = status.Phase == ReconnectPhase.GaveUp ? TransferPhase.Failed : TransferPhase.Connecting,
        Bottleneck = Bottleneck.Reconnecting,
        ReconnectAttempt = status.Attempt,
        ReconnectMaxAttempts = status.MaxAttempts,
        Error = status.Reason,
    };
}
