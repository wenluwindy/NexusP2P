using NexusP2P.Core.Crypto;

namespace NexusP2P.Core.Tests.Crypto;

public sealed class TransferSecretTests
{
    [Fact]
    public void 生成的密钥是_32_字节()
    {
        var secret = TransferSecret.Generate();

        Assert.Equal(TransferSecret.Size, secret.ToArray().Length);
    }

    [Fact]
    public void 字节往返无损()
    {
        var bytes = new byte[TransferSecret.Size];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)i;
        }

        Assert.Equal(bytes, new TransferSecret(bytes).ToArray());
    }

    [Fact]
    public void base64url_往返无损()
    {
        var secret = TransferSecret.Generate();

        Assert.True(TransferSecret.TryFromBase64Url(secret.ToBase64Url(), out var restored));
        Assert.Equal(secret, restored);
    }

    [Fact]
    public void base64url_长度符合常量且是_URL_安全字符()
    {
        var encoded = TransferSecret.Generate().ToBase64Url();

        Assert.Equal(TransferSecret.EncodedLength, encoded.Length);

        // 不能有 '+' '/' '='，否则放进 URL fragment 会出问题
        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);
        Assert.DoesNotContain('=', encoded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tooshort")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]   // 44 个字符
    [InlineData("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!")]    // 长度对但不是 base64
    public void 非法编码被拒绝(string? text)
    {
        Assert.False(TransferSecret.TryFromBase64Url(text, out _));
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(0)]
    public void 长度不对的字节数组被拒绝(int length)
    {
        Assert.Throws<ArgumentException>(() => new TransferSecret(new byte[length]));
    }

    [Fact]
    public void 每个字节位置的差异都能被检出()
    {
        var baseline = new TransferSecret(new byte[TransferSecret.Size]);

        for (var i = 0; i < TransferSecret.Size; i++)
        {
            var mutated = new byte[TransferSecret.Size];
            mutated[i] = 0xFF;

            Assert.NotEqual(baseline, new TransferSecret(mutated));
        }
    }

    [Fact]
    public void 连续生成不重复()
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < 500; i++)
        {
            Assert.True(seen.Add(TransferSecret.Generate().ToBase64Url()), "生成的密钥出现重复");
        }
    }

    [Fact]
    public void ToString_不泄露密钥内容()
    {
        // 密钥被顺手写进日志是很常见的事故。ToString 刻意不输出内容。
        var secret = TransferSecret.Generate();

        var text = secret.ToString();

        Assert.DoesNotContain(secret.ToBase64Url(), text, StringComparison.Ordinal);
        Assert.Contains("已隐藏", text, StringComparison.Ordinal);
    }
}
