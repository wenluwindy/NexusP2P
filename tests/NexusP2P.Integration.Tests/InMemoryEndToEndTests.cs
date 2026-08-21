using NexusP2P.Core.Crypto;
using NexusP2P.Transfer;
using NexusP2P.Transfer.Storage;
using NexusP2P.Transport.Abstractions;

namespace NexusP2P.Integration.Tests;

/// <summary>
/// 内存管道上的端到端测试 —— <b>Checkpoint 2 的验收</b>。
///
/// <para>这一组测试通过之后，协议的正确性就与网络无关了：分片、加密、
/// 校验、落盘、断点续传全部被证明。接真网时只会面对一类 bug（网络），
/// 而不是网络与协议两类叠加。</para>
/// </summary>
public sealed class InMemoryEndToEndTests
{
    [Fact]
    public async Task 单文件端到端一致()
    {
        using var harness = new TransferHarness().With("a.bin", 50_000);
        var destination = harness.CreateTemporaryDirectory();

        var run = await harness.RunAsync(destination);

        Assert.True(run.Completed, $"发送端：{run.SenderError}；接收端：{run.ReceiverError}");
        await harness.AssertLandedAsync(destination);
    }

    [Fact]
    public async Task 文件夹含嵌套目录空文件与空目录()
    {
        using var harness = new TransferHarness()
            .With("proj/readme.md", 500)
            .With("proj/src/deep/nested/a.bin", 30_000)
            .With("proj/src/b.bin", 1)
            .With("proj/empty.dat", 0)
            .With("proj/我的文件.txt", 2000)
            .WithEmptyDirectory("proj/logs")
            .WithEmptyDirectory("proj/tmp/cache");

        var destination = harness.CreateTemporaryDirectory();

        var run = await harness.RunAsync(destination);

        Assert.True(run.Completed, $"发送端：{run.SenderError}；接收端：{run.ReceiverError}");
        await harness.AssertLandedAsync(destination);
        Assert.Equal(5, run.Result!.LandedFiles.Count);
    }

    [Fact]
    public async Task 真实分片参数下的一_MiB_内容()
    {
        // 默认参数：64 KiB 叶子 / 1 MiB 分片，单条消息 256 KiB ——
        // 一个分片必须切成多条消息，这条路径在真实参数下必须走通
        using var harness = new TransferHarness
        {
            Parameters = NexusP2P.Core.Hashing.MerkleParameters.Default,
        }.With("big.bin", 3 * 1024 * 1024);

        var destination = harness.CreateTemporaryDirectory();

        var run = await harness.RunAsync(destination, maxMessageSize: 256 * 1024);

        Assert.True(run.Completed, $"发送端：{run.SenderError}；接收端：{run.ReceiverError}");
        await harness.AssertLandedAsync(destination);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4095)]
    [InlineData(4096)]
    [InlineData(4097)]
    [InlineData(100_000)]
    public async Task 各种长度边界都一致(int length)
    {
        using var harness = new TransferHarness().With("a.bin", length);
        var destination = harness.CreateTemporaryDirectory();

        var run = await harness.RunAsync(destination);

        Assert.True(run.Completed, $"发送端：{run.SenderError}；接收端：{run.ReceiverError}");
        await harness.AssertLandedAsync(destination);
    }

    // ---- 断点续传 ----

    [Fact]
    public async Task 中途断开后重跑能续上并最终一致()
    {
        using var harness = new TransferHarness().With("a.bin", 200_000);
        var destination = harness.CreateTemporaryDirectory();

        // 第一次：投递约 60 KiB 之后强制断开
        var interrupted = await harness.RunAsync(
            destination, FaultProfile.DisconnectAfter(60 * 1024));

        Assert.False(interrupted.Completed, "第一次本应被中断");

        // 第二次：全新的连接与会话，模拟重连
        var resumed = await harness.RunAsync(destination);

        Assert.True(resumed.Completed, $"续传失败：{resumed.SenderError}；{resumed.ReceiverError}");
        await harness.AssertLandedAsync(destination);

        // 关键断言：续传只补缺的部分，不是从头重传
        var totalPieces = (200_000 + 4095) / 4096;
        Assert.True(resumed.PiecesSent < totalPieces,
            $"续传发了 {resumed.PiecesSent} 个分片，总共只有 {totalPieces} 个 —— 说明进度没被利用");
        Assert.True(resumed.PiecesSent > 0, "续传应该还有分片要发");
    }

    [Fact]
    public async Task 断开多次仍能一路续到完成()
    {
        using var harness = new TransferHarness().With("a.bin", 300_000);
        var destination = harness.CreateTemporaryDirectory();

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var run = await harness.RunAsync(destination, FaultProfile.DisconnectAfter(40 * 1024));
            if (run.Completed)
            {
                break;
            }
        }

        var final = await harness.RunAsync(destination);

        Assert.True(final.Completed, $"最终应完成：{final.SenderError}；{final.ReceiverError}");
        await harness.AssertLandedAsync(destination);
    }

    [Fact]
    public async Task 删掉_meta_后仍能续传()
    {
        // .meta 只是加速手段。删了应该退化为全量重扫，进度不丢。
        using var harness = new TransferHarness().With("a.bin", 200_000);
        var destination = harness.CreateTemporaryDirectory();

        await harness.RunAsync(destination, FaultProfile.DisconnectAfter(60 * 1024));

        var workDirectory = Path.Combine(destination, PieceStore.WorkDirectoryName);
        foreach (var meta in Directory.GetFiles(workDirectory, "*.meta"))
        {
            File.Delete(meta);
        }

        var resumed = await harness.RunAsync(destination);

        Assert.True(resumed.Completed, $"删 .meta 后续传失败：{resumed.SenderError}；{resumed.ReceiverError}");
        await harness.AssertLandedAsync(destination);

        var totalPieces = (200_000 + 4095) / 4096;
        Assert.True(resumed.PiecesSent < totalPieces,
            "重扫应该恢复出已完成的进度，不该从头重传");
    }

    [Fact]
    public async Task 发送方用新清单重发也能续上()
    {
        // 这正是产品的设计：关掉程序重开会生成新文件码、新清单对象，
        // 但内容没变所以清单哈希不变，续传的锚点是内容而非会话。
        using var harness = new TransferHarness().With("a.bin", 200_000);
        var destination = harness.CreateTemporaryDirectory();

        await harness.RunAsync(destination, FaultProfile.DisconnectAfter(60 * 1024));

        // 重新构造清单（内容一样 -> 哈希一样）
        var freshManifest = await harness.BuildManifestAsync();
        var resumed = await harness.RunAsync(destination, manifest: freshManifest);

        Assert.True(resumed.Completed, $"{resumed.SenderError}；{resumed.ReceiverError}");
        await harness.AssertLandedAsync(destination);
    }

    [Fact]
    public async Task 文件夹的断点续传()
    {
        using var harness = new TransferHarness()
            .With("d/a.bin", 80_000)
            .With("d/b.bin", 80_000)
            .With("d/sub/c.bin", 80_000);

        var destination = harness.CreateTemporaryDirectory();

        await harness.RunAsync(destination, FaultProfile.DisconnectAfter(50 * 1024));
        var resumed = await harness.RunAsync(destination);

        Assert.True(resumed.Completed, $"{resumed.SenderError}；{resumed.ReceiverError}");
        await harness.AssertLandedAsync(destination);
    }

    /// <summary>
    /// 一次传输里出现内容完全相同的多个文件。
    ///
    /// <para>这是一条<b>回归测试</b>。<c>.part</c> 最初按文件根命名（内容寻址），
    /// 于是内容相同的文件会撞名 —— 数据全写进同一个 <c>.part</c>，
    /// 收尾时第一个文件把它移走、后面的报「文件找不到」。
    /// 文件夹里有重复文件很常见，而<b>多个空文件必然撞名</b>。</para>
    /// </summary>
    [Fact]
    public async Task 内容完全相同的多个文件()
    {
        var identical = TransferHarness.Content(30_000, seed: 42);

        using var harness = new TransferHarness()
            .With("dup/a.bin", identical)
            .With("dup/b.bin", identical)
            .With("dup/sub/c.bin", identical)
            .With("dup/empty1.dat", [])
            .With("dup/empty2.dat", []);

        var destination = harness.CreateTemporaryDirectory();

        var run = await harness.RunAsync(destination);

        Assert.True(run.Completed, $"发送端：{run.SenderError}；接收端：{run.ReceiverError}");
        await harness.AssertLandedAsync(destination);
        Assert.Equal(5, run.Result!.LandedFiles.Count);
    }

    [Fact]
    public async Task 内容相同的多个文件也能断点续传()
    {
        var identical = TransferHarness.Content(60_000, seed: 7);

        using var harness = new TransferHarness()
            .With("dup/a.bin", identical)
            .With("dup/b.bin", identical);

        var destination = harness.CreateTemporaryDirectory();

        await harness.RunAsync(destination, FaultProfile.DisconnectAfter(40 * 1024));
        var resumed = await harness.RunAsync(destination);

        Assert.True(resumed.Completed, $"{resumed.SenderError}；{resumed.ReceiverError}");
        await harness.AssertLandedAsync(destination);
    }

    // ---- 拒收后的重传（曾经会死锁）----

    /// <summary>
    /// 分片在途损坏被拒收后，必须能在下一轮被重传。
    ///
    /// <para>这是一条<b>回归测试</b>。最初的设计没有轮次边界：接收方拒收是静默的，
    /// 发送方不知道要重发，于是接收方等分片、发送方等完成通知 —— 两边各等各的，
    /// 整个传输永久挂死且没有任何错误输出。</para>
    /// </summary>
    [Fact]
    public async Task 分片被拒收后能在下一轮重传()
    {
        using var harness = new TransferHarness().With("a.bin", 40_000);
        var destination = harness.CreateTemporaryDirectory();

        // 第 1 条是密钥要约，第 2 条是清单，之后是分片。挑几条分片消息损坏掉。
        var faults = new FaultProfile
        {
            CorruptMessageOrdinals = new HashSet<long> { 4, 6, 9 },
        };

        var run = await harness.RunAsync(destination, faults);

        Assert.True(run.Completed, $"应能靠重传完成：{run.SenderError}；{run.ReceiverError}");
        await harness.AssertLandedAsync(destination);

        // 被损坏的 3 个分片必然被重发过，所以总投递数超过分片总数
        var totalPieces = (40_000 + 4095) / 4096;
        Assert.True(run.PiecesSent > totalPieces,
            $"投递了 {run.PiecesSent} 个分片，总共 {totalPieces} 个 —— 应该有重传");
    }

    [Fact]
    public async Task 对端持续发损坏数据时明确失败而不是挂死()
    {
        // 每一条分片消息都损坏。必须在有限时间内报错，不能永远等下去。
        using var harness = new TransferHarness().With("a.bin", 20_000);
        var destination = harness.CreateTemporaryDirectory();

        // 序号 1 是密钥要约，2 是清单，3~7 是 5 个分片。只坏分片 ——
        // 把轮次边界的 PushComplete 也坏掉测的就是另一回事了（帧解析），
        // 不是这条用例要验证的东西。
        var faults = new FaultProfile
        {
            CorruptMessageOrdinals = new HashSet<long> { 3, 4, 5, 6, 7 },
        };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = await harness.RunAsync(destination, faults, cancellationToken: timeout.Token);

        Assert.False(run.Completed, "全部损坏时不该报告成功");
        Assert.False(timeout.IsCancellationRequested, "应主动失败而不是被超时掐掉");

        var failure = run.ReceiverError as TransferFailedException
                      ?? run.SenderError as TransferFailedException;
        Assert.NotNull(failure);
        Assert.Equal(NexusP2P.Transfer.Protocol.TransferErrorCode.PieceVerificationFailed, failure.Code);
    }

    // ---- 加密与安全 ----

    [Fact]
    public async Task 密钥要约与清单不匹配时接收端明确报错()
    {
        // V3 里用户不再输入密钥，所以「用户填错密钥」这个失败模式没有了。
        // 剩下的是实现层面的不一致：对端推来的密钥与它密封清单用的不是同一把。
        using var sender = new TransferHarness().With("a.bin", 10_000);
        var manifest = await sender.BuildManifestAsync();
        var destination = sender.CreateTemporaryDirectory();

        await using var pair = InMemoryDataChannelPair.Create();
        await using var senderConnection = new NexusP2P.Transfer.Protocol.ProtocolConnection(pair.Left);
        await using var receiverConnection = new NexusP2P.Transfer.Protocol.ProtocolConnection(pair.Right);

        var receiveSession = new ReceiveSession(destination);

        var senderTask = Task.Run(async () =>
        {
            try
            {
                // 推一把密钥，却用另一把密封清单
                await senderConnection.SendAsync(
                    NexusP2P.Transfer.Protocol.MessageType.KeyOffer,
                    new NexusP2P.Transfer.Protocol.KeyOfferPayload(TransferSecret.Generate()).Serialize());

                var manifestKey = NexusP2P.Core.Crypto.KeyDerivation.DeriveManifestKey(sender.Secret);
                await senderConnection.SendAsync(
                    NexusP2P.Transfer.Protocol.MessageType.Manifest,
                    NexusP2P.Core.Crypto.BlobCipher.Seal(manifestKey, manifest.Serialize()));
            }
            catch
            {
                // 对端会报错并关闭，这里不关心
            }
        });

        var failure = await Assert.ThrowsAsync<TransferFailedException>(
            () => receiveSession.RunAsync(receiverConnection));

        Assert.Equal(NexusP2P.Transfer.Protocol.TransferErrorCode.InvalidManifest, failure.Code);

        await senderTask;
    }

    [Fact]
    public async Task 对端不发密钥要约时接收端明确报错()
    {
        // 旧版发送方（V1/V2）会直接发 Manifest。这必须失败得清楚，
        // 而不是让用户对着一个卡住的进度条猜。
        using var sender = new TransferHarness().With("a.bin", 10_000);
        var manifest = await sender.BuildManifestAsync();
        var destination = sender.CreateTemporaryDirectory();

        await using var pair = InMemoryDataChannelPair.Create();
        await using var senderConnection = new NexusP2P.Transfer.Protocol.ProtocolConnection(pair.Left);
        await using var receiverConnection = new NexusP2P.Transfer.Protocol.ProtocolConnection(pair.Right);

        var senderTask = Task.Run(async () =>
        {
            try
            {
                var manifestKey = NexusP2P.Core.Crypto.KeyDerivation.DeriveManifestKey(sender.Secret);
                await senderConnection.SendAsync(
                    NexusP2P.Transfer.Protocol.MessageType.Manifest,
                    NexusP2P.Core.Crypto.BlobCipher.Seal(manifestKey, manifest.Serialize()));
            }
            catch
            {
                // 对端会报错并关闭
            }
        });

        var failure = await Assert.ThrowsAsync<TransferFailedException>(
            () => new ReceiveSession(destination).RunAsync(receiverConnection));

        Assert.Equal(NexusP2P.Transfer.Protocol.TransferErrorCode.ProtocolViolation, failure.Code);
        Assert.Contains("旧版本", failure.Message, StringComparison.Ordinal);

        await senderTask;
    }

    [Fact]
    public async Task 明文不会以原样出现在链路上()
    {
        // 端到端加密的最直接验证：抓下链路上的全部字节，
        // 里面不该出现源文件的可识别片段。
        using var harness = new TransferHarness();
        var marker = System.Text.Encoding.UTF8.GetBytes("SECRET-MARKER-DO-NOT-LEAK-0123456789");
        var content = new byte[20_000];
        for (var offset = 0; offset + marker.Length <= content.Length; offset += 4096)
        {
            marker.CopyTo(content.AsSpan(offset));
        }

        harness.With("secret.txt", content);
        var manifest = await harness.BuildManifestAsync();
        var destination = harness.CreateTemporaryDirectory();

        var captured = new List<byte[]>();
        var gate = new Lock();

        await using var pair = InMemoryDataChannelPair.Create();
        pair.Right.MessageReceived += frame =>
        {
            lock (gate)
            {
                captured.Add(frame.ToArray());
            }
        };

        await using var senderConnection = new NexusP2P.Transfer.Protocol.ProtocolConnection(pair.Left);
        await using var receiverConnection = new NexusP2P.Transfer.Protocol.ProtocolConnection(pair.Right);
        await using var source = new MemoryPieceSource(manifest, harness.Files);

        var sendSession = new SendSession(manifest, source, harness.Secret);
        var receiveSession = new ReceiveSession(destination);

        await Task.WhenAll(
            Task.Run(() => sendSession.RunAsync(senderConnection)),
            Task.Run(() => receiveSession.RunAsync(receiverConnection)));

        byte[][] frames;
        lock (gate)
        {
            frames = [.. captured];
        }

        Assert.NotEmpty(frames);
        foreach (var frame in frames)
        {
            Assert.False(ContainsSequence(frame, marker), "链路上出现了明文标记");
        }

        // 文件名也不该以明文出现（清单是密封的）
        var nameBytes = System.Text.Encoding.UTF8.GetBytes("secret.txt");
        foreach (var frame in frames)
        {
            Assert.False(ContainsSequence(frame, nameBytes), "链路上出现了明文文件名");
        }
    }

    private static bool ContainsSequence(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.IsEmpty || haystack.Length < needle.Length)
        {
            return false;
        }

        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// 需要独占机器的测试放这里，不与其他集合并行。
///
/// <para>两类：</para>
/// <list type="bullet">
/// <item><b>量整个进程资源的</b> —— <see cref="GC.GetTotalMemory"/> 与线程数
/// 都是进程级的，并行跑的其他测试分配的内存会原样算到被测代码头上。</item>
/// <item><b>吃满 CPU 的真实网络测试</b> —— 跨进程测试要额外拉起两个进程做
/// WebRTC 握手加大文件哈希。机器被压满时 30 秒的连接超时就不够用了，
/// 于是变成随机失败，而且失败现象与真的连不上一模一样。</item>
/// </list>
///
/// <para>代价是整个测试套件变慢。但一个会随机变红的套件比慢的套件更糟 ——
/// 尤其这几条正是盯着重连那几个竞态的。</para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ExclusiveRun
{
    public const string Name = "独占进程";
}

/// <summary>把内存占用单独拎出来，跑在独占集合里 —— 见 <see cref="ExclusiveRun"/>。</summary>
[Collection(ExclusiveRun.Name)]
public sealed class MemoryFootprintTests
{
    [Fact]
    public async Task 内存占用不随内容大小线性增长()
    {
        // 若哪天有人把整个文件读进内存，这条会失败
        using var harness = new TransferHarness
        {
            Parameters = NexusP2P.Core.Hashing.MerkleParameters.Default,
        }.With("big.bin", 16 * 1024 * 1024);

        var destination = harness.CreateTemporaryDirectory();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetTotalMemory(true);

        var run = await harness.RunAsync(destination, maxMessageSize: 256 * 1024);

        Assert.True(run.Completed, $"{run.SenderError}；{run.ReceiverError}");

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var after = GC.GetTotalMemory(true);

        // 16 MiB 内容，允许 8 MiB 的常驻增长（缓冲区、清单、位图）。
        // 真正要抓的是「把整个文件读进内存」那种线性增长。
        var growth = after - before;
        Assert.True(growth < 8L * 1024 * 1024,
            $"传输 16 MiB 后托管堆增长了 {growth / 1024 / 1024} MiB，疑似把内容整体留在内存里");
    }
}
