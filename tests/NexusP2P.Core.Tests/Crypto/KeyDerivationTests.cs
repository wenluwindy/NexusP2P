using NexusP2P.Core.Crypto;

namespace NexusP2P.Core.Tests.Crypto;

public sealed class KeyDerivationTests
{
    /// <summary>刻意选一组差异极小的标签，验证它们不会派生出同一把钥匙。</summary>
    private static readonly string[] NearlyIdenticalPurposes =
        ["content", "contents", "conten", "Content", "content "];

    [Fact]
    public void 派生出的密钥是_32_字节()
    {
        var key = KeyDerivation.DeriveContentKey(TransferSecret.Generate());

        Assert.Equal(KeyDerivation.KeySize, key.Length);
    }

    [Fact]
    public void 同一密钥材料与用途派生结果确定()
    {
        var secret = TransferSecret.Generate();

        Assert.Equal(
            KeyDerivation.DeriveContentKey(secret),
            KeyDerivation.DeriveContentKey(secret));
    }

    [Fact]
    public void 不同用途派生出不同密钥()
    {
        // 这是 HKDF 在这里存在的全部理由：清单和分片必须用不同的钥匙，
        // 否则两边的 nonce 空间重叠，AES-GCM 会因 nonce 重用而崩塌。
        var secret = TransferSecret.Generate();

        Assert.NotEqual(
            KeyDerivation.DeriveContentKey(secret),
            KeyDerivation.DeriveManifestKey(secret));
    }

    [Fact]
    public void 不同密钥材料派生出不同密钥()
    {
        Assert.NotEqual(
            KeyDerivation.DeriveContentKey(TransferSecret.Generate()),
            KeyDerivation.DeriveContentKey(TransferSecret.Generate()));
    }

    [Fact]
    public void 派生出的密钥不等于原始密钥材料()
    {
        // 若哪天有人把 HKDF 简化成「直接用密钥材料」，这条会失败
        var secret = TransferSecret.Generate();

        Assert.NotEqual(secret.ToArray(), KeyDerivation.DeriveContentKey(secret));
    }

    [Fact]
    public void 用途标签为空被拒绝()
    {
        var secret = TransferSecret.Generate();

        Assert.ThrowsAny<ArgumentException>(() => KeyDerivation.DeriveKey(secret, ""));
        Assert.ThrowsAny<ArgumentException>(() => KeyDerivation.DeriveKey(secret, null!));
    }

    [Fact]
    public void 相近的用途标签不会撞车()
    {
        var secret = TransferSecret.Generate();

        var keys = NearlyIdenticalPurposes
            .Select(purpose => Convert.ToHexStringLower(KeyDerivation.DeriveKey(secret, purpose)))
            .ToHashSet();

        Assert.Equal(NearlyIdenticalPurposes.Length, keys.Count);
    }
}
