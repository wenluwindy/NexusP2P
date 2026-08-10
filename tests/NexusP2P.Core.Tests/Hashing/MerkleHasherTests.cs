using System.Security.Cryptography;
using NexusP2P.Core.Hashing;

namespace NexusP2P.Core.Tests.Hashing;

public sealed class MerkleHasherTests
{
    private static Hash256 H(byte seed)
    {
        var bytes = new byte[Hash256.Size];
        Array.Fill(bytes, seed);
        return new Hash256(bytes);
    }

    [Fact]
    public void 叶子哈希不等于裸_SHA256()
    {
        // 域分隔的核心断言：叶子哈希带前缀，不能等于对同样字节做裸 SHA-256。
        // 若这条失效，说明前缀没生效，第二原像防御也就没了。
        using var hasher = new MerkleHasher();
        var data = "hello"u8;

        var leaf = hasher.HashLeaf(data);
        var raw = new Hash256(SHA256.HashData(data));

        Assert.NotEqual(raw, leaf);
    }

    [Fact]
    public void 叶子与节点使用不同的域()
    {
        using var hasher = new MerkleHasher();

        // 构造一个「叶子内容恰好是两个哈希拼接」的情形。
        // 没有域分隔时它会与 HashNode(a, b) 相等 —— 这正是树形歧义的来源。
        var a = H(0xAA);
        var b = H(0xBB);

        var concatenated = new byte[Hash256.Size * 2];
        a.CopyTo(concatenated.AsSpan(0, Hash256.Size));
        b.CopyTo(concatenated.AsSpan(Hash256.Size, Hash256.Size));

        Assert.NotEqual(hasher.HashLeaf(concatenated), hasher.HashNode(a, b));
    }

    [Fact]
    public void 节点哈希对左右顺序敏感()
    {
        using var hasher = new MerkleHasher();

        Assert.NotEqual(hasher.HashNode(H(1), H(2)), hasher.HashNode(H(2), H(1)));
    }

    [Fact]
    public void 单个哈希的根就是它自己()
    {
        using var hasher = new MerkleHasher();
        var only = H(0x42);

        Assert.Equal(only, hasher.ComputeRoot([only]));
    }

    [Fact]
    public void 两个哈希的根是它们的节点哈希()
    {
        using var hasher = new MerkleHasher();
        var expected = hasher.HashNode(H(1), H(2));

        Assert.Equal(expected, hasher.ComputeRoot([H(1), H(2)]));
    }

    [Fact]
    public void 奇数节点时最后一个上提而非复制()
    {
        using var hasher = new MerkleHasher();

        // 三个叶子：期望 Node(Node(h1,h2), h3)，而不是 Node(Node(h1,h2), Node(h3,h3))
        var promoted = hasher.HashNode(hasher.HashNode(H(1), H(2)), H(3));
        var duplicated = hasher.HashNode(hasher.HashNode(H(1), H(2)), hasher.HashNode(H(3), H(3)));

        var actual = hasher.ComputeRoot([H(1), H(2), H(3)]);

        Assert.Equal(promoted, actual);
        Assert.NotEqual(duplicated, actual);
    }

    [Fact]
    public void 空列表被拒绝()
    {
        using var hasher = new MerkleHasher();

        Assert.Throws<ArgumentException>(() => hasher.ComputeRoot([]));
    }

    [Fact]
    public void 长度被绑进分片根()
    {
        using var hasher = new MerkleHasher();
        var leaves = new[] { H(1), H(2) };

        var atLength100 = hasher.ComputePieceRoot([.. leaves], 100);
        var atLength200 = hasher.ComputePieceRoot([.. leaves], 200);

        Assert.NotEqual(atLength100, atLength200);
    }

    [Fact]
    public void 长度被绑进文件根()
    {
        using var hasher = new MerkleHasher();
        var pieces = new[] { H(1), H(2) };

        Assert.NotEqual(
            hasher.ComputeFileRoot([.. pieces], 100),
            hasher.ComputeFileRoot([.. pieces], 200));
    }

    [Fact]
    public void 分片根与文件根使用不同的域()
    {
        using var hasher = new MerkleHasher();
        var same = new[] { H(7) };

        Assert.NotEqual(
            hasher.ComputePieceRoot([.. same], 64),
            hasher.ComputeFileRoot([.. same], 64));
    }

    [Fact]
    public void ComputeRoot_会原地修改传入的_span()
    {
        // 这是刻意的行为（避免为大列表再分配一份），但很容易踩坑，
        // 所以用测试把它钉住 —— FileHasher 依赖这个事实才传副本进去。
        using var hasher = new MerkleHasher();
        var buffer = new[] { H(1), H(2) };
        var before = buffer[0];

        hasher.ComputeRoot(buffer);

        Assert.NotEqual(before, buffer[0]);
    }

    [Fact]
    public void 释放后再用会抛异常()
    {
        var hasher = new MerkleHasher();
        hasher.Dispose();

        Assert.Throws<ObjectDisposedException>(() => hasher.HashLeaf("x"u8));
    }

    [Fact]
    public void 重复释放不抛异常()
    {
        var hasher = new MerkleHasher();

        hasher.Dispose();
        hasher.Dispose();
    }

    [Fact]
    public void 复用同一个实例算出的哈希保持一致()
    {
        // IncrementalHash 是复用的，如果哪次忘了 Reset，
        // 第二次调用就会把前一次的数据带进来。
        using var hasher = new MerkleHasher();
        var data = "重复调用"u8;

        var first = hasher.HashLeaf(data);
        hasher.HashLeaf("干扰数据"u8);
        var third = hasher.HashLeaf(data);

        Assert.Equal(first, third);
    }
}
