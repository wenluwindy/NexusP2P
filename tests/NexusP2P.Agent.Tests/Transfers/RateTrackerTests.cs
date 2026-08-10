using NexusP2P.Agent.Transfers;

namespace NexusP2P.Agent.Tests.Transfers;

public sealed class RateTrackerTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-08-08T12:00:00Z");

    [Fact]
    public void 样本不足时速率为零()
    {
        var tracker = new RateTracker();

        Assert.Equal(0, tracker.BytesPerSecond(Start));

        tracker.Record(1000, Start);
        Assert.Equal(0, tracker.BytesPerSecond(Start));
    }

    [Fact]
    public void 稳定传输下速率接近真实值()
    {
        var tracker = new RateTracker(TimeSpan.FromSeconds(3));

        // 每 100ms 传 1 MiB -> 10 MiB/s
        long total = 0;
        for (var i = 1; i <= 20; i++)
        {
            total += 1024 * 1024;
            tracker.Record(total, Start.AddMilliseconds(i * 100));
        }

        var rate = tracker.BytesPerSecond(Start.AddMilliseconds(2000));

        Assert.InRange(rate, 9.0 * 1024 * 1024, 11.5 * 1024 * 1024);
    }

    [Fact]
    public void 早期的慢启动不会永久拖住速率()
    {
        // 这是用滑动窗口而不是「总字节/总耗时」的全部理由：
        // 后者会让用户看到的数字永远追不上实际速度，剩余时间也一直是错的。
        var tracker = new RateTracker(TimeSpan.FromSeconds(2));

        long total = 0;

        // 前 10 秒极慢：每秒 100 KiB
        for (var i = 1; i <= 10; i++)
        {
            total += 100 * 1024;
            tracker.Record(total, Start.AddSeconds(i));
        }

        // 之后提速到每 100ms 传 1 MiB
        for (var i = 1; i <= 20; i++)
        {
            total += 1024 * 1024;
            tracker.Record(total, Start.AddSeconds(10).AddMilliseconds(i * 100));
        }

        var rate = tracker.BytesPerSecond(Start.AddSeconds(12));

        // 若用总量/总耗时，这里只会得到约 1 MiB/s
        Assert.True(rate > 5.0 * 1024 * 1024,
            $"滑动窗口应反映当前速度，实际只有 {rate / 1024 / 1024:N1} MiB/s");
    }

    [Fact]
    public void 窗口滑过后旧样本被丢弃()
    {
        var tracker = new RateTracker(TimeSpan.FromSeconds(1));

        tracker.Record(1024 * 1024, Start);
        tracker.Record(2 * 1024 * 1024, Start.AddMilliseconds(500));

        // 5 秒后窗口里已经没有样本了
        Assert.Equal(0, tracker.BytesPerSecond(Start.AddSeconds(5)));
    }

    [Fact]
    public void 累计值变小时重置而不是算出负速率()
    {
        // 续传时新会话的累计值从 0 重新计，不能因此得到负数
        var tracker = new RateTracker();

        tracker.Record(10_000_000, Start);
        tracker.Record(12_000_000, Start.AddSeconds(1));

        tracker.Record(0, Start.AddSeconds(2));           // 新会话
        tracker.Record(1_000_000, Start.AddSeconds(3));
        tracker.Record(2_000_000, Start.AddSeconds(4));

        var rate = tracker.BytesPerSecond(Start.AddSeconds(4));

        Assert.True(rate >= 0, $"速率不该为负，实际 {rate}");
    }

    [Fact]
    public void Reset_后从零开始()
    {
        var tracker = new RateTracker();
        tracker.Record(1_000_000, Start);
        tracker.Record(2_000_000, Start.AddSeconds(1));

        tracker.Reset();

        Assert.Equal(0, tracker.BytesPerSecond(Start.AddSeconds(1)));
    }

    [Fact]
    public void 并发记录不会崩()
    {
        var tracker = new RateTracker();

        Parallel.For(0, 200, i =>
        {
            tracker.Record(i * 1000L, Start.AddMilliseconds(i));
            _ = tracker.BytesPerSecond(Start.AddMilliseconds(i));
        });
    }
}
