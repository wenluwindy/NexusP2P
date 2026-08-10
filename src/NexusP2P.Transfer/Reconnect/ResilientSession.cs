using NexusP2P.Transfer.Protocol;

namespace NexusP2P.Transfer.Reconnect;

/// <summary>
/// 把一次会话跑在自动重连之上。
///
/// <para>按 AD-7，重连<b>不做 ICE restart 也不做 SDP 重协商</b> ——
/// 每次重试都建一条全新的连接。续传的锚点是接收端的位图而不是连接身份，
/// 所以「维持连接」本身没有价值，而重协商的复杂度是实打实的。</para>
///
/// <para>这一层与传输实现无关：<paramref name="connect"/> 想返回内存管道还是
/// 真实 WebRTC 都行。所以重连逻辑能在完全没有网络的情况下测透（AD-1）。</para>
/// </summary>
public static class ResilientSession
{
    /// <summary>
    /// 反复尝试 <paramref name="session"/>，直到成功、遇到不可重试的失败、
    /// 或重试次数用尽。
    /// </summary>
    /// <param name="connect">建一条新连接。每次尝试都会调用一次。</param>
    /// <param name="session">在给定连接上跑一次完整会话。</param>
    /// <param name="status">
    /// 重连状态。回调<b>必须线程安全</b>（<see cref="Progress{T}"/> 会并发投递）。
    /// </param>
    /// <exception cref="ReconnectExhaustedException">自动重试用尽，需手动重连。</exception>
    public static async Task<TResult> RunAsync<TResult>(
        Func<CancellationToken, Task<ProtocolConnection>> connect,
        Func<ProtocolConnection, CancellationToken, Task<TResult>> session,
        ReconnectPolicy? policy = null,
        IProgress<ReconnectStatus>? status = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connect);
        ArgumentNullException.ThrowIfNull(session);

        var effective = policy ?? ReconnectPolicy.Default;
        Exception? lastFailure = null;

        // attempt 0 是首次尝试，1..MaxAttempts 是重试
        for (var attempt = 0; attempt <= effective.MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (attempt > 0)
            {
                var delay = effective.DelayBefore(attempt);

                status?.Report(new ReconnectStatus(
                    ReconnectPhase.WaitingBeforeRetry,
                    attempt,
                    effective.MaxAttempts,
                    delay,
                    lastFailure?.Message));

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            status?.Report(new ReconnectStatus(
                ReconnectPhase.Connecting, attempt, effective.MaxAttempts, TimeSpan.Zero, null));

            ProtocolConnection? connection = null;
            try
            {
                connection = await connect(cancellationToken).ConfigureAwait(false);

                status?.Report(new ReconnectStatus(
                    ReconnectPhase.Running, attempt, effective.MaxAttempts, TimeSpan.Zero, null));

                return await session(connection, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 用户取消要立刻停，不重试
                throw;
            }
            catch (Exception ex)
            {
                lastFailure = ex;

                if (!ReconnectPolicy.IsRetryable(ex))
                {
                    // 换连接改变不了结果，原样抛出让用户看到真正的原因
                    throw;
                }
            }
            finally
            {
                if (connection is not null)
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        status?.Report(new ReconnectStatus(
            ReconnectPhase.GaveUp,
            effective.MaxAttempts,
            effective.MaxAttempts,
            TimeSpan.Zero,
            lastFailure?.Message));

        throw new ReconnectExhaustedException(
            effective.MaxAttempts,
            lastFailure ?? new InvalidOperationException("重试用尽但没有记录到失败原因。"));
    }
}
