using System.Net;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NexusP2P.Signaling;
using NexusP2P.Signaling.RateLimiting;

namespace NexusP2P.Integration.Tests.Signaling;

public sealed class JoinRateLimiterTests
{
    private static (JoinRateLimiter Limiter, FakeTimeProvider Clock) Create(int perMinute = 20)
    {
        var options = Options.Create(new SignalingOptions
        {
            PublicOrigin = "https://p2p.example.com",
            JoinAttemptsPerMinute = perMinute,
        });

        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-07T12:00:00Z"));
        return (new JoinRateLimiter(options, clock), clock);
    }

    private static readonly IPAddress Alice = IPAddress.Parse("203.0.113.10");
    private static readonly IPAddress Bob = IPAddress.Parse("203.0.113.11");

    [Fact]
    public void 限额之内全部放行()
    {
        var (limiter, _) = Create(perMinute: 5);

        for (var i = 0; i < 5; i++)
        {
            Assert.True(limiter.TryRecordAttempt(Alice), $"第 {i + 1} 次本应放行");
        }
    }

    [Fact]
    public void 超出限额被拒()
    {
        var (limiter, _) = Create(perMinute: 5);

        for (var i = 0; i < 5; i++)
        {
            limiter.TryRecordAttempt(Alice);
        }

        Assert.False(limiter.TryRecordAttempt(Alice));
    }

    [Fact]
    public void 不同_IP_各自计数()
    {
        var (limiter, _) = Create(perMinute: 3);

        for (var i = 0; i < 3; i++)
        {
            limiter.TryRecordAttempt(Alice);
        }

        Assert.False(limiter.TryRecordAttempt(Alice));
        Assert.True(limiter.TryRecordAttempt(Bob));
    }

    [Fact]
    public void 窗口滑过之后恢复配额()
    {
        var (limiter, clock) = Create(perMinute: 3);

        for (var i = 0; i < 3; i++)
        {
            limiter.TryRecordAttempt(Alice);
        }

        Assert.False(limiter.TryRecordAttempt(Alice));

        clock.Advance(TimeSpan.FromSeconds(61));

        Assert.True(limiter.TryRecordAttempt(Alice));
    }

    [Fact]
    public void 是滑动窗口而不是固定窗口()
    {
        // 固定窗口在边界处允许两倍突发（窗口末尾打满 + 新窗口立刻再打满），
        // 而这里的目的就是压住突发。滑动窗口下配额是逐个恢复的。
        var (limiter, clock) = Create(perMinute: 3);

        limiter.TryRecordAttempt(Alice);          // t=0
        clock.Advance(TimeSpan.FromSeconds(30));
        limiter.TryRecordAttempt(Alice);          // t=30
        limiter.TryRecordAttempt(Alice);          // t=30
        Assert.False(limiter.TryRecordAttempt(Alice));

        // t=61：只有 t=0 那一次滑出窗口，所以只恢复一个名额
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.True(limiter.TryRecordAttempt(Alice));
        Assert.False(limiter.TryRecordAttempt(Alice));
    }

    [Fact]
    public void 拿不到_IP_时归到共享桶而不是不限速()
    {
        // 「拿不到 IP 就放行」等于给攻击者一条绕过路径
        var (limiter, _) = Create(perMinute: 2);

        Assert.True(limiter.TryRecordAttempt(null));
        Assert.True(limiter.TryRecordAttempt(null));
        Assert.False(limiter.TryRecordAttempt(null));
    }

    [Fact]
    public void Remaining_反映剩余配额()
    {
        var (limiter, _) = Create(perMinute: 5);

        Assert.Equal(5, limiter.Remaining(Alice));

        limiter.TryRecordAttempt(Alice);
        limiter.TryRecordAttempt(Alice);

        Assert.Equal(3, limiter.Remaining(Alice));
    }

    [Fact]
    public void Remaining_对没见过的_IP_返回满额()
    {
        var (limiter, _) = Create(perMinute: 7);

        Assert.Equal(7, limiter.Remaining(Bob));
    }

    [Fact]
    public void 成功的尝试也计数()
    {
        // 只算失败会给攻击者留一个「先成功一次刷新配额」的漏子；
        // 而正常用户一分钟内也不会入房二十次
        var (limiter, _) = Create(perMinute: 2);

        Assert.True(limiter.TryRecordAttempt(Alice));
        Assert.True(limiter.TryRecordAttempt(Alice));
        Assert.False(limiter.TryRecordAttempt(Alice));
    }

    [Fact]
    public void 并发调用下计数不超发()
    {
        var (limiter, _) = Create(perMinute: 50);
        var granted = 0;

        Parallel.For(0, 200, _ =>
        {
            if (limiter.TryRecordAttempt(Alice))
            {
                Interlocked.Increment(ref granted);
            }
        });

        Assert.Equal(50, granted);
    }
}
