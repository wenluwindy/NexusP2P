using NexusP2P.Core.Codes;

namespace NexusP2P.Core.Tests.Codes;

public sealed class ShareLinkTests
{
    private static readonly ShareLinkFactory Factory = new("https://p2p.example.com");

    /// <summary>
    /// V3 的核心不变式：链接里<b>没有任何秘密</b>。
    ///
    /// <para>V1/V2 靠「密钥在 fragment 里」保证服务器无法解密，代价是用户
    /// 得转述 43 个字符。V3 把密钥挪进了数据通道，于是链接退化成
    /// 「文件码的可点击形式」—— 这条测试盯住的就是「别再往里塞东西」。</para>
    /// </summary>
    [Fact]
    public void 链接里不含任何秘密()
    {
        var url = Factory.Create(TransferCode.Parse("123456789"));

        Assert.DoesNotContain("#", url, StringComparison.Ordinal);
        Assert.Equal("https://p2p.example.com/r/123456789", url);
    }

    [Fact]
    public void 链接里用无分隔的九位数字()
    {
        // 展示给人看的是 123-456-789，但 URL 里用纯数字，免得路由要处理连字符
        var url = Factory.Create(TransferCode.Parse("123-456-789"));

        Assert.Contains("/r/123456789", url, StringComparison.Ordinal);
        Assert.DoesNotContain("123-456-789", url, StringComparison.Ordinal);
    }

    [Fact]
    public void 往返无损()
    {
        var code = TransferCode.Generate();

        var url = Factory.Create(code);

        Assert.True(ShareLinkFactory.TryParse(url, out var link));
        Assert.Equal(code, link.Code);
    }

    [Fact]
    public void 解析与基址无关()
    {
        // 接收方拿到的链接可能来自任何域名或端口，解析不该校验主机
        foreach (var url in new[]
                 {
                     "https://other.example.org/r/111222333",
                     "http://192.168.1.10:8443/r/111222333",
                     "https://p2p.example.com:8443/r/111222333",
                     "https://example.com/sub/path/r/111222333",
                 })
        {
            Assert.True(ShareLinkFactory.TryParse(url, out var link), $"\"{url}\" 本应能解析");
            Assert.Equal("111222333", link.Code.Digits);
        }
    }

    /// <summary>
    /// V1/V2 生成的链接带 <c>#密钥</c>。它们仍然要能解析出文件码 ——
    /// 用户不该因为手里拿的是一条旧链接就被卡住，而那段密钥现在只是
    /// 一段无用的字符。
    /// </summary>
    [Fact]
    public void 旧链接的密钥片段被忽略但码仍可解析()
    {
        const string legacy =
            "https://p2p.example.com/r/111222333#AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

        Assert.True(ShareLinkFactory.TryParse(legacy, out var link));
        Assert.Equal("111222333", link.Code.Digits);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://example.com/r/111222333")]        // 协议不对
    [InlineData("https://example.com/r/12345")]          // 码只有 5 位
    [InlineData("https://example.com/x/111222333")]      // 路径段不对
    [InlineData("https://example.com/111222333")]        // 少了 /r/
    public void 非法链接被拒绝(string? url)
    {
        Assert.False(ShareLinkFactory.TryParse(url, out _));
    }

    // ---- 基址校验（AD-8：配置错了要快速失败，而不是生成一堆废链接）----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("example.com")]                      // 不是绝对 URL
    [InlineData("ftp://example.com")]                // 协议不对
    [InlineData("https://example.com/?a=1")]         // 带查询串
    [InlineData("https://example.com/#frag")]        // 带片段
    public void 非法的_PublicOrigin_被拒绝(string? origin)
    {
        // ThrowsAny：null 会走成 ArgumentNullException，它是 ArgumentException 的子类，
        // 而 xunit 的 Assert.Throws<T> 要求类型完全一致。
        Assert.ThrowsAny<ArgumentException>(() => new ShareLinkFactory(origin!));
    }

    [Fact]
    public void 基址末尾的斜杠被规范化()
    {
        var withSlash = new ShareLinkFactory("https://p2p.example.com/").Create(TransferCode.Parse("111222333"));
        var withoutSlash = new ShareLinkFactory("https://p2p.example.com").Create(TransferCode.Parse("111222333"));

        Assert.Equal(withoutSlash, withSlash);
        Assert.DoesNotContain("//r/", withSlash, StringComparison.Ordinal);
    }

    [Fact]
    public void 支持带子路径的基址()
    {
        var factory = new ShareLinkFactory("https://example.com/nexus");

        var url = factory.Create(TransferCode.Parse("111222333"));

        Assert.Equal("https://example.com/nexus/r/111222333", url);
        Assert.True(ShareLinkFactory.TryParse(url, out var link));
        Assert.Equal("111222333", link.Code.Digits);
    }

    [Fact]
    public void 支持非标端口()
    {
        var factory = new ShareLinkFactory("https://example.com:8443");

        var url = factory.Create(TransferCode.Parse("111222333"));

        Assert.StartsWith("https://example.com:8443/r/", url, StringComparison.Ordinal);
    }
}
