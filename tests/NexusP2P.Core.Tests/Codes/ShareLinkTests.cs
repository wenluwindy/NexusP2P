using NexusP2P.Core.Codes;
using NexusP2P.Core.Crypto;

namespace NexusP2P.Core.Tests.Codes;

public sealed class ShareLinkTests
{
    private static readonly ShareLinkFactory Factory = new("https://p2p.example.com");

    /// <summary>
    /// 端到端加密的全部依据就是这一条：密钥位于 <c>#</c> 之后。
    /// URL fragment 按规范永不随请求发送到服务器 ——
    /// 一旦有人把密钥挪进路径或查询串，服务器就能解密所有流量，
    /// 而这种改动在代码评审里极易被忽略。所以专门盯住它。
    /// </summary>
    [Fact]
    public void 密钥位于_fragment_之后而不在服务器可见的部分()
    {
        var secret = TransferSecret.Generate();
        var encoded = secret.ToBase64Url();

        var url = Factory.Create(TransferCode.Parse("123456789"), secret);

        var hashIndex = url.IndexOf('#', StringComparison.Ordinal);
        Assert.True(hashIndex > 0, "链接里必须有 '#'");

        var serverVisible = url[..hashIndex];
        var fragment = url[(hashIndex + 1)..];

        Assert.Equal(encoded, fragment);
        Assert.DoesNotContain(encoded, serverVisible, StringComparison.Ordinal);
    }

    [Fact]
    public void 服务器看到的路径与查询串里没有密钥()
    {
        // 换个角度再验一次：按 Uri 的语义拆开，模拟服务端实际收到的东西
        var secret = TransferSecret.Generate();
        var url = Factory.Create(TransferCode.Parse("987654321"), secret);

        var uri = new Uri(url);
        var whatServerReceives = uri.AbsolutePath + uri.Query;

        Assert.DoesNotContain(secret.ToBase64Url(), whatServerReceives, StringComparison.Ordinal);
        Assert.Empty(uri.Query);
    }

    [Fact]
    public void 链接格式符合预期()
    {
        var secret = TransferSecret.Generate();

        var url = Factory.Create(TransferCode.Parse("123456789"), secret);

        Assert.Equal($"https://p2p.example.com/r/123456789#{secret.ToBase64Url()}", url);
    }

    [Fact]
    public void 链接里用无分隔的九位数字()
    {
        // 展示给人看的是 123-456-789，但 URL 里用纯数字，免得路由要处理连字符
        var url = Factory.Create(TransferCode.Parse("123-456-789"), TransferSecret.Generate());

        Assert.Contains("/r/123456789#", url, StringComparison.Ordinal);
        Assert.DoesNotContain("123-456-789", url, StringComparison.Ordinal);
    }

    [Fact]
    public void 往返无损()
    {
        var code = TransferCode.Generate();
        var secret = TransferSecret.Generate();

        var url = Factory.Create(code, secret);

        Assert.True(ShareLinkFactory.TryParse(url, out var link));
        Assert.Equal(code, link.Code);
        Assert.Equal(secret, link.Secret);
    }

    [Fact]
    public void 解析与基址无关()
    {
        // 接收方拿到的链接可能来自任何域名或端口，解析不该校验主机
        var secret = TransferSecret.Generate();
        var encoded = secret.ToBase64Url();

        foreach (var url in new[]
                 {
                     $"https://other.example.org/r/111222333#{encoded}",
                     $"http://192.168.1.10:8443/r/111222333#{encoded}",
                     $"https://p2p.example.com:8443/r/111222333#{encoded}",
                     $"https://example.com/sub/path/r/111222333#{encoded}",
                 })
        {
            Assert.True(ShareLinkFactory.TryParse(url, out var link), $"\"{url}\" 本应能解析");
            Assert.Equal("111222333", link.Code.Digits);
            Assert.Equal(secret, link.Secret);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("https://example.com/r/111222333")]                       // 缺密钥
    [InlineData("https://example.com/r/111222333#")]                      // 空片段
    [InlineData("https://example.com/r/111222333#tooshort")]              // 密钥长度不对
    [InlineData("https://example.com/r/12345#AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]  // 码只有 5 位
    [InlineData("https://example.com/x/111222333#AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // 路径段不对
    [InlineData("https://example.com/111222333#AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]   // 少了 /r/
    public void 非法链接被拒绝(string? url)
    {
        Assert.False(ShareLinkFactory.TryParse(url, out _));
    }

    [Fact]
    public void 密钥被篡改一个字符后解析出的密钥不同()
    {
        var secret = TransferSecret.Generate();
        var url = Factory.Create(TransferCode.Parse("111222333"), secret);

        var hashIndex = url.IndexOf('#', StringComparison.Ordinal);
        var mutated = url[..(hashIndex + 1)] +
                      (url[hashIndex + 1] == 'A' ? 'B' : 'A') +
                      url[(hashIndex + 2)..];

        Assert.True(ShareLinkFactory.TryParse(mutated, out var link));
        Assert.NotEqual(secret, link.Secret);
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
        var secret = TransferSecret.Generate();

        var withSlash = new ShareLinkFactory("https://p2p.example.com/").Create(TransferCode.Parse("111222333"), secret);
        var withoutSlash = new ShareLinkFactory("https://p2p.example.com").Create(TransferCode.Parse("111222333"), secret);

        Assert.Equal(withoutSlash, withSlash);
        Assert.DoesNotContain("//r/", withSlash, StringComparison.Ordinal);
    }

    [Fact]
    public void 支持带子路径的基址()
    {
        var secret = TransferSecret.Generate();
        var factory = new ShareLinkFactory("https://example.com/nexus");

        var url = factory.Create(TransferCode.Parse("111222333"), secret);

        Assert.Equal($"https://example.com/nexus/r/111222333#{secret.ToBase64Url()}", url);
        Assert.True(ShareLinkFactory.TryParse(url, out var link));
        Assert.Equal(secret, link.Secret);
    }

    [Fact]
    public void 支持非标端口()
    {
        var secret = TransferSecret.Generate();
        var factory = new ShareLinkFactory("https://example.com:8443");

        var url = factory.Create(TransferCode.Parse("111222333"), secret);

        Assert.StartsWith("https://example.com:8443/r/", url, StringComparison.Ordinal);
    }
}
