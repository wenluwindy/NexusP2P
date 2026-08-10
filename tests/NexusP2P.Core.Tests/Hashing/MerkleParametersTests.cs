using NexusP2P.Core.Hashing;

namespace NexusP2P.Core.Tests.Hashing;

public sealed class MerkleParametersTests
{
    [Fact]
    public void 默认值是_64KiB_叶子与_1MiB_分片()
    {
        var p = MerkleParameters.Default;

        Assert.Equal(64 * 1024, p.LeafSize);
        Assert.Equal(1024 * 1024, p.PieceSize);
        Assert.Equal(16, p.LeavesPerPiece);
    }

    [Theory]
    [InlineData(512)]   // 小于 MinLeafSize
    [InlineData(0)]
    [InlineData(-1024)]
    public void 叶子块过小被拒绝(int leafSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MerkleParameters(leafSize, 1024 * 1024));
    }

    [Fact]
    public void 叶子块必须是_2_的幂()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MerkleParameters(3 * 1024, 12 * 1024));
    }

    [Fact]
    public void 分片必须是叶子的整数倍()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MerkleParameters(1024, 1536));
    }

    [Fact]
    public void 分片不得小于叶子()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MerkleParameters(4096, 1024));
    }

    [Fact]
    public void 空内容也算一个分片()
    {
        // 这是刻意的设计：让「分片数为 0」这种需要处处特殊处理的状态不存在
        Assert.Equal(1, MerkleParameters.Default.PieceCount(0));
        Assert.Equal(0, MerkleParameters.Default.PieceLength(0, 0));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1024 * 1024, 1)]           // 恰好一个分片
    [InlineData((1024 * 1024) + 1, 2)]     // 多一个字节就多一片
    [InlineData(3 * 1024 * 1024, 3)]
    [InlineData((3 * 1024 * 1024) - 1, 3)]
    public void 分片数按上取整(long length, long expected)
    {
        Assert.Equal(expected, MerkleParameters.Default.PieceCount(length));
    }

    [Fact]
    public void 末片长度可以不足()
    {
        var p = MerkleParameters.Default;
        var length = (2 * p.PieceSize) + 1234;

        Assert.Equal(3, p.PieceCount(length));
        Assert.Equal(p.PieceSize, p.PieceLength(length, 0));
        Assert.Equal(p.PieceSize, p.PieceLength(length, 1));
        Assert.Equal(1234, p.PieceLength(length, 2));
    }

    [Fact]
    public void 越界的分片下标被拒绝()
    {
        var p = MerkleParameters.Default;

        Assert.Throws<ArgumentOutOfRangeException>(() => p.PieceLength(p.PieceSize, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => p.PieceLength(0, 1));
    }

    [Fact]
    public void 负长度被拒绝()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MerkleParameters.Default.PieceCount(-1));
    }

    [Fact]
    public void 分片偏移是分片大小的整数倍()
    {
        var p = MerkleParameters.Default;

        Assert.Equal(0, p.PieceOffset(0));
        Assert.Equal(p.PieceSize, p.PieceOffset(1));
        Assert.Equal(5L * p.PieceSize, p.PieceOffset(5));
    }
}
