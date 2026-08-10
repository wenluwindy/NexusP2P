using System.Buffers.Binary;
using System.Security.Cryptography;

namespace NexusP2P.Core.Hashing;

/// <summary>
/// Merkle 树的哈希原语。
///
/// <para><b>域分隔（domain separation）</b>：叶子、内部节点、分片根、文件根
/// 四类哈希各自带一个不同的前缀字节。这不是装饰 —— 它有两个实际作用：</para>
/// <list type="number">
/// <item>抵御第二原像攻击（同 RFC 6962 / Certificate Transparency 的做法）：
/// 攻击者无法把一棵子树的根冒充成一个叶子。</item>
/// <item>让「奇数节点直接上提」这种折叠方式变得安全。若没有域分隔，
/// 一个被上提的叶子哈希可能与某个内部节点哈希相等，造成树形歧义；
/// 有了域分隔，两者的哈希输入前缀不同，要相等就得先攻破 SHA-256。</item>
/// </list>
///
/// <para><b>长度绑定</b>：分片根与文件根都把长度混进哈希。这样根本身就是
/// 自描述的，不依赖清单里的长度字段来消除歧义。</para>
///
/// <para>本类型不是线程安全的 —— 它持有一个可复用的 <see cref="IncrementalHash"/>。
/// 每个线程各建一个。</para>
/// </summary>
public sealed class MerkleHasher : IDisposable
{
    private const byte LeafPrefix = 0x00;
    private const byte NodePrefix = 0x01;
    private const byte PieceRootPrefix = 0x02;
    private const byte FileRootPrefix = 0x03;

    private readonly IncrementalHash _sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private bool _disposed;

    /// <summary>叶子哈希：<c>SHA256(0x00 ‖ data)</c>。data 可以为空（空文件的唯一叶子）。</summary>
    public Hash256 HashLeaf(ReadOnlySpan<byte> data)
    {
        ThrowIfDisposed();

        Span<byte> prefix = [LeafPrefix];
        _sha.AppendData(prefix);
        _sha.AppendData(data);
        return GetHashAndReset();
    }

    /// <summary>内部节点：<c>SHA256(0x01 ‖ left ‖ right)</c>。</summary>
    public Hash256 HashNode(Hash256 left, Hash256 right)
    {
        ThrowIfDisposed();

        Span<byte> buffer = stackalloc byte[1 + (Hash256.Size * 2)];
        buffer[0] = NodePrefix;
        left.CopyTo(buffer.Slice(1, Hash256.Size));
        right.CopyTo(buffer.Slice(1 + Hash256.Size, Hash256.Size));

        Span<byte> digest = stackalloc byte[Hash256.Size];
        SHA256.HashData(buffer, digest);
        return new Hash256(digest);
    }

    /// <summary>
    /// 把一层哈希折叠成根。<b>原地修改</b> <paramref name="hashes"/>。
    /// 某层节点数为奇数时，最后一个节点直接上提（不复制、不补位）——
    /// 见类型注释里关于域分隔为何让这么做安全的说明。
    /// </summary>
    public Hash256 ComputeRoot(Span<Hash256> hashes)
    {
        ThrowIfDisposed();

        if (hashes.IsEmpty)
        {
            throw new ArgumentException("至少需要一个哈希才能折叠出根。", nameof(hashes));
        }

        var count = hashes.Length;
        while (count > 1)
        {
            var written = 0;
            for (var i = 0; i < count; i += 2)
            {
                // 原地写入是安全的：written 永远不超过 i
                hashes[written++] = i + 1 < count
                    ? HashNode(hashes[i], hashes[i + 1])
                    : hashes[i];
            }

            count = written;
        }

        return hashes[0];
    }

    /// <summary>
    /// 分片根：<c>SHA256(0x02 ‖ pieceLength_be32 ‖ merkleRoot(叶子哈希))</c>。
    ///
    /// <para><b>这是分片根的唯一计算入口</b>。流式的 <see cref="FileHasher"/> 与
    /// 校验用的 <see cref="PieceHasher"/> 都必须走这里 ——
    /// 两条独立实现哪天算出不同的根，整个续传机制就会静默地烂掉。</para>
    /// </summary>
    public Hash256 ComputePieceRoot(Span<Hash256> leafHashes, int pieceLength)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(pieceLength);

        var subtree = ComputeRoot(leafHashes);
        return BindLength(PieceRootPrefix, pieceLength, subtree);
    }

    /// <summary>
    /// 文件根：<c>SHA256(0x03 ‖ fileLength_be64 ‖ merkleRoot(分片根))</c>。
    /// </summary>
    public Hash256 ComputeFileRoot(Span<Hash256> pieceRoots, long fileLength)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(fileLength);

        var subtree = ComputeRoot(pieceRoots);
        return BindLength(FileRootPrefix, fileLength, subtree);
    }

    private Hash256 BindLength(byte domain, long length, Hash256 subtreeRoot)
    {
        Span<byte> buffer = stackalloc byte[1 + sizeof(long) + Hash256.Size];
        buffer[0] = domain;
        BinaryPrimitives.WriteInt64BigEndian(buffer.Slice(1, sizeof(long)), length);
        subtreeRoot.CopyTo(buffer.Slice(1 + sizeof(long), Hash256.Size));

        Span<byte> digest = stackalloc byte[Hash256.Size];
        SHA256.HashData(buffer, digest);
        return new Hash256(digest);
    }

    private Hash256 GetHashAndReset()
    {
        Span<byte> digest = stackalloc byte[Hash256.Size];
        var written = _sha.GetHashAndReset(digest);
        return written == Hash256.Size
            ? new Hash256(digest)
            : throw new CryptographicException($"SHA-256 返回了 {written} 字节，预期 {Hash256.Size} 字节。");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sha.Dispose();
    }
}
