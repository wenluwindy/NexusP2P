using System.Buffers.Binary;
using System.Security.Cryptography;
using NexusP2P.Core.Hashing;

namespace NexusP2P.Transfer.Storage;

/// <summary>
/// <c>.meta</c> 文件的编解码：记录「哪些分片已完成」。
///
/// <para><b>它只是加速手段，不是依赖。</b>丢失或损坏时退化为对 <c>.part</c>
/// 全量重扫（用 Merkle 逐分片校验）。所以这里对任何异常都返回「读不出来」
/// 而不是抛错 —— 让调用方走重扫那条路，比让用户看到一个崩溃更有用。</para>
///
/// <para>末尾带一个自身内容的 SHA-256。断电时 <c>.meta</c> 很可能只写了一半，
/// 而一个半截的位图会让接收方以为某些分片已完成 —— 那是静默的数据损坏。
/// 校验和把这种情况变成「读不出来 → 重扫」。</para>
/// </summary>
public static class PartFileMeta
{
    private static readonly byte[] Magic = "NXP2PMET"u8.ToArray();
    private const byte FormatVersion = 1;

    /// <summary>魔数 + 版本 + 清单哈希 + 分片数。</summary>
    private const int HeaderSize = 8 + 1 + Hash256.Size + 4;

    public static byte[] Serialize(Hash256 manifestHash, PieceBitfield bitfield)
    {
        ArgumentNullException.ThrowIfNull(bitfield);

        var bitmapBytes = (bitfield.Count + 7) / 8;
        var body = new byte[HeaderSize + bitmapBytes];

        Magic.CopyTo(body.AsSpan(0));
        body[8] = FormatVersion;
        manifestHash.CopyTo(body.AsSpan(9, Hash256.Size));
        BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(9 + Hash256.Size), bitfield.Count);

        // 直接复用位图的线上格式（前 4 字节是分片数），跳过它只取位图字节
        var serialized = bitfield.Serialize();
        serialized.AsSpan(sizeof(int)).CopyTo(body.AsSpan(HeaderSize));

        var result = new byte[body.Length + Hash256.Size];
        body.CopyTo(result.AsSpan(0));
        SHA256.HashData(body, result.AsSpan(body.Length, Hash256.Size));
        return result;
    }

    /// <summary>
    /// 解析。任何不一致都返回 false 而不抛异常 —— 调用方据此退化为全量重扫。
    /// </summary>
    public static bool TryDeserialize(
        ReadOnlySpan<byte> data,
        Hash256 expectedManifestHash,
        int expectedPieceCount,
        out PieceBitfield? bitfield,
        out string? reason)
    {
        bitfield = null;

        var bitmapBytes = (expectedPieceCount + 7) / 8;
        var expectedLength = HeaderSize + bitmapBytes + Hash256.Size;

        if (data.Length != expectedLength)
        {
            reason = $".meta 应为 {expectedLength} 字节，实际 {data.Length} 字节（很可能是断电写了一半）。";
            return false;
        }

        if (!data[..8].SequenceEqual(Magic))
        {
            reason = ".meta 魔数不匹配。";
            return false;
        }

        if (data[8] != FormatVersion)
        {
            reason = $".meta 版本 {data[8]} 不受支持。";
            return false;
        }

        var body = data[..^Hash256.Size];
        Span<byte> computed = stackalloc byte[Hash256.Size];
        SHA256.HashData(body, computed);
        if (!computed.SequenceEqual(data[^Hash256.Size..]))
        {
            reason = ".meta 校验和不匹配，内容已损坏。";
            return false;
        }

        var storedManifestHash = new Hash256(data.Slice(9, Hash256.Size));
        if (storedManifestHash != expectedManifestHash)
        {
            reason = ".meta 属于另一次传输（清单哈希不同）。";
            return false;
        }

        var storedCount = BinaryPrimitives.ReadInt32BigEndian(data[(9 + Hash256.Size)..]);
        if (storedCount != expectedPieceCount)
        {
            reason = $".meta 记录了 {storedCount} 个分片，本次是 {expectedPieceCount} 个。";
            return false;
        }

        // 拼回位图的线上格式再交给它自己校验（含末字节越界位检查）
        var forBitfield = new byte[sizeof(int) + bitmapBytes];
        BinaryPrimitives.WriteInt32BigEndian(forBitfield, expectedPieceCount);
        data.Slice(HeaderSize, bitmapBytes).CopyTo(forBitfield.AsSpan(sizeof(int)));

        try
        {
            bitfield = PieceBitfield.Deserialize(forBitfield, expectedPieceCount);
        }
        catch (ArgumentException ex)
        {
            reason = $".meta 里的位图不合法：{ex.Message}";
            return false;
        }

        reason = null;
        return true;
    }
}
