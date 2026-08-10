using NexusP2P.Transport.Abstractions;

namespace NexusP2P.Transfer.Tests.Transport;

public sealed class InMemoryDataChannelTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>收集对端收到的全部消息。</summary>
    private sealed class Collector
    {
        private readonly List<byte[]> _messages = [];
        private readonly Lock _gate = new();

        public Collector(IDataChannel channel) =>
            channel.MessageReceived += m =>
            {
                lock (_gate)
                {
                    // 契约上回调期间才有效，所以复制 —— 也顺便验证消费方照契约写不会出问题
                    _messages.Add(m.ToArray());
                }
            };

        public IReadOnlyList<byte[]> Messages
        {
            get
            {
                lock (_gate)
                {
                    return [.. _messages];
                }
            }
        }

        public long TotalBytes => Messages.Sum(m => (long)m.Length);

        public async Task WaitForCountAsync(int count)
        {
            var deadline = DateTime.UtcNow + Timeout;
            while (Messages.Count < count)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"等待 {count} 条消息超时，只收到 {Messages.Count} 条。");
                }

                await Task.Delay(5);
            }
        }
    }

    private static byte[] Message(int length, byte fill)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, fill);
        return bytes;
    }

    [Fact]
    public async Task 创建后两端都是_Open()
    {
        await using var pair = InMemoryDataChannelPair.Create();

        Assert.Equal(DataChannelState.Open, pair.Left.State);
        Assert.Equal(DataChannelState.Open, pair.Right.State);

        await pair.Left.WaitForOpenAsync();
        await pair.Right.WaitForOpenAsync();
    }

    [Fact]
    public async Task 消息能从左传到右()
    {
        await using var pair = InMemoryDataChannelPair.Create();
        var collector = new Collector(pair.Right);

        pair.Left.Send([1, 2, 3]);
        await collector.WaitForCountAsync(1);

        Assert.Equal([1, 2, 3], collector.Messages[0]);
    }

    [Fact]
    public async Task 双向都能传()
    {
        await using var pair = InMemoryDataChannelPair.Create();
        var toRight = new Collector(pair.Right);
        var toLeft = new Collector(pair.Left);

        pair.Left.Send([1]);
        pair.Right.Send([2]);

        await toRight.WaitForCountAsync(1);
        await toLeft.WaitForCountAsync(1);

        Assert.Equal([1], toRight.Messages[0]);
        Assert.Equal([2], toLeft.Messages[0]);
    }

    [Fact]
    public async Task 消息边界被保留()
    {
        // 这是「消息式而非流式」的核心保证：三条消息收到就是三条，
        // 不会被合并成一坨。上层因此不需要自己分帧。
        await using var pair = InMemoryDataChannelPair.Create();
        var collector = new Collector(pair.Right);

        pair.Left.Send(Message(10, 0xAA));
        pair.Left.Send(Message(20, 0xBB));
        pair.Left.Send(Message(30, 0xCC));

        await collector.WaitForCountAsync(3);

        Assert.Equal([10, 20, 30], collector.Messages.Select(m => m.Length).ToArray());
        Assert.All(collector.Messages[0], b => Assert.Equal(0xAA, b));
        Assert.All(collector.Messages[2], b => Assert.Equal(0xCC, b));
    }

    [Fact]
    public async Task 消息顺序被保留()
    {
        await using var pair = InMemoryDataChannelPair.Create();
        var collector = new Collector(pair.Right);

        for (var i = 0; i < 200; i++)
        {
            pair.Left.Send([(byte)(i & 0xFF), (byte)(i >> 8)]);
        }

        await collector.WaitForCountAsync(200);

        for (var i = 0; i < 200; i++)
        {
            var expected = new[] { (byte)(i & 0xFF), (byte)(i >> 8) };
            Assert.Equal(expected, collector.Messages[i]);
        }
    }

    [Fact]
    public async Task 超过上限的消息被拒绝()
    {
        await using var pair = InMemoryDataChannelPair.Create(maxMessageSize: 100);

        Assert.Throws<ArgumentException>(() => pair.Left.Send(Message(101, 0)));

        // 恰好到上限应该可以
        pair.Left.Send(Message(100, 0));
    }

    [Fact]
    public async Task 调用方在_Send_返回后可以复用缓冲区()
    {
        // Send 的契约是同步入队。如果实现只保存了引用而没有复制，
        // 调用方复用缓冲区就会让对端收到被改写的数据 —— 极难排查。
        await using var pair = InMemoryDataChannelPair.Create();
        var collector = new Collector(pair.Right);

        var buffer = Message(16, 0x11);
        pair.Left.Send(buffer);
        Array.Fill(buffer, (byte)0x99);   // 立刻改写

        await collector.WaitForCountAsync(1);

        Assert.All(collector.Messages[0], b => Assert.Equal(0x11, b));
    }

    // ---- 背压 ----

    [Fact]
    public async Task BufferedAmount_随投递上升随排空下降()
    {
        // 限速到 100 KiB/s，这样缓冲才有机会真的堆起来
        await using var pair = InMemoryDataChannelPair.Create(
            FaultProfile.Throttled(100 * 1024, TimeSpan.Zero));

        for (var i = 0; i < 10; i++)
        {
            pair.Left.Send(Message(10 * 1024, (byte)i));
        }

        Assert.True(pair.Left.BufferedAmount > 0, "投递后缓冲应大于零");

        await pair.Left.WaitForDrainAsync(0);

        Assert.Equal(0, pair.Left.BufferedAmount);
    }

    [Fact]
    public async Task WaitForDrainAsync_在阈值以下立即返回()
    {
        await using var pair = InMemoryDataChannelPair.Create();

        await pair.Left.WaitForDrainAsync(0);
        await pair.Left.WaitForDrainAsync(1024);
    }

    [Fact]
    public async Task WaitForDrainAsync_等到降到阈值以下()
    {
        await using var pair = InMemoryDataChannelPair.Create(
            FaultProfile.Throttled(200 * 1024, TimeSpan.Zero));

        for (var i = 0; i < 20; i++)
        {
            pair.Left.Send(Message(10 * 1024, (byte)i));
        }

        await pair.Left.WaitForDrainAsync(50 * 1024);

        Assert.True(pair.Left.BufferedAmount <= 50 * 1024,
            $"排空后缓冲为 {pair.Left.BufferedAmount}，应不超过 {50 * 1024}");
    }

    [Fact]
    public async Task BufferedAmountLow_事件在回落时触发()
    {
        await using var pair = InMemoryDataChannelPair.Create(
            FaultProfile.Throttled(200 * 1024, TimeSpan.Zero));

        var fired = new TaskCompletionSource();
        pair.Left.BufferedAmountLowThreshold = 20 * 1024;
        pair.Left.BufferedAmountLow += () => fired.TrySetResult();

        for (var i = 0; i < 20; i++)
        {
            pair.Left.Send(Message(10 * 1024, (byte)i));
        }

        await fired.Task.WaitAsync(Timeout);
    }

    [Fact]
    public async Task 阈值为零时不触发_BufferedAmountLow()
    {
        await using var pair = InMemoryDataChannelPair.Create();

        var fired = false;
        pair.Left.BufferedAmountLowThreshold = 0;
        pair.Left.BufferedAmountLow += () => fired = true;

        pair.Left.Send(Message(1024, 1));
        await pair.Left.WaitForDrainAsync(0);

        Assert.False(fired);
    }

    [Fact]
    public async Task WaitForDrainAsync_可以被取消()
    {
        await using var pair = InMemoryDataChannelPair.Create(
            FaultProfile.Throttled(1024, TimeSpan.Zero));

        for (var i = 0; i < 50; i++)
        {
            pair.Left.Send(Message(1024, (byte)i));
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pair.Left.WaitForDrainAsync(0, cts.Token));
    }

    // ---- 故障注入 ----

    [Fact]
    public async Task 按字节数断开()
    {
        await using var pair = InMemoryDataChannelPair.Create(FaultProfile.DisconnectAfter(3 * 1024));

        var closedReason = new TaskCompletionSource<string?>();
        pair.Left.Closed += reason => closedReason.TrySetResult(reason);

        for (var i = 0; i < 10; i++)
        {
            try
            {
                pair.Left.Send(Message(1024, (byte)i));
            }
            catch (DataChannelClosedException)
            {
                break;   // 断开之后继续投递会被拒绝，符合预期
            }
        }

        var reason = await closedReason.Task.WaitAsync(Timeout);

        Assert.Equal(DataChannelState.Closed, pair.Left.State);
        Assert.NotNull(reason);
    }

    [Fact]
    public async Task 断开会同时关闭两端()
    {
        // 单端关闭而对端不知道，会让对端永远等下去 —— 那种挂起最难查
        await using var pair = InMemoryDataChannelPair.Create(FaultProfile.DisconnectAfter(1024));

        var bothClosed = new TaskCompletionSource();
        var count = 0;
        void OnClosed(string? _)
        {
            if (Interlocked.Increment(ref count) == 2)
            {
                bothClosed.TrySetResult();
            }
        }

        pair.Left.Closed += OnClosed;
        pair.Right.Closed += OnClosed;

        pair.Left.Send(Message(2048, 1));

        await bothClosed.Task.WaitAsync(Timeout);

        Assert.Equal(DataChannelState.Closed, pair.Left.State);
        Assert.Equal(DataChannelState.Closed, pair.Right.State);
    }

    [Fact]
    public async Task 按消息数断开()
    {
        await using var pair = InMemoryDataChannelPair.Create(
            new FaultProfile { DisconnectAfterMessages = 3 });

        var collector = new Collector(pair.Right);
        var closed = new TaskCompletionSource();
        pair.Left.Closed += _ => closed.TrySetResult();

        for (var i = 0; i < 10; i++)
        {
            try
            {
                pair.Left.Send([(byte)i]);
            }
            catch (DataChannelClosedException)
            {
                break;
            }
        }

        await closed.Task.WaitAsync(Timeout);

        // 跨过阈值的那条消息会完整投递后才断开，所以对端收到恰好 3 条
        Assert.Equal(3, collector.Messages.Count);
    }

    [Fact]
    public async Task 投递延迟被应用()
    {
        await using var pair = InMemoryDataChannelPair.Create(
            new FaultProfile { DeliveryDelay = TimeSpan.FromMilliseconds(50) });

        var collector = new Collector(pair.Right);
        var start = DateTime.UtcNow;

        pair.Left.Send([1]);
        await collector.WaitForCountAsync(1);

        Assert.True(DateTime.UtcNow - start >= TimeSpan.FromMilliseconds(40),
            "投递应至少延迟约 50ms");
    }

    // ---- 关闭语义 ----

    [Fact]
    public async Task 关闭后再发会抛_DataChannelClosedException()
    {
        await using var pair = InMemoryDataChannelPair.Create();

        await pair.Left.CloseAsync("测试");

        Assert.Throws<DataChannelClosedException>(() => pair.Left.Send([1]));
    }

    [Fact]
    public async Task Closed_事件只触发一次()
    {
        await using var pair = InMemoryDataChannelPair.Create();

        var count = 0;
        pair.Left.Closed += _ => Interlocked.Increment(ref count);

        await pair.Left.CloseAsync("第一次");
        await pair.Left.CloseAsync("第二次");
        await pair.Left.DisposeAsync();

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task 关闭原因被传递()
    {
        await using var pair = InMemoryDataChannelPair.Create();

        string? received = null;
        pair.Left.Closed += reason => received = reason;

        await pair.Left.CloseAsync("磁盘满了");

        Assert.Equal("磁盘满了", received);
    }

    [Fact]
    public async Task 等待排空期间关闭会抛异常而不是静默返回()
    {
        // 静默返回会让发送循环空转，症状是「CPU 打满但毫无进展」
        await using var pair = InMemoryDataChannelPair.Create(
            FaultProfile.Throttled(1024, TimeSpan.Zero));

        for (var i = 0; i < 100; i++)
        {
            pair.Left.Send(Message(1024, (byte)i));
        }

        var waiting = pair.Left.WaitForDrainAsync(0);
        await Task.Delay(50);
        await pair.Left.CloseAsync("中途关闭");

        await Assert.ThrowsAsync<DataChannelClosedException>(() => waiting);
    }

    [Fact]
    public async Task 接收回调抛异常会断开连接()
    {
        // 消费方处理不了数据时，断开比静默吞掉诚实 ——
        // 吞掉的症状是「传输卡住但没有任何错误」
        await using var pair = InMemoryDataChannelPair.Create();

        pair.Right.MessageReceived += _ => throw new InvalidOperationException("消费方炸了");

        var closed = new TaskCompletionSource<string?>();
        pair.Left.Closed += reason => closed.TrySetResult(reason);

        pair.Left.Send([1]);

        var reason = await closed.Task.WaitAsync(Timeout);

        Assert.NotNull(reason);
        Assert.Contains("消费方炸了", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 重复_Dispose_不抛异常()
    {
        var pair = InMemoryDataChannelPair.Create();

        await pair.DisposeAsync();
        await pair.DisposeAsync();
    }

    [Fact]
    public async Task 大量消息不会丢()
    {
        await using var pair = InMemoryDataChannelPair.Create();
        var collector = new Collector(pair.Right);

        const int Count = 2000;
        for (var i = 0; i < Count; i++)
        {
            pair.Left.Send(Message(64, (byte)(i & 0xFF)));
        }

        await collector.WaitForCountAsync(Count);
        await pair.Left.WaitForDrainAsync(0);

        Assert.Equal(Count, collector.Messages.Count);
        Assert.Equal(Count * 64L, collector.TotalBytes);
    }
}
