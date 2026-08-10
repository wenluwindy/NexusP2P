using NexusP2P.Transport.Abstractions;
using NexusP2P.Transport.WebRtc;

namespace NexusP2P.Integration.Tests.WebRtc;

/// <summary>
/// 真实 WebRTC 传输的基础行为。
///
/// <para>这些测试用的是<b>真的 DTLS 握手、真的 SCTP、真的 ICE</b>，
/// 只是把信令在内存里转交。所以它们验证的是「<see cref="IDataChannel"/>
/// 的契约在真实传输上成立」，而不只是接口能编译。</para>
/// </summary>
public sealed class WebRtcDataChannelTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>收集对端收到的消息。</summary>
    private sealed class Collector
    {
        private readonly List<byte[]> _messages = [];
        private readonly Lock _gate = new();

        public Collector(IDataChannel channel) =>
            channel.MessageReceived += message =>
            {
                lock (_gate)
                {
                    _messages.Add(message.ToArray());
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

        public async Task WaitForCountAsync(int count)
        {
            var deadline = DateTime.UtcNow + Timeout;
            while (Messages.Count < count)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"等待 {count} 条消息超时，只收到 {Messages.Count} 条。");
                }

                await Task.Delay(10);
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
    public async Task 能建立真实的_WebRTC_连接()
    {
        await using var pair = await LoopbackPeerPair.ConnectAsync();

        Assert.Equal(DataChannelState.Open, pair.Offerer.State);
        Assert.Equal(DataChannelState.Open, pair.Answerer.State);
    }

    [Fact]
    public async Task 回环下选中的是_host_候选()
    {
        // 同机连接应该选 host 候选而不是走中继。
        // 这条同时验证了 GetCandidatePairKind 真的能读出候选类型 ——
        // 「瓶颈说明」要靠它区分直连与中继。
        await using var pair = await LoopbackPeerPair.ConnectAsync();

        Assert.Equal(CandidatePairKind.Host, pair.OffererCandidateKind);
    }

    [Fact]
    public async Task 消息能双向传递()
    {
        await using var pair = await LoopbackPeerPair.ConnectAsync();
        var toAnswerer = new Collector(pair.Answerer);
        var toOfferer = new Collector(pair.Offerer);

        pair.Offerer.Send([1, 2, 3]);
        pair.Answerer.Send([4, 5]);

        await toAnswerer.WaitForCountAsync(1);
        await toOfferer.WaitForCountAsync(1);

        Assert.Equal([1, 2, 3], toAnswerer.Messages[0]);
        Assert.Equal([4, 5], toOfferer.Messages[0]);
    }

    [Fact]
    public async Task 消息边界被保留()
    {
        // SCTP 是消息式的，三条消息收到就该是三条 —— 上层因此不必自己分帧
        await using var pair = await LoopbackPeerPair.ConnectAsync();
        var collector = new Collector(pair.Answerer);

        pair.Offerer.Send(Message(10, 0xAA));
        pair.Offerer.Send(Message(1000, 0xBB));
        pair.Offerer.Send(Message(30_000, 0xCC));

        await collector.WaitForCountAsync(3);

        Assert.Equal([10, 1000, 30_000], collector.Messages.Select(m => m.Length).ToArray());
        Assert.All(collector.Messages[2], b => Assert.Equal(0xCC, b));
    }

    [Fact]
    public async Task 消息顺序被保留()
    {
        await using var pair = await LoopbackPeerPair.ConnectAsync();
        var collector = new Collector(pair.Answerer);

        const int Count = 500;
        for (var i = 0; i < Count; i++)
        {
            pair.Offerer.Send([(byte)(i & 0xFF), (byte)(i >> 8)]);
        }

        await collector.WaitForCountAsync(Count);

        for (var i = 0; i < Count; i++)
        {
            Assert.Equal([(byte)(i & 0xFF), (byte)(i >> 8)], collector.Messages[i]);
        }
    }

    [Fact]
    public async Task 超过上限的消息被拒绝()
    {
        await using var pair = await LoopbackPeerPair.ConnectAsync();

        Assert.Throws<ArgumentException>(
            () => pair.Offerer.Send(new byte[pair.Offerer.MaxMessageSize + 1]));

        // 恰好到上限应该可以
        pair.Offerer.Send(new byte[pair.Offerer.MaxMessageSize]);
    }

    [Fact]
    public async Task 单条上限是保守的_64_KiB()
    {
        // 浏览器跨实现的安全上限是 256 KiB，但贴着上限发容易在边界失败。
        // spike 已证明 64 KiB 分片就能跑到 76 MiB/s，没必要冒险。
        await using var pair = await LoopbackPeerPair.ConnectAsync();

        Assert.Equal(64 * 1024, pair.Offerer.MaxMessageSize);
    }

    // ---- 接管前的消息 ----

    [Fact]
    public async Task 订阅之前到达的消息不会丢()
    {
        // 曾经会丢：原生消息回调在构造函数里就注册了，而上层要等通道
        // 被交出去之后才订阅。窗口里到达的消息投给空事件 = 永久消失，
        // 现象是两端一起干等、谁都不报错。
        await using var pair = await LoopbackPeerPair.ConnectAsync();

        var payload = Message(4096, 0x5A);
        pair.Offerer.Send(payload);

        // 等一会儿，确保消息已经躺在对端的原生层里，而这时还没有任何订阅者
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.Answerer.MessageReceived += message => received.TrySetResult(message.ToArray());

        Assert.Equal(payload, await received.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task 补发的消息保持原顺序()
    {
        // 顺序错了对 MessageAssembler 等同于数据损坏
        await using var pair = await LoopbackPeerPair.ConnectAsync();

        for (var i = 0; i < 5; i++)
        {
            pair.Offerer.Send(Message(1024, (byte)i));
        }

        await Task.Delay(TimeSpan.FromMilliseconds(300));

        var order = new List<byte>();
        var gate = new Lock();
        var all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        pair.Answerer.MessageReceived += message =>
        {
            lock (gate)
            {
                order.Add(message.Span[0]);
                if (order.Count == 5)
                {
                    all.TrySetResult();
                }
            }
        };

        await all.Task.WaitAsync(TimeSpan.FromSeconds(10));

        lock (gate)
        {
            Assert.Equal<byte>([0, 1, 2, 3, 4], order);
        }
    }

    // ---- 背压 ----

    [Fact]
    public async Task BufferedAmount_随投递上升随排空归零()
    {
        await using var pair = await LoopbackPeerPair.ConnectAsync();

        // 反复几轮：WaitForDrainAsync 内部对低水位脉冲有 200ms 的兜底超时，
        // 而它曾经在超时时直接退出整个方法（假装排空完成）。机器一忙就会中招，
        // 单轮撞不稳，多轮才容易把它逼出来。
        for (var round = 0; round < 4; round++)
        {
            // 一次灌进去足够多，让缓冲来得及堆起来
            for (var i = 0; i < 400; i++)
            {
                pair.Offerer.Send(Message(60 * 1024, (byte)(i & 0xFF)));
            }

            await pair.Offerer.WaitForDrainAsync(0);

            Assert.Equal(0, pair.Offerer.BufferedAmount);
        }
    }

    [Fact]
    public async Task 低水位事件会触发()
    {
        // 这是 libdatachannel 相对 SIPSorcery 的关键优势：有真正的事件，
        // 不必用 Task.Delay(1) 轮询（那在 Windows 上实际睡 15.6ms）
        await using var pair = await LoopbackPeerPair.ConnectAsync();

        var fired = new TaskCompletionSource();
        pair.Offerer.BufferedAmountLowThreshold = 64 * 1024;
        pair.Offerer.BufferedAmountLow += () => fired.TrySetResult();

        for (var i = 0; i < 200; i++)
        {
            pair.Offerer.Send(Message(60 * 1024, (byte)(i & 0xFF)));
        }

        await fired.Task.WaitAsync(Timeout);
    }

    [Fact]
    public async Task WaitForDrainAsync_在阈值以下立即返回()
    {
        await using var pair = await LoopbackPeerPair.ConnectAsync();

        await pair.Offerer.WaitForDrainAsync(0);
        await pair.Offerer.WaitForDrainAsync(1024 * 1024);
    }

    [Fact]
    public async Task WaitForDrainAsync_可以被取消()
    {
        await using var pair = await LoopbackPeerPair.ConnectAsync();

        for (var i = 0; i < 500; i++)
        {
            pair.Offerer.Send(Message(60 * 1024, (byte)(i & 0xFF)));
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pair.Offerer.WaitForDrainAsync(0, cts.Token));
    }

    // ---- 关闭 ----

    [Fact]
    public async Task 关闭后再发会抛异常()
    {
        await using var pair = await LoopbackPeerPair.ConnectAsync();

        await pair.Offerer.CloseAsync("测试");

        Assert.Throws<DataChannelClosedException>(() => pair.Offerer.Send([1]));
    }

    [Fact]
    public async Task Closed_事件只触发一次()
    {
        await using var pair = await LoopbackPeerPair.ConnectAsync();

        var count = 0;
        pair.Offerer.Closed += _ => Interlocked.Increment(ref count);

        await pair.Offerer.CloseAsync("第一次");
        await pair.Offerer.CloseAsync("第二次");

        Assert.Equal(1, Volatile.Read(ref count));
    }

    [Fact]
    public async Task 一端关闭后对端会收到通知()
    {
        await using var pair = await LoopbackPeerPair.ConnectAsync();

        var peerClosed = new TaskCompletionSource();
        pair.Answerer.Closed += _ => peerClosed.TrySetResult();

        await pair.Offerer.CloseAsync("走了");

        await peerClosed.Task.WaitAsync(Timeout);
        Assert.Equal(DataChannelState.Closed, pair.Answerer.State);
    }

    [Fact]
    public async Task 重复释放不抛异常()
    {
        var pair = await LoopbackPeerPair.ConnectAsync();

        await pair.DisposeAsync();
        await pair.DisposeAsync();
    }

    [Fact]
    public async Task 反复建连接不泄漏句柄()
    {
        // 原生对象与 GCHandle 的释放顺序写错就会泄漏或崩溃。
        // 连续建拆 20 次，若有问题通常会在这里暴露。
        for (var i = 0; i < 20; i++)
        {
            await using var pair = await LoopbackPeerPair.ConnectAsync();
            pair.Offerer.Send([(byte)i]);
        }
    }

    [Fact]
    public async Task 大量数据能完整送达()
    {
        await using var pair = await LoopbackPeerPair.ConnectAsync();

        long received = 0;
        var done = new TaskCompletionSource();
        const long Total = 16L * 1024 * 1024;

        pair.Answerer.MessageReceived += message =>
        {
            if (Interlocked.Add(ref received, message.Length) >= Total)
            {
                done.TrySetResult();
            }
        };

        var chunk = Message(60 * 1024, 0x5A);
        long sent = 0;
        while (sent < Total)
        {
            if (pair.Offerer.BufferedAmount > 4L * 1024 * 1024)
            {
                await pair.Offerer.WaitForDrainAsync(1024 * 1024);
            }

            pair.Offerer.Send(chunk);
            sent += chunk.Length;
        }

        await done.Task.WaitAsync(Timeout);

        Assert.True(Interlocked.Read(ref received) >= Total);
    }
}
