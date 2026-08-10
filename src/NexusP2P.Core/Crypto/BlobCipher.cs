using System.Security.Cryptography;

namespace NexusP2P.Core.Crypto;

/// <summary>
/// 加密一次性的独立数据块（目前只有传输清单）。
///
/// <para>格式：<c>nonce(12) ‖ 密文 ‖ 标签(16)</c>。nonce 随机生成并放在最前面。</para>
///
/// <para>为什么这里用随机 nonce 而 <see cref="PieceCipher"/> 用位置派生：
/// 分片有天然唯一的位置可用，而清单没有。理论上「一次传输只发一条清单」
/// 意味着固定 nonce 也安全，但这个推理很脆弱 ——
/// 重连时会再发一次清单，将来若清单变成可增量更新的，固定 nonce 就成了灾难。
/// 12 个字节换掉这份脆弱，划算。</para>
///
/// <para><b>为什么需要这一层</b>（而不是只靠 WebRTC 的 DTLS）：
/// DTLS 确实让中继服务器看不到内容，但<b>信令服务器居中转发 SDP</b>，
/// 它可以把双方的 DTLS 指纹换成自己的，从而完整地中间人攻击 ——
/// 这是 WebRTC 上的经典问题。文件码 URL fragment 里的密钥是一条
/// 服务器看不到的带外通道，正是它让指纹替换失效。</para>
/// </summary>
public static class BlobCipher
{
    public const int NonceSize = 12;
    public const int TagSize = 16;

    /// <summary>密封后的长度。</summary>
    public static int SealedLength(int plaintextLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(plaintextLength);
        return NonceSize + plaintextLength + TagSize;
    }

    public static byte[] Seal(byte[] key, ReadOnlySpan<byte> plaintext)
    {
        ArgumentNullException.ThrowIfNull(key);

        var result = new byte[SealedLength(plaintext.Length)];
        RandomNumberGenerator.Fill(result.AsSpan(0, NonceSize));

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(
            result.AsSpan(0, NonceSize),
            plaintext,
            result.AsSpan(NonceSize, plaintext.Length),
            result.AsSpan(NonceSize + plaintext.Length, TagSize));

        return result;
    }

    /// <summary>
    /// 解封。认证失败时抛 <see cref="BlobAuthenticationException"/>，不返回部分明文。
    /// </summary>
    public static byte[] Open(byte[] key, ReadOnlySpan<byte> sealedBlob)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (sealedBlob.Length < NonceSize + TagSize)
        {
            throw new BlobAuthenticationException(
                $"密封数据只有 {sealedBlob.Length} 字节，不足 nonce 与标签的 {NonceSize + TagSize} 字节。");
        }

        var plaintextLength = sealedBlob.Length - NonceSize - TagSize;
        var plaintext = new byte[plaintextLength];

        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(
                sealedBlob[..NonceSize],
                sealedBlob.Slice(NonceSize, plaintextLength),
                sealedBlob[(NonceSize + plaintextLength)..],
                plaintext);
        }
        catch (CryptographicException ex)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new BlobAuthenticationException("密封数据认证失败：被篡改或密钥不匹配。", ex);
        }

        return plaintext;
    }
}

/// <summary>独立数据块的认证失败。</summary>
public sealed class BlobAuthenticationException(string message, Exception? inner = null)
    : Exception(message, inner);
