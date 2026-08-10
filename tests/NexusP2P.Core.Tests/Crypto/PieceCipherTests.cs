using NexusP2P.Core.Crypto;
using NexusP2P.Core.Hashing;

namespace NexusP2P.Core.Tests.Crypto;

public sealed class PieceCipherTests
{
    private static Hash256 ManifestHash(byte seed = 0x5A)
    {
        var bytes = new byte[Hash256.Size];
        Array.Fill(bytes, seed);
        return new Hash256(bytes);
    }

    private static byte[] Plaintext(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)((i * 7) & 0xFF);
        }

        return bytes;
    }

    private static byte[] RoundTrip(PieceCipher cipher, int fileIndex, long pieceIndex, byte[] plaintext)
    {
        var ciphertext = new byte[PieceCipher.GetCiphertextLength(plaintext.Length)];
        cipher.Encrypt(fileIndex, pieceIndex, plaintext, ciphertext);

        var decrypted = new byte[plaintext.Length];
        cipher.Decrypt(fileIndex, pieceIndex, ciphertext, decrypted);
        return decrypted;
    }

    // ---- nonce 唯一性：本类型最关键的不变式 ----

    /// <summary>
    /// AES-GCM 下 nonce 重用会直接泄露明文异或值。
    /// 遍历一个 (文件序号, 分片序号) 网格，确认没有任何两组位置撞 nonce。
    /// </summary>
    [Fact]
    public void 位置网格上的_nonce_两两不同()
    {
        var seen = new Dictionary<string, (int File, long Piece)>();

        for (var fileIndex = 0; fileIndex < 40; fileIndex++)
        {
            foreach (var pieceIndex in new long[] { 0, 1, 2, 255, 256, 65_535, 65_536, 1_000_000, long.MaxValue })
            {
                var nonce = new byte[PieceCipher.NonceSize];
                PieceCipher.DeriveNonce(fileIndex, pieceIndex, nonce);

                var key = Convert.ToHexStringLower(nonce);
                Assert.False(
                    seen.TryGetValue(key, out var previous),
                    $"nonce 重复：(文件 {fileIndex}, 分片 {pieceIndex}) 与 (文件 {previous.File}, 分片 {previous.Piece})");

                seen[key] = (fileIndex, pieceIndex);
            }
        }
    }

    /// <summary>
    /// 只用分片序号派生 nonce 是这套方案最容易犯的错：
    /// 同一次传输里两个不同文件的第 0 片会撞 nonce。
    /// 这条确认文件序号真的参与了派生。
    /// </summary>
    [Fact]
    public void 不同文件的同一个分片序号得到不同_nonce()
    {
        var first = new byte[PieceCipher.NonceSize];
        var second = new byte[PieceCipher.NonceSize];

        PieceCipher.DeriveNonce(0, 0, first);
        PieceCipher.DeriveNonce(1, 0, second);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void 同一位置派生出的_nonce_是确定性的()
    {
        var first = new byte[PieceCipher.NonceSize];
        var second = new byte[PieceCipher.NonceSize];

        PieceCipher.DeriveNonce(3, 12_345, first);
        PieceCipher.DeriveNonce(3, 12_345, second);

        Assert.Equal(first, second);
    }

    [Fact]
    public void nonce_布局是文件序号在前分片序号在后()
    {
        var nonce = new byte[PieceCipher.NonceSize];

        PieceCipher.DeriveNonce(0x01020304, 0x0A0B0C0D0E0F1011, nonce);

        Assert.Equal(
            new byte[] { 0x01, 0x02, 0x03, 0x04, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10, 0x11 },
            nonce);
    }

    [Fact]
    public void 负的位置被拒绝()
    {
        var nonce = new byte[PieceCipher.NonceSize];

        Assert.Throws<ArgumentOutOfRangeException>(() => PieceCipher.DeriveNonce(-1, 0, nonce));
        Assert.Throws<ArgumentOutOfRangeException>(() => PieceCipher.DeriveNonce(0, -1, nonce));
    }

    // ---- 往返 ----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(1024)]
    [InlineData(1024 * 1024)]
    public void 加解密往返一致(int length)
    {
        using var cipher = new PieceCipher(TransferSecret.Generate(), ManifestHash());
        var plaintext = Plaintext(length);

        Assert.Equal(plaintext, RoundTrip(cipher, 2, 7, plaintext));
    }

    [Fact]
    public void 密文比明文长一个标签()
    {
        using var cipher = new PieceCipher(TransferSecret.Generate(), ManifestHash());
        var plaintext = Plaintext(1000);
        var ciphertext = new byte[PieceCipher.GetCiphertextLength(plaintext.Length)];

        cipher.Encrypt(0, 0, plaintext, ciphertext);

        Assert.Equal(1000 + PieceCipher.TagSize, ciphertext.Length);
    }

    [Fact]
    public void 密文不等于明文()
    {
        using var cipher = new PieceCipher(TransferSecret.Generate(), ManifestHash());
        var plaintext = Plaintext(256);
        var ciphertext = new byte[PieceCipher.GetCiphertextLength(plaintext.Length)];

        cipher.Encrypt(0, 0, plaintext, ciphertext);

        Assert.NotEqual(plaintext, ciphertext[..plaintext.Length]);
    }

    // ---- 篡改与错位 ----

    [Fact]
    public void 篡改密文任意一个_bit_都会认证失败()
    {
        using var cipher = new PieceCipher(TransferSecret.Generate(), ManifestHash());
        var plaintext = Plaintext(64);
        var ciphertext = new byte[PieceCipher.GetCiphertextLength(plaintext.Length)];
        cipher.Encrypt(0, 0, plaintext, ciphertext);

        for (var i = 0; i < ciphertext.Length; i++)
        {
            var tampered = (byte[])ciphertext.Clone();
            tampered[i] ^= 0x01;

            var destination = new byte[plaintext.Length];
            Assert.Throws<PieceAuthenticationException>(
                () => cipher.Decrypt(0, 0, tampered, destination));
        }
    }

    [Fact]
    public void 认证失败时目标缓冲区被清零而不留部分明文()
    {
        using var cipher = new PieceCipher(TransferSecret.Generate(), ManifestHash());
        var plaintext = Plaintext(128);
        var ciphertext = new byte[PieceCipher.GetCiphertextLength(plaintext.Length)];
        cipher.Encrypt(0, 0, plaintext, ciphertext);

        ciphertext[^1] ^= 0xFF;   // 破坏标签

        var destination = new byte[plaintext.Length];
        Array.Fill(destination, (byte)0xCC);   // 预填哨兵，确认真的被清了

        Assert.Throws<PieceAuthenticationException>(() => cipher.Decrypt(0, 0, ciphertext, destination));
        Assert.All(destination, b => Assert.Equal(0, b));
    }

    [Fact]
    public void 用错的分片序号解密会失败()
    {
        // nonce 由位置派生，所以把密文挪到别的位置就解不开 ——
        // 这防止攻击者在传输里调换分片顺序。
        using var cipher = new PieceCipher(TransferSecret.Generate(), ManifestHash());
        var plaintext = Plaintext(64);
        var ciphertext = new byte[PieceCipher.GetCiphertextLength(plaintext.Length)];
        cipher.Encrypt(0, 5, plaintext, ciphertext);

        var destination = new byte[plaintext.Length];
        Assert.Throws<PieceAuthenticationException>(() => cipher.Decrypt(0, 6, ciphertext, destination));
    }

    [Fact]
    public void 用错的文件序号解密会失败()
    {
        using var cipher = new PieceCipher(TransferSecret.Generate(), ManifestHash());
        var plaintext = Plaintext(64);
        var ciphertext = new byte[PieceCipher.GetCiphertextLength(plaintext.Length)];
        cipher.Encrypt(1, 0, plaintext, ciphertext);

        var destination = new byte[plaintext.Length];
        Assert.Throws<PieceAuthenticationException>(() => cipher.Decrypt(2, 0, ciphertext, destination));
    }

    [Fact]
    public void 换一个密钥解不开()
    {
        var plaintext = Plaintext(64);
        var manifestHash = ManifestHash();

        using var sender = new PieceCipher(TransferSecret.Generate(), manifestHash);
        using var stranger = new PieceCipher(TransferSecret.Generate(), manifestHash);

        var ciphertext = new byte[PieceCipher.GetCiphertextLength(plaintext.Length)];
        sender.Encrypt(0, 0, plaintext, ciphertext);

        var destination = new byte[plaintext.Length];
        Assert.Throws<PieceAuthenticationException>(() => stranger.Decrypt(0, 0, ciphertext, destination));
    }

    [Fact]
    public void 清单哈希不同则密文无法互通()
    {
        // AAD 绑定清单哈希：即使密钥相同，一次传输的密文也无法重放进另一次
        var secret = TransferSecret.Generate();
        var plaintext = Plaintext(64);

        using var first = new PieceCipher(secret, ManifestHash(0x11));
        using var second = new PieceCipher(secret, ManifestHash(0x22));

        var ciphertext = new byte[PieceCipher.GetCiphertextLength(plaintext.Length)];
        first.Encrypt(0, 0, plaintext, ciphertext);

        var destination = new byte[plaintext.Length];
        Assert.Throws<PieceAuthenticationException>(() => second.Decrypt(0, 0, ciphertext, destination));
    }

    [Fact]
    public void 异常里带上了出错的位置()
    {
        using var cipher = new PieceCipher(TransferSecret.Generate(), ManifestHash());
        var ciphertext = new byte[PieceCipher.GetCiphertextLength(16)];

        var ex = Assert.Throws<PieceAuthenticationException>(
            () => cipher.Decrypt(3, 99, ciphertext, new byte[16]));

        Assert.Equal(3, ex.FileIndex);
        Assert.Equal(99, ex.PieceIndex);
    }

    // ---- 缓冲区尺寸校验 ----

    [Fact]
    public void 目标缓冲区尺寸不对会被拒绝()
    {
        using var cipher = new PieceCipher(TransferSecret.Generate(), ManifestHash());
        var plaintext = Plaintext(100);

        Assert.Throws<ArgumentException>(() => cipher.Encrypt(0, 0, plaintext, new byte[100]));
        Assert.Throws<ArgumentException>(() => cipher.Encrypt(0, 0, plaintext, new byte[200]));
        Assert.Throws<ArgumentException>(
            () => cipher.Decrypt(0, 0, new byte[100 + PieceCipher.TagSize], new byte[99]));
    }

    [Fact]
    public void 短于标签长度的密文被拒绝()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PieceCipher.GetPlaintextLength(PieceCipher.TagSize - 1));
    }

    [Fact]
    public void 释放后再用会抛异常()
    {
        var cipher = new PieceCipher(TransferSecret.Generate(), ManifestHash());
        cipher.Dispose();

        Assert.Throws<ObjectDisposedException>(() => cipher.Encrypt(0, 0, [1], new byte[1 + PieceCipher.TagSize]));
    }

    [Fact]
    public void 重复释放不抛异常()
    {
        var cipher = new PieceCipher(TransferSecret.Generate(), ManifestHash());

        cipher.Dispose();
        cipher.Dispose();
    }
}
