using NexusP2P.Core.Manifest;
using NexusP2P.Transfer.Protocol;
using NexusP2P.Transfer.Storage;
using NexusP2P.Transport.Abstractions;

namespace NexusP2P.Transfer.Reconnect;

/// <summary>
/// 重连策略（AD-7）：自动重试 <see cref="MaxAttempts"/> 次，指数退避，之后转手动。
///
/// <para><b>最容易做错的地方是「什么该重试」。</b>把「文件码不对」重试三次
/// 只是白等七秒再报同一个错，反而让用户以为是网络问题。所以
/// <see cref="IsRetryable"/> 只对<b>传输层</b>的失败点头。</para>
/// </summary>
public sealed record ReconnectPolicy
{
    public static ReconnectPolicy Default { get; } = new();

    /// <summary>自动重试次数。AD-7 定为 3。</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>第一次退避的时长。</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>退避倍数。</summary>
    public double BackoffFactor { get; init; } = 2.0;

    /// <summary>退避上限。</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>第 <paramref name="attempt"/> 次重试前要等多久（attempt 从 1 开始）。</summary>
    public TimeSpan DelayBefore(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);

        var multiplier = Math.Pow(BackoffFactor, attempt - 1);
        var milliseconds = InitialDelay.TotalMilliseconds * multiplier;

        return milliseconds >= MaxDelay.TotalMilliseconds
            ? MaxDelay
            : TimeSpan.FromMilliseconds(milliseconds);
    }

    /// <summary>
    /// 这个失败值不值得重连。
    ///
    /// <para><b>可重试</b>：通道断开、IO 抖动 —— 换一条连接就有希望。</para>
    ///
    /// <para><b>不可重试</b>：文件码不对、清单含非法路径、磁盘满、目标不可写、
    /// 对端违反协议、分片反复校验失败 —— 这些换一百条连接结果都一样，
    /// 重试只会推迟用户看到真正原因的时间。</para>
    /// </summary>
    public static bool IsRetryable(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            // 用户主动取消不是失败
            OperationCanceledException => false,

            // 异常自己知道答案时以它为准 —— 见 IRetryableFailure
            IRetryableFailure failure => failure.IsRetryable,

            // 内容或环境的问题，重连改变不了
            UnsafePathException => false,
            InvalidManifestException => false,
            InsufficientDiskSpaceException => false,
            IntegrityException => false,

            TransferFailedException failed => failed.Code switch
            {
                TransferErrorCode.InvalidManifest => false,
                TransferErrorCode.InsufficientDiskSpace => false,
                TransferErrorCode.DestinationNotWritable => false,
                TransferErrorCode.ProtocolViolation => false,
                TransferErrorCode.PieceVerificationFailed => false,
                TransferErrorCode.Cancelled => false,
                _ => true,
            },

            // 传输层断开：正是重连要解决的情况
            DataChannelClosedException => true,
            ProtocolException => true,
            IOException => true,

            // 建连接超时。**拔掉网线十秒就是这个样子** ——
            // 不重试的话，AD-7 承诺的「自动重连 3 次」在最典型的场景下
            // 一次都不会发生。
            TimeoutException => true,

            // 不认识的异常不重试。盲目重连一个未知故障
            // 只会把真正的原因埋在三次重试之后。
            _ => false,
        };
    }
}

/// <summary>重连过程处于哪个阶段。</summary>
public enum ReconnectPhase
{
    /// <summary>正在建立连接。</summary>
    Connecting,

    /// <summary>连接已建立，传输进行中。</summary>
    Running,

    /// <summary>断开了，正在退避等待下一次重试。</summary>
    WaitingBeforeRetry,

    /// <summary>自动重试用尽，等待用户手动重连。</summary>
    GaveUp,
}

/// <summary>
/// 重连状态。UI 据此显示「正在重连 2/3」——
/// 自动重试必须是<b>可见的</b>，否则 3 次重试会把「网络确实不通」这件事
/// 静默推迟好几秒，用户只看到一个卡住的进度条。
/// </summary>
public readonly record struct ReconnectStatus(
    ReconnectPhase Phase,
    int Attempt,
    int MaxAttempts,
    TimeSpan Delay,
    string? Reason);

/// <summary>
/// 自动重连次数用尽，需要用户手动重连。
/// <see cref="LastFailure"/> 是最后一次的真实原因，UI 应该把它显示出来。
/// </summary>
public sealed class ReconnectExhaustedException(int attempts, Exception lastFailure)
    : Exception($"自动重连 {attempts} 次后仍未成功，需要手动重连。最后一次的原因：{lastFailure.Message}",
        lastFailure)
{
    public int Attempts { get; } = attempts;

    public Exception LastFailure { get; } = lastFailure;
}
