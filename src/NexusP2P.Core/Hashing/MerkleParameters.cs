namespace NexusP2P.Core.Hashing;

/// <summary>
/// 分片与叶子块的尺寸参数。
///
/// 叶子 64 KiB 而不是 BEP-52 规定的 16 KiB —— 我们只借用它的 Merkle 树结构，
/// 不与 BitTorrent 互操作，所以可以把哈希次数降到 1/4。
/// 尺寸写进传输清单，日后可调而不破坏兼容。
/// </summary>
public sealed record MerkleParameters
{
    /// <summary>默认叶子块大小：64 KiB。</summary>
    public const int DefaultLeafSize = 64 * 1024;

    /// <summary>默认分片大小：1 MiB，即 16 个叶子。</summary>
    public const int DefaultPieceSize = 1024 * 1024;

    /// <summary>叶子块最小值。太小会让哈希次数与分片根列表都暴涨。</summary>
    public const int MinLeafSize = 1024;

    public static MerkleParameters Default { get; } = new(DefaultLeafSize, DefaultPieceSize);

    public int LeafSize { get; }

    public int PieceSize { get; }

    /// <summary>每个分片包含多少个叶子。</summary>
    public int LeavesPerPiece => PieceSize / LeafSize;

    public MerkleParameters(int leafSize, int pieceSize)
    {
        if (leafSize < MinLeafSize)
        {
            throw new ArgumentOutOfRangeException(nameof(leafSize),
                leafSize, $"叶子块不得小于 {MinLeafSize} 字节。");
        }

        if (!int.IsPow2(leafSize))
        {
            throw new ArgumentOutOfRangeException(nameof(leafSize),
                leafSize, "叶子块大小必须是 2 的幂。");
        }

        if (pieceSize < leafSize)
        {
            throw new ArgumentOutOfRangeException(nameof(pieceSize),
                pieceSize, $"分片（{pieceSize}）不得小于叶子块（{leafSize}）。");
        }

        if (pieceSize % leafSize != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pieceSize),
                pieceSize, $"分片大小必须是叶子块大小（{leafSize}）的整数倍。");
        }

        LeafSize = leafSize;
        PieceSize = pieceSize;
    }

    /// <summary>
    /// 指定长度的内容会被切成多少个分片。
    /// 空内容也算 <b>一个</b> 分片（含一个空叶子），这样「分片数为 0」这种
    /// 需要特殊处理的状态就不存在了。
    /// </summary>
    public long PieceCount(long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return length == 0 ? 1 : (length + PieceSize - 1) / PieceSize;
    }

    /// <summary>第 <paramref name="pieceIndex"/> 个分片的实际字节数（末片可能不足）。</summary>
    public int PieceLength(long contentLength, long pieceIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(contentLength);
        ArgumentOutOfRangeException.ThrowIfNegative(pieceIndex);

        var count = PieceCount(contentLength);
        if (pieceIndex >= count)
        {
            throw new ArgumentOutOfRangeException(nameof(pieceIndex),
                pieceIndex, $"内容长度 {contentLength} 只有 {count} 个分片。");
        }

        var offset = pieceIndex * PieceSize;
        var remaining = contentLength - offset;
        return (int)Math.Min(remaining, PieceSize);
    }

    /// <summary>第 <paramref name="pieceIndex"/> 个分片在内容中的起始偏移。</summary>
    public long PieceOffset(long pieceIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pieceIndex);
        return pieceIndex * PieceSize;
    }
}
