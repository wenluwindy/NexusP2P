using System.Security.Cryptography;
using System.Text;
using NexusP2P.Core.Hashing;

namespace NexusP2P.Core.Tests.Hashing;

/// <summary>
/// 固定测试向量 —— 把哈希语义钉死。
///
/// <para>这些值不是从规范抄来的（我们的构造是自定义的），而是本实现第一次
/// 跑出来后固化下来的。它们的作用是<b>回归护栏</b>：任何重构一旦改变了
/// 域分隔前缀、折叠顺序、长度绑定方式或字节序，这些断言就会失败。</para>
///
/// <para>为什么重要：哈希语义一旦悄悄变了，老的 <c>.part</c> 文件会全部
/// 校验失败，续传静默失效，而且表现为「速度为零但不报错」，极难排查。
/// 若确实要改语义，必须是有意识的决定 —— 那时同步更新这些值，
/// 并意识到它破坏了与旧 <c>.part</c> 文件的兼容。</para>
/// </summary>
public sealed class KnownVectorsTests
{
    private static async Task<FileHashResult> HashAsync(byte[] content)
    {
        using var hasher = new FileHasher(MerkleParameters.Default);
        using var stream = new MemoryStream(content, writable: false);
        return await hasher.ComputeAsync(stream);
    }

    [Fact]
    public void 空叶子的哈希等于单字节零的_SHA256()
    {
        // 这条从外部确证域分隔前缀真的被用上了：
        // 空叶子哈希的输入是 [0x00]（前缀）而不是空输入。
        using var hasher = new MerkleHasher();

        var leafOfEmpty = hasher.HashLeaf([]);
        var sha256OfSingleZeroByte = new Hash256(SHA256.HashData([0x00]));
        var sha256OfNothing = new Hash256(SHA256.HashData([]));

        Assert.Equal(sha256OfSingleZeroByte, leafOfEmpty);
        Assert.NotEqual(sha256OfNothing, leafOfEmpty);
    }

    [Theory]
    [InlineData("", "6e340b9cffb37a989ca544e6bb780a2c78901d3fb33738768511a30617afa01d")]
    [InlineData("hello", "8a2a5c9b768827de5a9552c38a044c66959c68f6d2f21b5260af54d2f87db827")]
    public void 叶子哈希向量(string text, string expected)
    {
        using var hasher = new MerkleHasher();

        var actual = hasher.HashLeaf(Encoding.UTF8.GetBytes(text));

        Assert.Equal(expected, actual.ToString());
    }

    [Fact]
    public void 节点哈希向量()
    {
        using var hasher = new MerkleHasher();

        var actual = hasher.HashNode(Hash256.Zero, Hash256.Zero);

        Assert.Equal("ae0798d0ecaed2b778eddebf18f071a561c53658c05e76cedecc27cafbdbc577", actual.ToString());
    }

    [Fact]
    public async Task 空文件的根()
    {
        var result = await HashAsync([]);

        Assert.Equal("cf47a4b1ae0e5cf4bfc325eb995203718a18692fda59074b3cbd9809d5f98227", result.Root.ToString());
        Assert.Equal(0, result.Length);
        Assert.Equal(1, result.PieceCount);
    }

    [Fact]
    public async Task 五字节内容的根()
    {
        var result = await HashAsync(Encoding.UTF8.GetBytes("hello"));

        Assert.Equal("f7781da690db514af7720196438ff907268bbaa5a65926b0a936445d3ca46bcd", result.Root.ToString());
    }

    [Fact]
    public async Task 单个零字节的根不等于空文件的根()
    {
        // 长度被绑进根，所以「空」和「一个零字节」必然不同。
        // 若哪天长度绑定被去掉，这两个会撞根。
        var oneZeroByte = await HashAsync(new byte[1]);
        var empty = await HashAsync([]);

        Assert.Equal("ecfcc24c6b6129fc16dd6c562b73f8591eb23fa0a7394d57baa210371d46b3fd", oneZeroByte.Root.ToString());
        Assert.NotEqual(empty.Root, oneZeroByte.Root);
    }

    [Fact]
    public async Task 恰好一个叶子的根()
    {
        var result = await HashAsync(new byte[MerkleParameters.DefaultLeafSize]);

        Assert.Equal("7cbb44ef39d389f45f0634ec4294d8900e9863eb347f3f601395731156cbd122", result.Root.ToString());
        Assert.Equal(1, result.PieceCount);
    }

    [Fact]
    public async Task 恰好一个分片的根()
    {
        var result = await HashAsync(new byte[MerkleParameters.DefaultPieceSize]);

        Assert.Equal("c7c614582b09d7b416aa0630250e130d60fba43e8852554721550e6a6eada6c4", result.Root.ToString());
        Assert.Equal(1, result.PieceCount);
    }

    [Fact]
    public async Task 跨过分片边界一个字节的根()
    {
        // 分片边界 +1 是最容易写错的地方：末片只有一个字节、一个叶子
        var result = await HashAsync(new byte[MerkleParameters.DefaultPieceSize + 1]);

        Assert.Equal("2964a16460364dd7391025f5ad80db9e1c3de545d4c353ee753d667e1703fef8", result.Root.ToString());
        Assert.Equal(2, result.PieceCount);
    }
}
