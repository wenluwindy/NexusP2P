using System.Security.Cryptography;
using System.Text;

namespace NexusP2P.Core.Crypto;

/// <summary>
/// 从 <see cref="TransferSecret"/> 派生各用途的密钥。
///
/// <para><b>为什么不直接把密钥材料当 AES 密钥用</b>：不同用途必须用不同密钥。
/// 若清单和分片共用一把钥匙，两边的 nonce 空间就会重叠，而 AES-GCM 下
/// nonce 重用会直接泄露明文异或值 —— 这是 GCM 最经典的灾难性误用。
/// 用 HKDF 按用途标签分开，从结构上让这种重叠不可能发生。</para>
/// </summary>
public static class KeyDerivation
{
    /// <summary>派生密钥长度：32 字节，即 AES-256。</summary>
    public const int KeySize = 32;

    /// <summary>
    /// 用途标签前缀。带上版本号，这样日后改派生方案时不会与旧实现悄悄互通 ——
    /// 宁可明确地连不上，也不要用错的密钥解出垃圾。
    /// </summary>
    private const string LabelPrefix = "NexusP2P/v1/";

    /// <summary>分片内容加密用。</summary>
    public const string ContentPurpose = "content";

    /// <summary>清单消息加密用。与分片分开，避免 nonce 空间重叠。</summary>
    public const string ManifestPurpose = "manifest";

    /// <summary>
    /// HKDF-SHA256 派生。<paramref name="purpose"/> 会被加上带版本的前缀后
    /// 作为 HKDF 的 info 参数。
    /// </summary>
    public static byte[] DeriveKey(TransferSecret secret, string purpose)
    {
        ArgumentException.ThrowIfNullOrEmpty(purpose);

        Span<byte> ikm = stackalloc byte[TransferSecret.Size];
        secret.CopyTo(ikm);

        var info = Encoding.UTF8.GetBytes(LabelPrefix + purpose);
        var key = new byte[KeySize];

        // salt 留空：ikm 本身已经是 32 字节的高熵随机值，HKDF 的 salt
        // 在这种情形下没有额外收益，而多一个要同步的参数就多一处出错的机会。
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, key, salt: default, info: info);
        return key;
    }

    public static byte[] DeriveContentKey(TransferSecret secret) => DeriveKey(secret, ContentPurpose);

    public static byte[] DeriveManifestKey(TransferSecret secret) => DeriveKey(secret, ManifestPurpose);
}
