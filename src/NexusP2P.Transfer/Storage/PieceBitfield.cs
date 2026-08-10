using System.Buffers.Binary;

namespace NexusP2P.Transfer.Storage;

/// <summary>
/// 「哪些分片已经有了」的位图。
///
/// <para>这是断点续传的核心数据结构，也是二期 swarm 的地基 ——
/// peer 之间交换位图就能知道谁缺什么。所以现在就把它设计对，
/// 日后换调度器（顺序拉 → 稀有块优先）是局部手术。</para>
/// </summary>
public sealed class PieceBitfield
{
    private readonly byte[] _bits;

    public PieceBitfield(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        Count = count;
        _bits = new byte[(count + 7) / 8];
    }

    private PieceBitfield(int count, byte[] bits)
    {
        Count = count;
        _bits = bits;
        SetCount = CountSetBits(bits, count);
    }

    public int Count { get; }

    /// <summary>已置位的数量。维护成增量的，免得每次问进度都扫一遍位图。</summary>
    public int SetCount { get; private set; }

    public bool IsComplete => SetCount == Count;

    public bool this[int index]
    {
        get
        {
            ValidateIndex(index);
            return (_bits[index >> 3] & (1 << (index & 7))) != 0;
        }
    }

    public void Set(int index)
    {
        ValidateIndex(index);

        var mask = (byte)(1 << (index & 7));
        ref var slot = ref _bits[index >> 3];
        if ((slot & mask) == 0)
        {
            slot |= mask;
            SetCount++;
        }
    }

    public void Clear(int index)
    {
        ValidateIndex(index);

        var mask = (byte)(1 << (index & 7));
        ref var slot = ref _bits[index >> 3];
        if ((slot & mask) != 0)
        {
            // 写成 (byte)(slot & ~mask) 而不是 slot &= (byte)~mask：
            // ~mask 会提升成 int 并变成负数，构建开了 CheckForOverflowUnderflow，
            // 直接转回 byte 会抛 OverflowException。
            slot = (byte)(slot & ~mask);
            SetCount--;
        }
    }

    /// <summary>还缺哪些分片，按下标升序。MVP 的调度器就是顺序拉这个序列。</summary>
    public IEnumerable<int> MissingIndices()
    {
        for (var i = 0; i < Count; i++)
        {
            if (!this[i])
            {
                yield return i;
            }
        }
    }

    /// <summary>本方缺、而对方有的分片。二期 swarm 用它挑该向谁请求什么。</summary>
    public IEnumerable<int> MissingButAvailableIn(PieceBitfield other)
    {
        if (other.Count != Count)
        {
            throw new ArgumentException(
                $"位图长度不一致：本方 {Count}，对方 {other.Count}。", nameof(other));
        }

        for (var i = 0; i < Count; i++)
        {
            if (!this[i] && other[i])
            {
                yield return i;
            }
        }
    }

    /// <summary>线上格式：分片数(be32) + 位图字节。</summary>
    public byte[] Serialize()
    {
        var result = new byte[sizeof(int) + _bits.Length];
        BinaryPrimitives.WriteInt32BigEndian(result, Count);
        _bits.CopyTo(result.AsSpan(sizeof(int)));
        return result;
    }

    /// <summary><paramref name="data"/> 是不可信输入，所有字段都校验。</summary>
    public static PieceBitfield Deserialize(ReadOnlySpan<byte> data, int expectedCount)
    {
        if (data.Length < sizeof(int))
        {
            throw new ArgumentException($"位图数据只有 {data.Length} 字节，不足头部。", nameof(data));
        }

        var count = BinaryPrimitives.ReadInt32BigEndian(data);
        if (count != expectedCount)
        {
            throw new ArgumentException(
                $"位图声明 {count} 个分片，但清单里是 {expectedCount} 个。", nameof(data));
        }

        var expectedBytes = (count + 7) / 8;
        if (data.Length != sizeof(int) + expectedBytes)
        {
            throw new ArgumentException(
                $"位图应为 {sizeof(int) + expectedBytes} 字节，实际 {data.Length} 字节。", nameof(data));
        }

        var bits = data[sizeof(int)..].ToArray();

        // 最后一个字节里超出 count 的高位必须是 0。放过它会让「同一个位图有多种
        // 字节表示」，进而让位图无法用于相等性判断或哈希。
        var remainder = count & 7;
        if (remainder != 0)
        {
            var validMask = (byte)((1 << remainder) - 1);
            if ((bits[^1] & ~validMask) != 0)
            {
                throw new ArgumentException("位图最后一个字节里有超出分片数的位被置起。", nameof(data));
            }
        }

        return new PieceBitfield(count, bits);
    }

    private void ValidateIndex(int index)
    {
        // 不用 (uint)index >= (uint)Count 这个常见技巧：checked 上下文里
        // (uint)(-1) 会抛 OverflowException 而不是得到一个大数。
        if (index < 0 || index >= Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"分片下标应在 0~{Count - 1} 之间。");
        }
    }

    private static int CountSetBits(byte[] bits, int count)
    {
        var total = 0;
        for (var i = 0; i < count; i++)
        {
            if ((bits[i >> 3] & (1 << (i & 7))) != 0)
            {
                total++;
            }
        }

        return total;
    }
}
