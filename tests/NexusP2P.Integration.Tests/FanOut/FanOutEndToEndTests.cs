using NexusP2P.Core.Crypto;
using NexusP2P.Transfer;
using NexusP2P.Transfer.Protocol;
using NexusP2P.Transfer.Storage;
using NexusP2P.Transport.Abstractions;

namespace NexusP2P.Integration.Tests.FanOut;

/// <summary>
/// Task 9.2：SendFanOut 在内存管道上的端到端（AD-11/13）。
/// 一条链路的失败/慢/重连不影响其他链路；N=1 时行为与 V1 一致。
/// </summary>
public sealed class FanOutEndToEndTests : IDisposable
{
    private readonly TransferHarness _harness = new TransferHarness()
        .With("data/large.bin", 48 * 1024, seed: 7)
        .With("data/small.txt", 3 * 1024, seed: 8)
        .WithEmptyDirectory("data/empty");

    public void Dispose() => _harness.Dispose();

    /// <summary>一个接收端：独立管道 + 独立目标目录。</summary>
    private sealed record ReceiverEnd(
        string PeerId,
        InMemoryDataChannelPair Pair,
        ProtocolConnection SenderSide,
        ProtocolConnection ReceiverSide,
        string Destination);

    private ReceiverEnd CreateReceiver(string peerId, FaultProfile? faults = null)
    {
        var pair = InMemoryDataChannelPair.Create(faults, maxMessageSize: 64 * 1024);
        return new ReceiverEnd(
            peerId,
            pair,
            new ProtocolConnection(pair.Left),
            new ProtocolConnection(pair.Right),
            _harness.CreateTemporaryDirectory());
    }

    private static async Task DisposeReceiverAsync(ReceiverEnd end)
    {
        await end.SenderSide.DisposeAsync();
        await end.ReceiverSide.DisposeAsync();
        await end.Pair.DisposeAsync();
    }

    [Fact]
    public async Task 一发三收_三个接收端各自收齐且内容一致()
    {
        var manifest = await _harness.BuildManifestAsync();
        await using var source = new MemoryPieceSource(manifest, _harness.Files);
        using var cache = new CipherPieceCache(manifest, source, _harness.Secret);
        using var fanOut = new SendFanOut(manifest, _harness.Secret, cache);

        var ends = new[] { CreateReceiver("r1"), CreateReceiver("r2"), CreateReceiver("r3") };
        try
        {
            var receiverTasks = ends.Select(end => Task.Run(async () =>
                await new ReceiveSession(_harness.Secret, end.Destination)
                    .RunAsync(end.ReceiverSide))).ToArray();

            var linkTasks = ends.Select(end =>
                fanOut.RunLinkAsync(end.PeerId, end.SenderSide)).ToArray();

            await AwaitWithDeadline(Task.WhenAll(linkTasks.Concat(receiverTasks)));

            foreach (var snapshot in fanOut.Links)
            {
                Assert.Equal(FanOutLinkState.Completed, snapshot.State);
                Assert.Null(snapshot.Error);
            }

            foreach (var end in ends)
            {
                await _harness.AssertLandedAsync(end.Destination);
            }

            // AD-13：三条链路共享密文 —— 每个分片只加密一次
            var locator = new PieceLocator(manifest);
            Assert.Equal(locator.TotalPieces, (int)cache.Encryptions);
            Assert.True(cache.Hits >= locator.TotalPieces * 2,
                $"另两条链路应当命中缓存（hits={cache.Hits}）");
        }
        finally
        {
            foreach (var end in ends)
            {
                await DisposeReceiverAsync(end);
            }
        }
    }

    [Fact]
    public async Task 一条链路中途断掉_其余两条不受影响()
    {
        var manifest = await _harness.BuildManifestAsync();
        await using var source = new MemoryPieceSource(manifest, _harness.Files);
        using var cache = new CipherPieceCache(manifest, source, _harness.Secret);
        using var fanOut = new SendFanOut(manifest, _harness.Secret, cache);

        // r2 的管道在 20 KiB 后断线 —— 「接收端被杀」的内存等价物
        var ends = new[]
        {
            CreateReceiver("r1"),
            CreateReceiver("r2", FaultProfile.DisconnectAfter(20 * 1024)),
            CreateReceiver("r3"),
        };

        try
        {
            var receiverTasks = ends.Select(end => Task.Run(async () =>
            {
                try
                {
                    await new ReceiveSession(_harness.Secret, end.Destination)
                        .RunAsync(end.ReceiverSide);
                }
                catch (Exception)
                {
                    // r2 的接收端会失败，这正是测试场景
                }
            })).ToArray();

            var linkTasks = ends.Select(end =>
                fanOut.RunLinkAsync(end.PeerId, end.SenderSide)).ToArray();

            await AwaitWithDeadline(Task.WhenAll(linkTasks.Concat(receiverTasks)));

            var snapshots = fanOut.Links.ToDictionary(s => s.PeerId);

            Assert.Equal(FanOutLinkState.Completed, snapshots["r1"].State);
            Assert.Equal(FanOutLinkState.Failed, snapshots["r2"].State);
            Assert.NotNull(snapshots["r2"].Error);
            Assert.Equal(FanOutLinkState.Completed, snapshots["r3"].State);

            await _harness.AssertLandedAsync(ends[0].Destination);
            await _harness.AssertLandedAsync(ends[2].Destination);
        }
        finally
        {
            foreach (var end in ends)
            {
                await DisposeReceiverAsync(end);
            }
        }
    }

    [Fact]
    public async Task 断掉的接收端以新_peerId_重连后从断点续传()
    {
        var manifest = await _harness.BuildManifestAsync();
        await using var source = new MemoryPieceSource(manifest, _harness.Files);
        using var cache = new CipherPieceCache(manifest, source, _harness.Secret);
        using var fanOut = new SendFanOut(manifest, _harness.Secret, cache);

        var destination = _harness.CreateTemporaryDirectory();

        // 第一轮：中途断线，部分分片已落盘
        var broken = CreateReceiver("r-old", FaultProfile.DisconnectAfter(20 * 1024));
        try
        {
            var receiverTask = Task.Run(async () =>
            {
                try
                {
                    await new ReceiveSession(_harness.Secret, destination)
                        .RunAsync(broken.ReceiverSide);
                }
                catch (Exception)
                {
                    // 预期失败
                }
            });

            await AwaitWithDeadline(Task.WhenAll(
                fanOut.RunLinkAsync("r-old", broken.SenderSide), receiverTask));

            Assert.Equal(FanOutLinkState.Failed, fanOut.Links.Single(s => s.PeerId == "r-old").State);
        }
        finally
        {
            await DisposeReceiverAsync(broken);
        }

        // 第二轮：同一个目标目录、新 peerId（AD-16），必须从断点接着传
        fanOut.ForgetLink("r-old");
        var fresh = CreateReceiver("r-new");
        try
        {
            ReceiveResult? result = null;
            var receiverTask = Task.Run(async () =>
            {
                result = await new ReceiveSession(_harness.Secret, destination)
                    .RunAsync(fresh.ReceiverSide);
            });

            await AwaitWithDeadline(Task.WhenAll(
                fanOut.RunLinkAsync("r-new", fresh.SenderSide), receiverTask));

            Assert.Equal(FanOutLinkState.Completed, fanOut.Links.Single(s => s.PeerId == "r-new").State);
            Assert.NotNull(result);
            Assert.True(result!.ResumedPieces > 0, "第二轮应当从断点续传而不是从头开始");

            await _harness.AssertLandedAsync(destination);
        }
        finally
        {
            await DisposeReceiverAsync(fresh);
        }
    }

    [Fact]
    public async Task 同一_peerId_不允许两条并存链路()
    {
        var manifest = await _harness.BuildManifestAsync();
        await using var source = new MemoryPieceSource(manifest, _harness.Files);
        using var cache = new CipherPieceCache(manifest, source, _harness.Secret);
        using var fanOut = new SendFanOut(manifest, _harness.Secret, cache);

        var first = CreateReceiver("dup");
        var second = CreateReceiver("dup");
        try
        {
            var receiverTask = Task.Run(async () =>
                await new ReceiveSession(_harness.Secret, first.Destination)
                    .RunAsync(first.ReceiverSide));
            var linkTask = fanOut.RunLinkAsync("dup", first.SenderSide);

            // 占位冲突是同步抛出的（在任务启动之前），所以这里是 Action 而不是 Func<Task>
            Assert.Throws<InvalidOperationException>(
                () => { _ = fanOut.RunLinkAsync("dup", second.SenderSide); });

            await AwaitWithDeadline(Task.WhenAll(linkTask, receiverTask));
        }
        finally
        {
            await DisposeReceiverAsync(first);
            await DisposeReceiverAsync(second);
        }
    }

    [Fact]
    public async Task 单接收方经_FanOut_与_V1_直接使用结果一致()
    {
        // N=1 时 FanOut 只是包了一层：落盘内容与 V1 SendSession 直接跑完全一致
        var manifest = await _harness.BuildManifestAsync();

        // V1 路径
        var v1Destination = _harness.CreateTemporaryDirectory();
        var v1Run = await _harness.RunAsync(v1Destination, manifest: manifest);
        Assert.True(v1Run.Completed, $"V1 路径失败：{v1Run.SenderError?.Message ?? v1Run.ReceiverError?.Message}");

        // FanOut 路径
        await using var source = new MemoryPieceSource(manifest, _harness.Files);
        using var cache = new CipherPieceCache(manifest, source, _harness.Secret);
        using var fanOut = new SendFanOut(manifest, _harness.Secret, cache);

        var end = CreateReceiver("solo");
        try
        {
            var receiverTask = Task.Run(async () =>
                await new ReceiveSession(_harness.Secret, end.Destination)
                    .RunAsync(end.ReceiverSide));

            await AwaitWithDeadline(Task.WhenAll(
                fanOut.RunLinkAsync("solo", end.SenderSide), receiverTask));

            Assert.Equal(FanOutLinkState.Completed, fanOut.Links.Single().State);
            await _harness.AssertLandedAsync(end.Destination);

            // 两条路径落盘逐字节一致
            foreach (var (path, _) in _harness.Files)
            {
                var relative = path.Replace('/', Path.DirectorySeparatorChar);
                Assert.Equal(
                    await File.ReadAllBytesAsync(Path.Combine(v1Destination, relative)),
                    await File.ReadAllBytesAsync(Path.Combine(end.Destination, relative)));
            }
        }
        finally
        {
            await DisposeReceiverAsync(end);
        }
    }

    [Fact]
    public async Task 进度快照逐链路独立且最终到达_Completed()
    {
        var manifest = await _harness.BuildManifestAsync();
        await using var source = new MemoryPieceSource(manifest, _harness.Files);
        using var cache = new CipherPieceCache(manifest, source, _harness.Secret);
        using var fanOut = new SendFanOut(manifest, _harness.Secret, cache);

        var snapshots = new System.Collections.Concurrent.ConcurrentBag<FanOutLinkSnapshot>();
        var progress = new ProgressCollector(snapshots);

        var ends = new[] { CreateReceiver("p1"), CreateReceiver("p2") };
        try
        {
            var receiverTasks = ends.Select(end => Task.Run(async () =>
                await new ReceiveSession(_harness.Secret, end.Destination)
                    .RunAsync(end.ReceiverSide))).ToArray();

            var linkTasks = ends.Select(end =>
                fanOut.RunLinkAsync(end.PeerId, end.SenderSide, progress)).ToArray();

            await AwaitWithDeadline(Task.WhenAll(linkTasks.Concat(receiverTasks)));

            foreach (var peerId in new[] { "p1", "p2" })
            {
                var own = snapshots.Where(s => s.PeerId == peerId).ToList();
                Assert.NotEmpty(own);
                Assert.Contains(own, s => s.State == FanOutLinkState.Completed);
                Assert.DoesNotContain(own, s => s.State == FanOutLinkState.Failed);
            }
        }
        finally
        {
            foreach (var end in ends)
            {
                await DisposeReceiverAsync(end);
            }
        }
    }

    private sealed class ProgressCollector(
        System.Collections.Concurrent.ConcurrentBag<FanOutLinkSnapshot> sink)
        : IProgress<FanOutLinkSnapshot>
    {
        public void Report(FanOutLinkSnapshot value) => sink.Add(value);
    }

    /// <summary>硬超时兜死锁：协议死锁的表现是两边各等各的（与 TransferHarness 同理）。</summary>
    private static async Task AwaitWithDeadline(Task work)
    {
        var finished = await Task.WhenAny(
            work, Task.Delay(TimeSpan.FromSeconds(60), CancellationToken.None));

        if (finished != work)
        {
            Assert.Fail("扇出传输在 60 秒内没有结束，疑似死锁。");
        }

        await work;   // 让异常浮出来
    }
}
