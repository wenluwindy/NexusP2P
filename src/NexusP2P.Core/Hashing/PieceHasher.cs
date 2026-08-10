namespace NexusP2P.Core.Hashing;

/// <summary>
/// 对单个分片独立算根、独立校验 —— 不需要整棵树，也不需要任何证明数据。
///
/// <para>这是断点续传与二期 swarm 的地基：接收方拿到一个分片，
/// 只用分片自身的字节和清单里那一个 32 字节的期望根，就能判断它是真是假。
/// 校验失败就丢弃重新请求，绝不落盘。</para>
/// </summary>
public sealed class PieceHasher(MerkleParameters parameters) : IDisposable
{
    private readonly MerkleHasher _hasher = new();
    private bool _disposed;

    public MerkleParameters Parameters { get; } = parameters;

    /// <summary>
    /// 算出一个分片的根。<paramref name="pieceData"/> 长度不得超过
    /// <see cref="MerkleParameters.PieceSize"/>（末片允许不足）。
    /// </summary>
    public Hash256 ComputePieceRoot(ReadOnlySpan<byte> pieceData)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (pieceData.Length > Parameters.PieceSize)
        {
            throw new ArgumentException(
                $"分片数据为 {pieceData.Length} 字节，超过分片大小 {Parameters.PieceSize}。",
                nameof(pieceData));
        }

        var leafCount = pieceData.Length == 0
            ? 1
            : (pieceData.Length + Parameters.LeafSize - 1) / Parameters.LeafSize;

        var leafHashes = leafCount <= 32
            ? stackalloc Hash256[leafCount]
            : new Hash256[leafCount];

        for (var i = 0; i < leafCount; i++)
        {
            var offset = i * Parameters.LeafSize;
            var length = Math.Min(Parameters.LeafSize, pieceData.Length - offset);
            leafHashes[i] = _hasher.HashLeaf(pieceData.Slice(offset, length));
        }

        return _hasher.ComputePieceRoot(leafHashes, pieceData.Length);
    }

    /// <summary>分片数据是否与期望的根一致。</summary>
    public bool Verify(ReadOnlySpan<byte> pieceData, Hash256 expectedRoot) =>
        ComputePieceRoot(pieceData) == expectedRoot;

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
