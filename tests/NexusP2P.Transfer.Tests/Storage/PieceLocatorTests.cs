using NexusP2P.Transfer.Storage;

namespace NexusP2P.Transfer.Tests.Storage;

public sealed class PieceLocatorTests
{
    // 小参数：1 KiB 叶子 / 4 KiB 分片
    private const int PieceSize = 4096;

    [Fact]
    public async Task 单文件的全局下标就是文件内下标()
    {
        using var fixture = new TransferFixture().With("a.bin", 10_000);
        var manifest = await fixture.BuildManifestAsync();

        var locator = new PieceLocator(manifest);

        Assert.Equal(3, locator.TotalPieces);   // 10000 -> 3 片
        for (var i = 0; i < 3; i++)
        {
            var location = locator.Locate(i);
            Assert.Equal(0, location.FileIndex);
            Assert.Equal(i, location.LocalPieceIndex);
        }
    }

    [Fact]
    public async Task 多文件的全局下标按清单顺序拼接()
    {
        using var fixture = new TransferFixture()
            .With("a.bin", PieceSize * 2)      // 2 片
            .With("b.bin", PieceSize)          // 1 片
            .With("c.bin", 100);               // 1 片

        var manifest = await fixture.BuildManifestAsync();
        var locator = new PieceLocator(manifest);

        Assert.Equal(4, locator.TotalPieces);
        Assert.Equal(["a.bin", "b.bin", "c.bin"], manifest.Entries.Select(e => e.Path).ToArray());

        Assert.Equal((0, 0L), (locator.Locate(0).FileIndex, locator.Locate(0).LocalPieceIndex));
        Assert.Equal((0, 1L), (locator.Locate(1).FileIndex, locator.Locate(1).LocalPieceIndex));
        Assert.Equal((1, 0L), (locator.Locate(2).FileIndex, locator.Locate(2).LocalPieceIndex));
        Assert.Equal((2, 0L), (locator.Locate(3).FileIndex, locator.Locate(3).LocalPieceIndex));
    }

    [Fact]
    public async Task 全局与局部下标互为逆运算()
    {
        using var fixture = new TransferFixture()
            .With("a.bin", 30_000)
            .With("b.bin", 5000)
            .With("c.bin", 0)
            .With("d.bin", 12_345);

        var manifest = await fixture.BuildManifestAsync();
        var locator = new PieceLocator(manifest);

        for (var global = 0; global < locator.TotalPieces; global++)
        {
            var location = locator.Locate(global);
            Assert.Equal(global, locator.GlobalIndex(location.FileIndex, location.LocalPieceIndex));
        }
    }

    [Fact]
    public async Task 偏移与长度正确()
    {
        using var fixture = new TransferFixture().With("a.bin", (PieceSize * 2) + 123);
        var manifest = await fixture.BuildManifestAsync();
        var locator = new PieceLocator(manifest);

        Assert.Equal(3, locator.TotalPieces);

        Assert.Equal(0, locator.Locate(0).OffsetInFile);
        Assert.Equal(PieceSize, locator.Locate(0).Length);

        Assert.Equal(PieceSize, locator.Locate(1).OffsetInFile);
        Assert.Equal(PieceSize, locator.Locate(1).Length);

        Assert.Equal(PieceSize * 2, locator.Locate(2).OffsetInFile);
        Assert.Equal(123, locator.Locate(2).Length);   // 末片不足
    }

    [Fact]
    public async Task 空文件占一个长度为零的分片()
    {
        using var fixture = new TransferFixture().With("empty.dat", 0);
        var manifest = await fixture.BuildManifestAsync();
        var locator = new PieceLocator(manifest);

        Assert.Equal(1, locator.TotalPieces);
        Assert.Equal(0, locator.Locate(0).Length);
        Assert.Equal(0, locator.Locate(0).OffsetInFile);
    }

    [Fact]
    public async Task 期望根来自清单()
    {
        using var fixture = new TransferFixture().With("a.bin", 10_000);
        var manifest = await fixture.BuildManifestAsync();
        var locator = new PieceLocator(manifest);

        for (var i = 0; i < locator.TotalPieces; i++)
        {
            Assert.Equal(manifest.Entries[0].PieceRoots[i], locator.Locate(i).ExpectedRoot);
        }
    }

    [Fact]
    public async Task FileRange_给出文件的全局下标区间()
    {
        using var fixture = new TransferFixture()
            .With("a.bin", PieceSize * 3)
            .With("b.bin", PieceSize * 2);

        var manifest = await fixture.BuildManifestAsync();
        var locator = new PieceLocator(manifest);

        Assert.Equal((0, 3), locator.FileRange(0));
        Assert.Equal((3, 5), locator.FileRange(1));
    }

    [Fact]
    public async Task 越界的全局下标被拒绝()
    {
        using var fixture = new TransferFixture().With("a.bin", 100);
        var manifest = await fixture.BuildManifestAsync();
        var locator = new PieceLocator(manifest);

        Assert.Throws<ArgumentOutOfRangeException>(() => locator.Locate(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => locator.Locate(locator.TotalPieces));
    }

    [Fact]
    public async Task 越界的文件序号或局部下标被拒绝()
    {
        using var fixture = new TransferFixture().With("a.bin", 100);
        var manifest = await fixture.BuildManifestAsync();
        var locator = new PieceLocator(manifest);

        Assert.Throws<ArgumentOutOfRangeException>(() => locator.GlobalIndex(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => locator.GlobalIndex(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => locator.GlobalIndex(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => locator.FileRange(5));
    }

    [Fact]
    public async Task 大量文件时二分查找正确()
    {
        // 文件数可达 10 万，映射用的是二分。这里验证边界没算错。
        using var fixture = new TransferFixture();
        for (var i = 0; i < 200; i++)
        {
            fixture.With($"f{i:D4}.bin", (i % 3) * PieceSize == 0 ? 1 : (i % 3) * PieceSize);
        }

        var manifest = await fixture.BuildManifestAsync();
        var locator = new PieceLocator(manifest);

        var seen = new HashSet<(int, long)>();
        for (var global = 0; global < locator.TotalPieces; global++)
        {
            var location = locator.Locate(global);

            Assert.True(seen.Add((location.FileIndex, (int)location.LocalPieceIndex)),
                $"全局下标 {global} 映射到了重复的坐标");
            Assert.Equal(global, locator.GlobalIndex(location.FileIndex, location.LocalPieceIndex));
        }

        Assert.Equal(locator.TotalPieces, seen.Count);
    }
}
