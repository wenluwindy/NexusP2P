using System.Buffers.Binary;
using System.Security.Cryptography;
using NexusP2P.Core.Hashing;

namespace NexusP2P.Core.Crypto;

/// <summary>
/// 分片的 AES-256-GCM 加解密。
///
/// <para><b>nonce 唯一性是这里唯一可能造成灾难性后果的地方。</b>
/// AES-GCM 下同一密钥重用 nonce 会直接泄露两段明文的异或值，
/// 并让攻击者能伪造认证标签。所以 nonce 不是随机生成的，而是
/// <b>由位置确定性派生</b>：</para>
///
/// <code>nonce = 文件序号(be32) ‖ 分片序号(be64)   // 恰好 12 字节</code>
///
/// <para>唯一性由构造保证：一次传输里 (文件序号, 分片序号) 这对值是唯一的，
/// 所以 nonce 必然唯一。<b>必须把文件序号也放进去</b> ——
/// 只用分片序号的话，同一次传输里两个不同文件的第 0 片会撞 nonce，
/// 这是这套方案里最容易犯的错。</para>
///
/// <para>跨传输不会重用：每次发送生成新的 <see cref="TransferSecret"/>，
/// 所以密钥不同。AAD 里再绑上清单哈希，即使密钥泄露也无法把一次传输的
/// 密文重放进另一次。</para>
///
/// <para><b>加密是传输层的，落盘存明文。</b>接收端的 <c>.part</c> 文件里是
/// 解密后的明文 —— 因为 Merkle 根是对明文算的，而且文件最终形态本来就是明文，
/// 存密文不会带来任何实际收益，却会让续传需要持久化密钥。</para>
/// </summary>
public sealed class PieceCipher : IDisposable
{
    /// <summary>AES-GCM 的 nonce 长度。12 字节是推荐值，也是唯一无需额外哈希的长度。</summary>
    public const int NonceSize = 12;

    /// <summary>认证标签长度。</summary>
    public const int TagSize = 16;

    private readonly AesGcm _aes;
    private readonly byte[] _associatedData;
    private bool _disposed;

    /// <param name="secret">本次传输的根密钥材料。</param>
    /// <param name="manifestHash">清单哈希，作为 AAD 把密文绑定到这一次传输。</param>
    public PieceCipher(TransferSecret secret, Hash256 manifestHash)
    {
        var key = KeyDerivation.DeriveContentKey(secret);
        try
        {
            _aes = new AesGcm(key, TagSize);
        }
        finally
        {
            // 派生出的密钥已经复制进 AesGcm 内部，这份副本可以清掉。
            // 不是什么强保证，但它是唯一一处我们真的持有裸密钥字节的地方，
            // 清掉的成本是零。
            CryptographicOperations.ZeroMemory(key);
        }

        _associatedData = manifestHash.ToArray();
    }

    /// <summary>加密后的长度 = 明文长度 + 认证标签。</summary>
    public static int GetCiphertextLength(int plaintextLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(plaintextLength);
        return plaintextLength + TagSize;
    }

    /// <summary>解密后的长度。</summary>
    public static int GetPlaintextLength(int ciphertextLength)
    {
        if (ciphertextLength < TagSize)
        {
            throw new ArgumentOutOfRangeException(nameof(ciphertextLength), ciphertextLength,
                $"密文至少要有 {TagSize} 字节的认证标签。");
        }

        return ciphertextLength - TagSize;
    }

    /// <summary>
    /// 加密一个分片。<paramref name="destination"/> 长度必须是
    /// <c>plaintext.Length + <see cref="TagSize"/></c>，标签追加在末尾。
    /// </summary>
    public void Encrypt(int fileIndex, long pieceIndex, ReadOnlySpan<byte> plaintext, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var expected = GetCiphertextLength(plaintext.Length);
        if (destination.Length != expected)
        {
            throw new ArgumentException(
                $"目标缓冲区应为 {expected} 字节，实际为 {destination.Length} 字节。", nameof(destination));
        }

        Span<byte> nonce = stackalloc byte[NonceSize];
        DeriveNonce(fileIndex, pieceIndex, nonce);

        _aes.Encrypt(
            nonce,
            plaintext,
            destination[..plaintext.Length],
            destination[plaintext.Length..],
            _associatedData);
    }

    /// <summary>
    /// 解密一个分片。认证失败时抛 <see cref="PieceAuthenticationException"/>，
    /// 且<b>目标缓冲区被显式清零</b> —— 不把任何部分明文留给调用方。
    /// </summary>
    public void Decrypt(int fileIndex, long pieceIndex, ReadOnlySpan<byte> ciphertext, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var expected = GetPlaintextLength(ciphertext.Length);
        if (destination.Length != expected)
        {
            throw new ArgumentException(
                $"目标缓冲区应为 {expected} 字节，实际为 {destination.Length} 字节。", nameof(destination));
        }

        Span<byte> nonce = stackalloc byte[NonceSize];
        DeriveNonce(fileIndex, pieceIndex, nonce);

        try
        {
            _aes.Decrypt(
                nonce,
                ciphertext[..expected],
                ciphertext[expected..],
                destination,
                _associatedData);
        }
        catch (CryptographicException ex)
        {
            // 平台对失败时目标缓冲区的内容不做承诺。自己清零，
            // 这样「不返回部分明文」是我们的保证而不是平台的实现细节。
            CryptographicOperations.ZeroMemory(destination);

            throw new PieceAuthenticationException(fileIndex, pieceIndex, ex);
        }
    }

    /// <summary>
    /// 由位置派生 nonce：<c>文件序号(be32) ‖ 分片序号(be64)</c>。
    /// 内部可见以便测试直接验证唯一性 —— 这是本类型最关键的不变式。
    /// </summary>
    internal static void DeriveNonce(int fileIndex, long pieceIndex, Span<byte> nonce)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fileIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(pieceIndex);

        if (nonce.Length != NonceSize)
        {
            throw new ArgumentException($"nonce 必须是 {NonceSize} 字节。", nameof(nonce));
        }

        BinaryPrimitives.WriteUInt32BigEndian(nonce, (uint)fileIndex);
        BinaryPrimitives.WriteInt64BigEndian(nonce[sizeof(uint)..], pieceIndex);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _aes.Dispose();
    }
}

/// <summary>分片的认证标签校验失败：数据被篡改、密钥不对，或位置不对。</summary>
public sealed class PieceAuthenticationException(int fileIndex, long pieceIndex, Exception? inner = null)
    : Exception($"文件 {fileIndex} 的第 {pieceIndex} 个分片认证失败：数据被篡改、密钥不匹配或位置错误。", inner)
{
    public int FileIndex { get; } = fileIndex;

    public long PieceIndex { get; } = pieceIndex;
}
