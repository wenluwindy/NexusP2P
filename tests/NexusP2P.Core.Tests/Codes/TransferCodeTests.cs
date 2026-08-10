using NexusP2P.Core.Codes;

namespace NexusP2P.Core.Tests.Codes;

public sealed class TransferCodeTests
{
    [Fact]
    public void 生成的码是九位数字()
    {
        for (var i = 0; i < 200; i++)
        {
            var code = TransferCode.Generate();

            Assert.Equal(9, code.Digits.Length);
            Assert.All(code.Digits, c => Assert.True(char.IsAsciiDigit(c)));
            Assert.InRange(code.Value, 0, TransferCode.ExclusiveUpper - 1);
        }
    }

    [Fact]
    public void 展示形式是三组用连字符分隔()
    {
        var code = TransferCode.Parse("123456789");

        Assert.Equal("123-456-789", code.ToString());
    }

    [Fact]
    public void 前导零被保留()
    {
        // 若内部用 int 存而格式化忘了补零，"000-000-001" 会变成 "1"，
        // 对方就永远输不进正确的码。
        var code = TransferCode.Parse("000000001");

        Assert.Equal("000000001", code.Digits);
        Assert.Equal("000-000-001", code.ToString());
    }

    [Fact]
    public void 全零码合法()
    {
        var code = TransferCode.Parse("000-000-000");

        Assert.Equal(0, code.Value);
        Assert.Equal("000-000-000", code.ToString());
    }

    [Fact]
    public void 最大码合法()
    {
        var code = TransferCode.Parse("999-999-999");

        Assert.Equal(999_999_999, code.Value);
    }

    [Theory]
    // 用户实际会粘进来的各种形态
    [InlineData("123456789")]
    [InlineData("123-456-789")]
    [InlineData("123 456 789")]
    [InlineData("  123-456-789  ")]
    [InlineData("123_456_789")]
    [InlineData("123.456.789")]
    [InlineData("1-2-3-4-5-6-7-8-9")]
    [InlineData("123－456－789")]        // 全角连字符
    [InlineData("123　456　789")]        // 全角空格
    [InlineData("123‐456–789")]         // 各种破折号
    [InlineData("１２３４５６７８９")]      // 全角数字
    [InlineData("１２３-456-７８９")]      // 全角半角混排
    public void 宽容解析各种分隔与全角形式(string text)
    {
        Assert.True(TransferCode.TryParse(text, out var code), $"\"{text}\" 本应能解析");
        Assert.Equal("123456789", code.Digits);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12345678")]          // 只有 8 位
    [InlineData("1234567890")]        // 10 位
    [InlineData("12345678a")]
    [InlineData("abcdefghi")]
    [InlineData("123-456-78")]
    [InlineData("---------")]
    [InlineData("123456789012")]
    public void 非法输入被拒绝(string? text)
    {
        Assert.False(TransferCode.TryParse(text, out _));
    }

    [Fact]
    public void Parse_对非法输入抛_FormatException()
    {
        Assert.Throws<FormatException>(() => TransferCode.Parse("nope"));
    }

    [Fact]
    public void 往返无损()
    {
        var original = TransferCode.Generate();

        Assert.Equal(original, TransferCode.Parse(original.ToString()));
        Assert.Equal(original, TransferCode.Parse(original.Digits));
    }

    [Fact]
    public void 相等性按数值比较()
    {
        var a = TransferCode.Parse("111-222-333");
        var b = TransferCode.Parse("111222333");
        var c = TransferCode.Parse("111-222-334");

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.True(a != c);
    }

    [Fact]
    public void 生成的码分布没有明显偏斜()
    {
        // 不是严格的随机性检验，只是抓「忘了用 CSPRNG」或「取模造成偏斜」
        // 这类粗错误：按首位数字分桶，每桶应大致均匀。
        const int Samples = 100_000;
        var buckets = new int[10];

        for (var i = 0; i < Samples; i++)
        {
            buckets[TransferCode.Generate().Digits[0] - '0']++;
        }

        var expected = Samples / 10.0;
        foreach (var count in buckets)
        {
            // 宽松到 ±25%：只为抓明显的结构性偏斜，不误报正常波动
            Assert.InRange(count, expected * 0.75, expected * 1.25);
        }
    }

    [Fact]
    public void 连续生成不会重复出同一个码()
    {
        // 抓「种子固定」或「返回常量」这类错误
        var codes = new HashSet<int>();
        for (var i = 0; i < 1000; i++)
        {
            codes.Add(TransferCode.Generate().Value);
        }

        // 10 亿空间里取 1000 个，生日问题下几乎不可能有碰撞；
        // 允许极少量重复以免偶发失败。
        Assert.True(codes.Count > 995, $"1000 次生成只得到 {codes.Count} 个不同的码");
    }
}
