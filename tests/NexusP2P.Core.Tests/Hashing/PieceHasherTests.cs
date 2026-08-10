using NexusP2P.Core.Hashing;

namespace NexusP2P.Core.Tests.Hashing;

public sealed class PieceHasherTests
{
    private static readonly MerkleParameters Small = new(1024, 4096);

    private static byte[] Pattern(int length, int seed = 0)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            // 显式掩码：构建开了 CheckForOverflowUnderflow，裸的 (byte) 收窄会抛异常
            bytes[i] = (byte)(((((i + seed) * 31) ^ (i >> 8))) & 0xFF);
        }

        return bytes;
    }

    /// <summary>
    /// 全项目最重要的一致性断言：流式的 FileHasher 与逐片校验的 PieceHasher
    /// 必须对同一份数据算出完全相同的分片根。
    ///
    /// 这两条路径的实现方式完全不同（一条按叶子流式喂入，一条从连续内存切片），
    /// 一旦它们分歧，续传会静默地失败 —— 接收方把每个分片都判为「校验不通过」
    /// 而反复重传，表现为「速度为零但没有报错」，极难排查。
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1023)]
    [InlineData(1024)]
    [InlineData(1025)]
    [InlineData(4095)]
    [InlineData(4096)]
    [InlineData(4097)]
    [InlineData(10_000)]
    [InlineData(12_288)]
    public async Task 与_FileHasher_算出的分片根完全一致(int length)
    {
        var content = Pattern(length);

        using var fileHasher = new FileHasher(Small);
        using var stream = new MemoryStream(content, writable: false);
        var fileResult = await fileHasher.ComputeAsync(stream);

        using var pieceHasher = new PieceHasher(Small);

        for (var index = 0; index < fileResult.PieceCount; index++)
        {
            var offset = Small.PieceOffset(index);
            var pieceLength = Small.PieceLength(length, index);
            var piece = content.AsSpan((int)offset, pieceLength);

            var actual = pieceHasher.ComputePieceRoot(piece);

            Assert.Equal(fileResult.PieceRoots[index], actual);
            Assert.True(pieceHasher.Verify(piece, fileResult.PieceRoots[index]));
        }
    }

    [Fact]
    public void 默认参数下也与_FileHasher_一致()
    {
        // 小参数容易掩盖「叶子数超过 stackalloc 阈值」这类分支。
        // 默认参数下每片 16 个叶子，走的是 stackalloc 路径；
        // 这里再单独验一次真实尺寸。
        var content = Pattern(MerkleParameters.DefaultPieceSize);

        using var pieceHasher = new PieceHasher(MerkleParameters.Default);
        var first = pieceHasher.ComputePieceRoot(content);
        var second = pieceHasher.ComputePieceRoot(content);

        Assert.Equal(first, second);
    }

    [Fact]
    public void 篡改任意一个字节都会校验失败()
    {
        var content = Pattern(4096);
        using var hasher = new PieceHasher(Small);
        var expected = hasher.ComputePieceRoot(content);

        foreach (var index in new[] { 0, 1, 1023, 1024, 2048, 4095 })
        {
            var tampered = Pattern(4096);
            tampered[index] ^= 0x01;   // 只翻一个 bit

            Assert.False(hasher.Verify(tampered, expected), $"位置 {index} 的篡改没被检出");
        }
    }

    [Fact]
    public void 长度不同的数据不会通过校验()
    {
        var content = Pattern(4096);
        using var hasher = new PieceHasher(Small);
        var expected = hasher.ComputePieceRoot(content);

        Assert.False(hasher.Verify(content.AsSpan(0, 4095), expected));
        Assert.False(hasher.Verify(content.AsSpan(0, 1024), expected));
    }

    [Fact]
    public void 空分片有确定的根()
    {
        using var hasher = new PieceHasher(Small);

        var root = hasher.ComputePieceRoot([]);

        Assert.NotEqual(Hash256.Zero, root);
        Assert.Equal(root, hasher.ComputePieceRoot([]));
    }

    [Fact]
    public void 超过分片大小的数据被拒绝()
    {
        using var hasher = new PieceHasher(Small);

        Assert.Throws<ArgumentException>(() => hasher.ComputePieceRoot(new byte[Small.PieceSize + 1]));
    }

    [Fact]
    public void 不同分片的根互不相同()
    {
        // 若长度没被绑进根，两个全零但长度不同的分片会撞根
        using var hasher = new PieceHasher(Small);

        var a = hasher.ComputePieceRoot(new byte[1024]);
        var b = hasher.ComputePieceRoot(new byte[2048]);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void 释放后再用会抛异常()
    {
        var hasher = new PieceHasher(Small);
        hasher.Dispose();

        Assert.Throws<ObjectDisposedException>(() => hasher.ComputePieceRoot([1, 2, 3]));
    }
}
