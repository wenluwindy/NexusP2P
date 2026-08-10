using NexusP2P.Core.Hashing;

namespace NexusP2P.Core.Tests.Hashing;

public sealed class Hash256Tests
{
    private static byte[] Sequential()
    {
        var bytes = new byte[Hash256.Size];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)i;
        }

        return bytes;
    }

    [Fact]
    public void 字节数组往返无损()
    {
        var original = Sequential();

        var hash = new Hash256(original);

        Assert.Equal(original, hash.ToArray());
    }

    [Fact]
    public void 十六进制往返无损()
    {
        var hash = new Hash256(Sequential());

        var parsed = Hash256.Parse(hash.ToString());

        Assert.Equal(hash, parsed);
    }

    [Fact]
    public void ToString_是_64_个小写十六进制字符()
    {
        var text = new Hash256(Sequential()).ToString();

        Assert.Equal(64, text.Length);
        Assert.All(text, c => Assert.True(char.IsAsciiDigit(c) || (c is >= 'a' and <= 'f'), $"意外字符 '{c}'"));
    }

    [Fact]
    public void 大写十六进制也能解析()
    {
        var hash = new Hash256(Sequential());

        Assert.Equal(hash, Hash256.Parse(hash.ToString().ToUpperInvariant()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00")]
    [InlineData("zz00000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("00000000000000000000000000000000000000000000000000000000000000000")]
    public void 非法输入被拒绝(string? text)
    {
        Assert.False(Hash256.TryParse(text, out _));
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(0)]
    public void 长度不对的字节数组被拒绝(int length)
    {
        Assert.Throws<ArgumentException>(() => new Hash256(new byte[length]));
    }

    [Fact]
    public void 相同字节相等_不同字节不等()
    {
        var a = new Hash256(Sequential());
        var b = new Hash256(Sequential());

        var differentBytes = Sequential();
        differentBytes[31] ^= 0x01;
        var c = new Hash256(differentBytes);

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());

        Assert.NotEqual(a, c);
        Assert.True(a != c);
    }

    [Fact]
    public void 每个字节位置的差异都能被检出()
    {
        // 用 4 个 ulong 存储，如果某个字段读写错位，只改动那个字段覆盖的
        // 字节时相等性会失灵。逐字节翻转能抓到这类错位 bug。
        var baseline = new Hash256(Sequential());

        for (var i = 0; i < Hash256.Size; i++)
        {
            var mutated = Sequential();
            mutated[i] ^= 0xFF;

            Assert.NotEqual(baseline, new Hash256(mutated));
        }
    }

    [Fact]
    public void CopyTo_要求恰好_32_字节()
    {
        var hash = new Hash256(Sequential());

        Assert.Throws<ArgumentException>(() => hash.CopyTo(new byte[31]));
        Assert.Throws<ArgumentException>(() => hash.CopyTo(new byte[33]));
    }
}
