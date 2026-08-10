using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace NexusP2P.Core.Hashing;

/// <summary>
/// 32 字节的 SHA-256 摘要。
///
/// 用值类型（4 个 <see cref="ulong"/>）而不是 <c>byte[]</c> 存储：
/// 一个 20 GiB 的文件会产生约 33 万个叶子哈希，逐个分配数组会给 GC
/// 带来完全无谓的压力，而这些哈希的生命周期极短。
/// </summary>
public readonly struct Hash256 : IEquatable<Hash256>
{
    /// <summary>摘要的字节数。</summary>
    public const int Size = 32;

    private readonly ulong _a;
    private readonly ulong _b;
    private readonly ulong _c;
    private readonly ulong _d;

    /// <summary>全零摘要。仅用作「未赋值」的哨兵，不参与任何哈希计算。</summary>
    public static Hash256 Zero => default;

    public Hash256(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
        {
            throw new ArgumentException(
                $"SHA-256 摘要必须是 {Size} 字节，实际为 {bytes.Length} 字节。", nameof(bytes));
        }

        _a = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        _b = BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
        _c = BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]);
        _d = BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]);
    }

    /// <summary>把摘要写入 <paramref name="destination"/>，必须恰好 32 字节。</summary>
    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length != Size)
        {
            throw new ArgumentException(
                $"目标缓冲区必须是 {Size} 字节，实际为 {destination.Length} 字节。", nameof(destination));
        }

        BinaryPrimitives.WriteUInt64LittleEndian(destination, _a);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], _b);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[16..], _c);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[24..], _d);
    }

    public byte[] ToArray()
    {
        var result = new byte[Size];
        CopyTo(result);
        return result;
    }

    public bool Equals(Hash256 other) =>
        _a == other._a && _b == other._b && _c == other._c && _d == other._d;

    public override bool Equals(object? obj) => obj is Hash256 other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_a, _b, _c, _d);

    public static bool operator ==(Hash256 left, Hash256 right) => left.Equals(right);

    public static bool operator !=(Hash256 left, Hash256 right) => !left.Equals(right);

    /// <summary>小写十六进制，64 个字符。</summary>
    public override string ToString()
    {
        Span<byte> bytes = stackalloc byte[Size];
        CopyTo(bytes);
        return Convert.ToHexStringLower(bytes);
    }

    public static Hash256 Parse(string hex)
    {
        return TryParse(hex, out var result)
            ? result
            : throw new FormatException($"不是合法的 SHA-256 十六进制摘要：\"{hex}\"。");
    }

    public static bool TryParse([NotNullWhen(true)] string? hex, out Hash256 result)
    {
        result = default;
        if (hex is null || hex.Length != Size * 2)
        {
            return false;
        }

        // 手写解析而不是 Convert.FromHexString + try/catch：
        // TryParse 的语义就是「不抛异常」，用异常做控制流既慢又别扭。
        Span<byte> bytes = stackalloc byte[Size];
        for (var i = 0; i < Size; i++)
        {
            var high = DecodeNibble(hex[i * 2]);
            var low = DecodeNibble(hex[(i * 2) + 1]);
            if (high < 0 || low < 0)
            {
                return false;
            }

            bytes[i] = (byte)((high << 4) | low);
        }

        result = new Hash256(bytes);
        return true;
    }

    private static int DecodeNibble(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };
}
