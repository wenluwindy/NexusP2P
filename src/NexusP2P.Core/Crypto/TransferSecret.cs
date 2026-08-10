using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace NexusP2P.Core.Crypto;

/// <summary>
/// 一次传输的根密钥材料（32 字节）。它<b>不直接当加密密钥用</b> ——
/// 内容密钥由它经 HKDF 派生（见 <c>KeyDerivation</c>）。
///
/// <para>它随分享链接的 URL fragment 传递，浏览器规范保证 fragment
/// 永不发往服务器，所以服务器从密码学上无法解密任何字节，
/// 即使它正在中继这些流量。</para>
///
/// <para><b>关于内存清零</b>：这是个值类型，会被到处复制，无法可靠归零。
/// 这里刻意不做「安全内存」那一套 —— 密钥本来就存在于 URL 栏、剪贴板
/// 和聊天记录里，进程内存不是这条链上的薄弱环节。为它引入复杂度是错配。</para>
/// </summary>
public readonly struct TransferSecret : IEquatable<TransferSecret>
{
    /// <summary>密钥材料字节数。32 字节喂给 HKDF 派生 AES-256 密钥。</summary>
    public const int Size = 32;

    /// <summary>base64url 编码后的字符数（32 字节无填充）。</summary>
    public const int EncodedLength = 43;

    private readonly ulong _a;
    private readonly ulong _b;
    private readonly ulong _c;
    private readonly ulong _d;

    public TransferSecret(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
        {
            throw new ArgumentException(
                $"密钥材料必须是 {Size} 字节，实际为 {bytes.Length} 字节。", nameof(bytes));
        }

        _a = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        _b = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
        _c = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]);
        _d = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]);
    }

    public static TransferSecret Generate()
    {
        Span<byte> bytes = stackalloc byte[Size];
        RandomNumberGenerator.Fill(bytes);
        return new TransferSecret(bytes);
    }

    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length != Size)
        {
            throw new ArgumentException(
                $"目标缓冲区必须是 {Size} 字节，实际为 {destination.Length} 字节。", nameof(destination));
        }

        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(destination, _a);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], _b);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(destination[16..], _c);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(destination[24..], _d);
    }

    public byte[] ToArray()
    {
        var result = new byte[Size];
        CopyTo(result);
        return result;
    }

    /// <summary>base64url（无填充）—— 可直接放进 URL fragment。</summary>
    public string ToBase64Url()
    {
        Span<byte> bytes = stackalloc byte[Size];
        CopyTo(bytes);
        return Base64Url.EncodeToString(bytes);
    }

    public static bool TryFromBase64Url([NotNullWhen(true)] string? text, out TransferSecret secret)
    {
        secret = default;

        if (string.IsNullOrEmpty(text) || text.Length != EncodedLength)
        {
            return false;
        }

        // Base64Url.TryDecodeFromChars 名字里带 Try，但遇到非法字符时会抛
        // FormatException 而不是返回 false。分享链接是不可信输入，
        // 让它抛到调用方等于「畸形链接把程序搞崩」，所以先用 IsValid 挡一道，
        // 再兜一个 catch —— 依赖单一 API 的行为不如两道都上。
        if (!Base64Url.IsValid(text.AsSpan()))
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[Size];
        try
        {
            if (!Base64Url.TryDecodeFromChars(text, bytes, out var written) || written != Size)
            {
                return false;
            }
        }
        catch (FormatException)
        {
            return false;
        }

        secret = new TransferSecret(bytes);
        return true;
    }

    public bool Equals(TransferSecret other) =>
        _a == other._a && _b == other._b && _c == other._c && _d == other._d;

    public override bool Equals(object? obj) => obj is TransferSecret other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_a, _b, _c, _d);

    public static bool operator ==(TransferSecret left, TransferSecret right) => left.Equals(right);

    public static bool operator !=(TransferSecret left, TransferSecret right) => !left.Equals(right);

    /// <summary>刻意不输出密钥内容 —— 免得它被顺手写进日志。</summary>
    public override string ToString() => "TransferSecret(已隐藏)";
}
