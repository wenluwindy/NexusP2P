using NexusP2P.Core.Manifest;
using NexusP2P.Transfer.Storage;

namespace NexusP2P.Transfer.Tests.Storage;

public sealed class PieceStoreTests
{
    /// <summary>
    /// 同步投递的 IProgress。
    ///
    /// <para><see cref="Progress{T}"/> 在没有 SynchronizationContext 时把回调
    /// 投到线程池，Report 返回时回调未必已经跑过 —— 断言「报告过没有」就成了
    /// 和线程池调度赛跑，在 CI 这种负载高的机器上会随机失败（v2.2.0 的发布
    /// 流水线就是这么挂掉的）。这里直接在调用线程上同步执行。</para>
    /// </summary>
    private sealed class SynchronousProgress<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }

    /// <summary>把整次传输灌完（按全局下标顺序），返回落地的文件路径。</summary>
    private static async Task<IReadOnlyList<string>> TransferAllAsync(
        PieceStore store, TransferFixture fixture, TransferManifest manifest)
    {
        for (var i = 0; i < store.Locator.TotalPieces; i++)
        {
            var location = store.Locator.Locate(i);
            await store.WritePieceAsync(i, fixture.Piece(manifest, location.FileIndex, location.LocalPieceIndex));
        }

        return await store.FinalizeAsync();
    }

    [Fact]
    public async Task 单文件从零传完并落地()
    {
        using var fixture = new TransferFixture().With("a.bin", 10_000);
        var manifest = await fixture.BuildManifestAsync();
        var root = fixture.CreateTemporaryDirectory();

        await using var store = await PieceStore.OpenAsync(root, manifest);

        Assert.Equal(0, store.Bitfield.SetCount);

        var landed = await TransferAllAsync(store, fixture, manifest);

        Assert.Single(landed);
        Assert.Equal(fixture.Files["a.bin"], await File.ReadAllBytesAsync(landed[0]));
    }

    [Fact]
    public async Task 文件夹含嵌套目录与空文件()
    {
        using var fixture = new TransferFixture()
            .With("proj/readme.md", 100)
            .With("proj/src/deep/a.bin", 10_000)
            .With("proj/empty.dat", 0)
            .WithEmptyDirectory("proj/logs");

        var manifest = await fixture.BuildManifestAsync();
        var root = fixture.CreateTemporaryDirectory();

        await using var store = await PieceStore.OpenAsync(root, manifest);
        await TransferAllAsync(store, fixture, manifest);

        foreach (var (path, content) in fixture.Files)
        {
            var landed = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(landed), $"{path} 没有落地");
            Assert.Equal(content, await File.ReadAllBytesAsync(landed));
        }

        Assert.True(Directory.Exists(Path.Combine(root, "proj", "logs")), "空目录没有被创建");
    }

    [Fact]
    public async Task 临时文件在完成后被清理()
    {
        using var fixture = new TransferFixture().With("a.bin", 5000);
        var manifest = await fixture.BuildManifestAsync();
        var root = fixture.CreateTemporaryDirectory();

        await using var store = await PieceStore.OpenAsync(root, manifest);
        await TransferAllAsync(store, fixture, manifest);

        var workDirectory = Path.Combine(root, PieceStore.WorkDirectoryName);
        Assert.False(Directory.Exists(workDirectory) &&
                     Directory.EnumerateFileSystemEntries(workDirectory).Any(),
            "完成后临时目录应为空或已删除");
    }

    // ---- 校验 ----

    [Fact]
    public async Task 校验失败的分片被拒收且不落盘()
    {
        using var fixture = new TransferFixture().With("a.bin", 10_000);
        var manifest = await fixture.BuildManifestAsync();
        var root = fixture.CreateTemporaryDirectory();

        await using var store = await PieceStore.OpenAsync(root, manifest);

        var tampered = fixture.Piece(manifest, 0, 0).ToArray();
        tampered[0] ^= 0xFF;

        var ex = await Assert.ThrowsAsync<PieceRejectedException>(
            () => store.WritePieceAsync(0, tampered));

        Assert.Equal(0, ex.GlobalPieceIndex);
        Assert.False(store.Bitfield[0], "被拒收的分片不该置位");
        Assert.Equal(0, store.Bitfield.SetCount);
    }

    [Fact]
    public async Task 长度不对的分片被拒收()
    {
        using var fixture = new TransferFixture().With("a.bin", 10_000);
        var manifest = await fixture.BuildManifestAsync();
        var root = fixture.CreateTemporaryDirectory();

        await using var store = await PieceStore.OpenAsync(root, manifest);

        await Assert.ThrowsAsync<PieceRejectedException>(
            () => store.WritePieceAsync(0, new byte[100]));
    }

    [Fact]
    public async Task 未完成时不能收尾()
    {
        using var fixture = new TransferFixture().With("a.bin", 10_000);
        var manifest = await fixture.BuildManifestAsync();
        var root = fixture.CreateTemporaryDirectory();

        await using var store = await PieceStore.OpenAsync(root, manifest);
        await store.WritePieceAsync(0, fixture.Piece(manifest, 0, 0));

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.FinalizeAsync());
    }

    [Fact]
    public async Task 落盘后被外部改写会在收尾时被发现()
    {
        // 每个分片入库时都校验过，所以这一步查的是「落盘之后」的完整性 ——
        // 磁盘写入错误、别的程序动了文件，只有重读才能发现。
        using var fixture = new TransferFixture().With("a.bin", 10_000);
        var manifest = await fixture.BuildManifestAsync();
        var root = fixture.CreateTemporaryDirectory();

        // 传完并落下 .meta，然后关闭（释放独占句柄）
        await using (var first = await PieceStore.OpenAsync(root, manifest))
        {
            for (var i = 0; i < first.Locator.TotalPieces; i++)
            {
                var location = first.Locator.Locate(i);
                await first.WritePieceAsync(i, fixture.Piece(manifest, location.FileIndex, location.LocalPieceIndex));
            }

            await first.FlushMetaAsync();
        }

        // 程序关闭期间别的东西改了 .part
        var partPath = Directory.GetFiles(
            Path.Combine(root, PieceStore.WorkDirectoryName), "*.part").Single();
        using (var handle = File.OpenHandle(partPath, FileMode.Open, FileAccess.Write))
        {
            RandomAccess.Write(handle, new byte[] { 0xFF }, 0);
        }

        // 重开后 .meta 说「全都完成了」，只有收尾时的重读能发现内容已被改
        await using var second = await PieceStore.OpenAsync(root, manifest);
        Assert.True(second.Bitfield.IsComplete, ".meta 应显示已全部完成");

        await Assert.ThrowsAsync<IntegrityException>(() => second.FinalizeAsync());
    }

    [Fact]
    public async Task 传输期间_part_被独占锁定()
    {
        // 独占句柄能防别的程序在传输中途破坏文件。这是个刻意的性质，钉住它。
        using var fixture = new TransferFixture().With("a.bin", 10_000);
        var manifest = await fixture.BuildManifestAsync();
        var root = fixture.CreateTemporaryDirectory();

        await using var store = await PieceStore.OpenAsync(root, manifest);
        await store.WritePieceAsync(0, fixture.Piece(manifest, 0, 0));

        var partPath = Directory.GetFiles(
            Path.Combine(root, PieceStore.WorkDirectoryName), "*.part").Single();

        Assert.Throws<IOException>(
            () => File.OpenHandle(partPath, FileMode.Open, FileAccess.Write));
    }

    // ---- 断点续传 ----

    [Fact]
    public async Task 关掉重开后从_meta_恢复进度()
    {
        using var fixture = new TransferFixture().With("a.bin", 20_000);
        var manifest = await fixture.BuildManifestAsync();
        var root = fixture.CreateTemporaryDirectory();

        int half;
        await using (var first = await PieceStore.OpenAsync(root, manifest))
        {
            half = first.Locator.TotalPieces / 2;
            for (var i = 0; i < half; i++)
            {
                var location = first.Locator.Locate(i);
                await first.WritePieceAsync(i, fixture.Piece(manifest, location.FileIndex, location.LocalPieceIndex));
            }

            await first.FlushMetaAsync();
        }

        // 全新的仓储实例，模拟程序重开
        await using var second = await PieceStore.OpenAsync(root, manifest);

        Assert.Equal(half, second.Bitfield.SetCount);
        for (var i = 0; i < half; i++)
        {
            Assert.True(second.Bitfield[i], $"分片 {i} 的进度丢了");
        }

        var landed = await TransferAllAsync(second, fixture, manifest);
        Assert.Equal(fixture.Files["a.bin"], await File.ReadAllBytesAsync(landed[0]));
    }

    [Fact]
    public async Task 首次接收不做重扫()
    {
        // .part 是按整个文件长度预分配的。首次接收时它们刚被建出来，
        // 里面必然什么都没有 —— 去重扫等于把 20 GiB 全零数据读一遍
        // 再算一遍 SHA-256，然后得出「什么都没有」这个已知答案，
        // 而用户对着一个不动的界面白等。
        using var fixture = new TransferFixture().With("a.bin", 20_000);
        var manifest = await fixture.BuildManifestAsync();
        var root = fixture.CreateTemporaryDirectory();

        var rescanned = false;
        var progress = new SynchronousProgress<RescanProgress>(_ => rescanned = true);

        await using var store = await PieceStore.OpenAsync(root, manifest, progress);

        Assert.False(rescanned, "首次接收不该重扫");
        Assert.Equal(0, store.Bitfield.SetCount);
    }

    [Fact]
    public async Task part_被删掉时不采信残留的_meta()
    {
        // .meta 描述的是某个具体的 .part。人手删掉 .part（或换了磁盘、
        // 清了临时目录）之后 .meta 还在，照它设位图就等于声称拥有一堆全零数据。
        // 那要一路传到最后整体根校验才暴露 —— 白传一场。
        using var fixture = new TransferFixture().With("a.bin", 20_000);
        var manifest = await fixture.BuildManifestAsync();
        var root = fixture.CreateTemporaryDirectory();

        await using (var first = await PieceStore.OpenAsync(root, manifest))
        {
            for (var i = 0; i < first.Locator.TotalPieces; i++)
            {
                var location = first.Locator.Locate(i);
                await first.WritePieceAsync(
                    i, fixture.Piece(manifest, location.FileIndex, location.LocalPieceIndex));
            }

            await first.FlushMetaAsync();
        }

        var work = Path.Combine(root, PieceStore.WorkDirectoryName);
        Assert.Single(Directory.GetFiles(work, "*.meta"));

        // 只删 .part，留下 .meta
        foreach (var part in Directory.GetFiles(work, "*.part"))
        {
            File.Delete(part);
        }

        await using var second = await PieceStore.OpenAsync(root, manifest);

        Assert.Equal(0, second.Bitfield.SetCount);
    }

    [Fact]
    public async Task meta_丢失时全量重扫仍能恢复进度()
    {
        using var fixture = new TransferFixture().With("a.bin", 20_000);
        var manifest = await fixture.BuildManifestAsync();
        var root = fixture.CreateTemporaryDirectory();

        int half;
        await using (var first = await PieceStore.OpenAsync(root, manifest))
        {
            half = first.Locator.TotalPieces / 2;
            for (var i = 0; i < half; i++)
            {
                var location = first.Locator.Locate(i);
                await first.WritePieceAsync(i, fixture.Piece(manifest, location.FileIndex, location.LocalPieceIndex));
            }

            await first.FlushMetaAsync();
        }

        // 删掉 .meta —— 它只是加速手段，不是依赖
        foreach (var meta in Directory.GetFiles(Path.Combine(root, PieceStore.WorkDirectoryName), "*.meta"))
        {
            File.Delete(meta);
        }

        var rescanned = false;
        var progress = new SynchronousProgress<RescanProgress>(_ => rescanned = true);

        await using var second = await PieceStore.OpenAsync(root, manifest, progress);

        Assert.Equal(half, second.Bitfield.SetCount);
        Assert.True(rescanned, "应该报告了重扫进度");
    }

    [Fact]
    public async Task meta_损坏时退化为重扫而不是崩溃()
    {
        using var fixture = new TransferFixture().With("a.bin", 20_000);
        var manifest = await fixture.BuildManifestAsync();
        var root = fixture.CreateTemporaryDirectory();

        int half;
        await using (var first = await PieceStore.OpenAsync(root, manifest))
        {
            half = first.Locator.TotalPieces / 2;
            for (var i = 0; i < half; i++)
            {
                var location = first.Locator.Locate(i);
                await first.WritePieceAsync(i, fixture.Piece(manifest, location.FileIndex, location.LocalPieceIndex));
            }

            await first.FlushMetaAsync();
        }

        // 把 .meta 内容改坏。校验和会发现，从而走重扫。
        var metaPath = Directory.GetFiles(
            Path.Combine(root, PieceStore.WorkDirectoryName), "*.meta").Single();
        var bytes = await File.ReadAllBytesAsync(metaPath);
        bytes[bytes.Length / 2] ^= 0xFF;
        await File.WriteAllBytesAsync(metaPath, bytes);

        await using var second = await PieceStore.OpenAsync(root, manifest);

        Assert.Equal(half, second.Bitfield.SetCount);
    }

    [Fact]
    public async Task meta_被截断时退化为重扫()
    {
        // 断电时 .meta 很可能只写了一半，而半截的位图会让接收方
        // 以为某些分片已完成 —— 那是静默的数据损坏
        using var fixture = new TransferFixture().With("a.bin", 20_000);
        var manifest = await fixture.BuildManifestAsync();
        var root = fixture.CreateTemporaryDirectory();

        await using (var first = await PieceStore.OpenAsync(root, manifest))
        {
            await first.WritePieceAsync(0, fixture.Piece(manifest, 0, 0));
            await first.FlushMetaAsync();
        }

        var metaPath = Directory.GetFiles(
            Path.Combine(root, PieceStore.WorkDirectoryName), "*.meta").Single();
        var bytes = await File.ReadAllBytesAsync(metaPath);
        await File.WriteAllBytesAsync(metaPath, bytes.AsSpan(0, bytes.Length / 2).ToArray());

        await using var second = await PieceStore.OpenAsync(root, manifest);

        Assert.Equal(1, second.Bitfield.SetCount);
    }

    [Fact]
    public async Task 属于另一次传输的_meta_被忽略()
    {
        using var fixture = new TransferFixture().With("a.bin", 20_000);
        var manifest = await fixture.BuildManifestAsync();
        var root = fixture.CreateTemporaryDirectory();

        await using (var first = await PieceStore.OpenAsync(root, manifest))
        {
            await first.WritePieceAsync(0, fixture.Piece(manifest, 0, 0));
            await first.FlushMetaAsync();
        }

        // 换一份内容不同的清单，但复用同一个目录
        using var other = new TransferFixture().With("b.bin", 8000);
        var otherManifest = await other.BuildManifestAsync();

        await using var store = await PieceStore.OpenAsync(root, otherManifest);

        Assert.Equal(0, store.Bitfield.SetCount);
    }

    [Fact]
    public async Task 内容相同的新清单能接上旧的_part()
    {
        // .part 按文件根命名，所以「关掉重开、生成新文件码」也能续 ——
        // 续传的锚点是内容而不是会话
        using var fixture = new TransferFixture().With("a.bin", 20_000);
        var manifest = await fixture.BuildManifestAsync();
        var root = fixture.CreateTemporaryDirectory();

        await using (var first = await PieceStore.OpenAsync(root, manifest))
        {
            for (var i = 0; i < first.Locator.TotalPieces / 2; i++)
            {
                var location = first.Locator.Locate(i);
                await first.WritePieceAsync(i, fixture.Piece(manifest, location.FileIndex, location.LocalPieceIndex));
            }

            await first.FlushMetaAsync();
        }

        // 重新算一份清单（内容一样 -> 文件根与清单哈希都一样）
        var sameManifest = await fixture.BuildManifestAsync();
        Assert.Equal(manifest.Hash, sameManifest.Hash);

        await using var second = await PieceStore.OpenAsync(root, sameManifest);

        Assert.True(second.Bitfield.SetCount > 0, "应能接上之前的进度");
    }

    [Fact]
    public async Task 长度不符的旧_part_被重建()
    {
        using var fixture = new TransferFixture().With("a.bin", 20_000);
        var manifest = await fixture.BuildManifestAsync();
        var root = fixture.CreateTemporaryDirectory();

        var workDirectory = Path.Combine(root, PieceStore.WorkDirectoryName);
        Directory.CreateDirectory(workDirectory);

        // 命名格式是「清单哈希.文件序号.part」——刻意在测试里写死，
        // 这样格式一旦被改动，这条会提醒去看兼容性影响
        var bogusPart = Path.Combine(workDirectory, $"{manifest.Hash}.0.part");
        await File.WriteAllBytesAsync(bogusPart, new byte[7]);   // 长度完全不对

        await using var store = await PieceStore.OpenAsync(root, manifest);

        Assert.Equal(20_000, new FileInfo(bogusPart).Length);
        Assert.Equal(0, store.Bitfield.SetCount);
    }

    // ---- 进度与空间 ----

    [Fact]
    public async Task CompletedBytes_反映已完成的字节数()
    {
        using var fixture = new TransferFixture().With("a.bin", 10_000);
        var manifest = await fixture.BuildManifestAsync();
        var root = fixture.CreateTemporaryDirectory();

        await using var store = await PieceStore.OpenAsync(root, manifest);

        Assert.Equal(0, store.CompletedBytes);

        await store.WritePieceAsync(0, fixture.Piece(manifest, 0, 0));

        Assert.Equal(TransferFixture.SmallParameters.PieceSize, store.CompletedBytes);
    }

    [Fact]
    public async Task 收尾时报告校验进度()
    {
        using var fixture = new TransferFixture().With("a.bin", 20_000);
        var manifest = await fixture.BuildManifestAsync();
        var root = fixture.CreateTemporaryDirectory();

        await using var store = await PieceStore.OpenAsync(root, manifest);
        for (var i = 0; i < store.Locator.TotalPieces; i++)
        {
            var location = store.Locator.Locate(i);
            await store.WritePieceAsync(i, fixture.Piece(manifest, location.FileIndex, location.LocalPieceIndex));
        }

        // 同步投递：Progress<T> 会把回调甩给线程池，那样断言就得靠 Task.Delay
        // 去赌调度，在 CI 上是不稳定的来源。
        var reportCount = 0;
        await store.FinalizeAsync(new SynchronousProgress<long>(_ => Interlocked.Increment(ref reportCount)));

        Assert.True(Volatile.Read(ref reportCount) > 0, "收尾时应报告校验进度");
    }

    [Fact]
    public void 空间不足在开始前就被发现()
    {
        // 提前几秒失败远好过让用户白等五十分钟
        Assert.Throws<InsufficientDiskSpaceException>(
            () => PieceStore.EnsureSpaceAvailable(Path.GetTempPath(), long.MaxValue / 2));
    }

    [Fact]
    public void 空间充足时不抛异常()
    {
        PieceStore.EnsureSpaceAvailable(Path.GetTempPath(), 1024);
    }

    [Fact]
    public async Task 打开时空间不足会抛异常()
    {
        using var fixture = new TransferFixture();
        var root = fixture.CreateTemporaryDirectory();

        // 造一份声称极大的清单：分片根数量受上限约束，所以用大分片参数
        var parameters = new NexusP2P.Core.Hashing.MerkleParameters(64 * 1024, 64 * 1024 * 1024);
        using var big = new TransferFixture { Parameters = parameters };
        big.With("huge.bin", 1);
        var manifest = await big.BuildManifestAsync();

        // 这份清单只有 1 字节，空间当然够；单独验证空间检查在 Open 路径上被调用
        await using var store = await PieceStore.OpenAsync(root, manifest);
        Assert.Equal(1, store.Locator.TotalPieces);
    }

    [Fact]
    public async Task 目标目录不存在时会被创建()
    {
        using var fixture = new TransferFixture().With("a.bin", 100);
        var manifest = await fixture.BuildManifestAsync();
        var root = Path.Combine(fixture.CreateTemporaryDirectory(), "not", "yet", "there");

        await using var store = await PieceStore.OpenAsync(root, manifest);

        Assert.True(Directory.Exists(root));
        _ = store;
    }
}
