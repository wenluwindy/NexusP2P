using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Options;

namespace NexusP2P.Signaling.RateLimiting;

/// <summary>
/// 按 IP 限制入房尝试频率。
///
/// <para><b>为什么必须有</b>：文件码是九位十进制、不过期、且是唯一的访问凭证。
/// 不限速的话，一个脚本每秒试几千次就能在可接受的时间里扫出活跃房间。
/// 限速把这件事的成本推高到不值得做。</para>
///
/// <para>算的是<b>全部</b>入房尝试而不只是失败的：只算失败会给攻击者留一个
/// 「先成功一次刷新配额」的漏子，而正常用户一分钟内也不会入房二十次。</para>
///
/// <para>用滑动窗口而不是固定窗口：固定窗口在边界处允许两倍突发，
/// 而这里的目的就是压住突发。</para>
/// </summary>
public sealed class JoinRateLimiter(IOptions<SignalingOptions> options, TimeProvider timeProvider)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>跟踪的 IP 数上限。防止用大量伪造源地址把内存吃光。</summary>
    private const int MaxTrackedAddresses = 10_000;

    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _attempts = new();
    private readonly Lock _sweepGate = new();

    private int Limit => options.Value.JoinAttemptsPerMinute;

    /// <summary>记一次尝试。返回 false 表示超出限额。</summary>
    public bool TryRecordAttempt(IPAddress? address)
    {
        var key = Normalize(address);
        var now = timeProvider.GetUtcNow();
        var cutoff = now - Window;

        if (_attempts.Count > MaxTrackedAddresses)
        {
            SweepStale(cutoff);
        }

        var queue = _attempts.GetOrAdd(key, static _ => new Queue<DateTimeOffset>());

        lock (queue)
        {
            while (queue.Count > 0 && queue.Peek() < cutoff)
            {
                queue.Dequeue();
            }

            if (queue.Count >= Limit)
            {
                return false;
            }

            queue.Enqueue(now);
            return true;
        }
    }

    /// <summary>某个 IP 当前窗口内还剩多少次。用于诊断与响应头。</summary>
    public int Remaining(IPAddress? address)
    {
        var cutoff = timeProvider.GetUtcNow() - Window;

        if (!_attempts.TryGetValue(Normalize(address), out var queue))
        {
            return Limit;
        }

        lock (queue)
        {
            var live = queue.Count(timestamp => timestamp >= cutoff);
            return Math.Max(0, Limit - live);
        }
    }

    /// <summary>
    /// 拿不到 IP 时归到一个共享桶。这会让这些请求互相挤占配额，
    /// 但比「拿不到 IP 就不限速」安全 —— 后者等于给攻击者一条绕过路径。
    /// </summary>
    private static string Normalize(IPAddress? address) => address?.ToString() ?? "<unknown>";

    private void SweepStale(DateTimeOffset cutoff)
    {
        // 只让一个线程清理，其余照常放行
        if (!_sweepGate.TryEnter())
        {
            return;
        }

        try
        {
            foreach (var (key, queue) in _attempts)
            {
                bool empty;
                lock (queue)
                {
                    while (queue.Count > 0 && queue.Peek() < cutoff)
                    {
                        queue.Dequeue();
                    }

                    empty = queue.Count == 0;
                }

                if (empty)
                {
                    _attempts.TryRemove(key, out _);
                }
            }
        }
        finally
        {
            _sweepGate.Exit();
        }
    }
}
