using System.Collections.Immutable;

namespace NexusP2P.Core.Hashing;

/// <summary>一次文件哈希的结果：文件根、逐分片的根、以及实际读到的长度。</summary>
public sealed record FileHashResult(Hash256 Root, ImmutableArray<Hash256> PieceRoots, long Length)
{
    public long PieceCount => PieceRoots.Length;

    // ImmutableArray<T> 实现的 IEquatable 比较的是**底层数组的引用**，不是逐元素。
    // record 自动生成的 Equals 会用上它，于是两份内容完全相同的结果会判为不等 ——
    // 这与 record 的值语义外观直接矛盾，是个很容易埋很久的坑。
    //
    // 改成按 Root 比较。这不是走捷径而是更准确的语义：Root 的构造方式
    // （SHA256(0x03 ‖ 长度 ‖ merkle(分片根))）已经把长度和全部分片根都
    // 密码学地承诺进去了，所以 Root 相等就等价于内容相等。
    public bool Equals(FileHashResult? other) => other is not null && Root == other.Root;

    public override int GetHashCode() => Root.GetHashCode();
}

/// <summary>
/// 流式计算文件的分片根与文件根。
///
/// <para>刻意不要求流可 seek、也不要求预先知道长度 —— 长度是读出来的。
/// 这样同一份代码既能处理磁盘文件，也能处理管道或网络流。</para>
///
/// <para>内存占用与文件大小无关：只持有一个叶子缓冲区（默认 64 KiB）
/// 加上分片根列表（20 GiB 文件约 640 KiB）。</para>
/// </summary>
public sealed class FileHasher(MerkleParameters parameters) : IDisposable
{
    private readonly MerkleHasher _hasher = new();
    private bool _disposed;

    public MerkleParameters Parameters { get; } = parameters;

    /// <summary>
    /// 读完 <paramref name="stream"/> 并算出全部哈希。
    /// <paramref name="progress"/> 报告的是已读字节数。
    ///
    /// <para><b>回调必须线程安全。</b><see cref="Progress{T}"/> 在没有
    /// <see cref="SynchronizationContext"/> 时把回调投到线程池，多次
    /// <c>Report</c> 可能并发执行，而且到达顺序不保证单调。
    /// 往非线程安全的容器里 <c>Add</c> 会直接炸在线程池线程上。</para>
    /// </summary>
    public async Task<FileHashResult> ComputeAsync(
        Stream stream,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(stream);

        var leafBuffer = new byte[Parameters.LeafSize];
        var leafHashes = new Hash256[Parameters.LeavesPerPiece];
        var pieceRoots = ImmutableArray.CreateBuilder<Hash256>();

        long totalLength = 0;
        var leavesInPiece = 0;
        var pieceLength = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var read = await stream
                .ReadAtLeastAsync(leafBuffer, leafBuffer.Length, throwOnEndOfStream: false, cancellationToken)
                .ConfigureAwait(false);

            var atEnd = read < leafBuffer.Length;

            // read == 0 且已经读过内容 -> 干净的结尾，不产出空叶子。
            // 但完全空的流必须产出恰好一个空叶子，好让「分片数为 0」这种
            // 需要特殊处理的状态不存在（见 MerkleParameters.PieceCount）。
            if (read > 0 || totalLength == 0)
            {
                leafHashes[leavesInPiece++] = _hasher.HashLeaf(leafBuffer.AsSpan(0, read));
                totalLength += read;
                pieceLength += read;
            }

            var pieceFull = leavesInPiece == Parameters.LeavesPerPiece;
            if (pieceFull || (atEnd && leavesInPiece > 0))
            {
                pieceRoots.Add(_hasher.ComputePieceRoot(leafHashes.AsSpan(0, leavesInPiece), pieceLength));
                leavesInPiece = 0;
                pieceLength = 0;
            }

            if (read > 0)
            {
                progress?.Report(totalLength);
            }

            if (atEnd)
            {
                break;
            }
        }

        var roots = pieceRoots.ToImmutable();

        // ComputeFileRoot 会原地修改传入的 span，所以给它一份副本，
        // 免得把要返回给调用方的分片根列表毁掉。
        var scratch = roots.ToArray();
        var fileRoot = _hasher.ComputeFileRoot(scratch, totalLength);

        return new FileHashResult(fileRoot, roots, totalLength);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hasher.Dispose();
    }
}
