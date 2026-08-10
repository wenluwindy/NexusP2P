using Microsoft.Extensions.Options;
using NexusP2P.Signaling;

namespace NexusP2P.Integration.Tests.Signaling;

/// <summary>
/// 配置校验（AD-8）。
///
/// <para>核心要求是<b>快速失败</b>：缺了 <c>PublicOrigin</c> 却照样启动，
/// 结果是生成一堆指向 localhost 的废链接，用户分享出去对方打不开 ——
/// 而这种错误很难从现象倒推回配置。</para>
/// </summary>
public sealed class SignalingOptionsTests
{
    private static readonly SignalingOptionsValidator Validator = new();

    private static SignalingOptions Valid() => new()
    {
        PublicOrigin = "https://p2p.example.com",
        RoomGracePeriodSeconds = 60,
        JoinAttemptsPerMinute = 20,
        MaxRooms = 1000,
    };

    private static ValidateOptionsResult Validate(SignalingOptions options) =>
        Validator.Validate(SignalingOptions.SectionName, options);

    [Fact]
    public void 合法配置通过()
    {
        Assert.True(Validate(Valid()).Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void 缺少_PublicOrigin_直接失败(string origin)
    {
        var options = Valid();
        options.PublicOrigin = origin;

        var result = Validate(options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("PublicOrigin", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("example.com")]              // 不是绝对 URL
    [InlineData("ftp://example.com")]        // 协议不对
    [InlineData("https://example.com/?a=1")] // 带查询串
    [InlineData("https://example.com/#x")]   // 带片段
    public void 非法的_PublicOrigin_失败(string origin)
    {
        var options = Valid();
        options.PublicOrigin = origin;

        Assert.True(Validate(options).Failed);
    }

    [Fact]
    public void 校验复用生成分享链接的那套规则()
    {
        // 两处规则漂移的后果是「配置校验通过但生成链接时抛异常」，
        // 那会变成运行时故障而不是启动时故障
        var options = Valid();
        options.PublicOrigin = "https://example.com:8443/sub";

        Assert.True(Validate(options).Succeeded);
    }

    [Fact]
    public void 宽限期为负数失败()
    {
        var options = Valid();
        options.RoomGracePeriodSeconds = -1;

        Assert.True(Validate(options).Failed);
    }

    [Fact]
    public void 宽限期为零允许但不推荐()
    {
        // 0 会让自动重连必然扑空，但那是用户的选择，不是配置错误
        var options = Valid();
        options.RoomGracePeriodSeconds = 0;

        Assert.True(Validate(options).Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void 限速值非正失败(int perMinute)
    {
        var options = Valid();
        options.JoinAttemptsPerMinute = perMinute;

        Assert.True(Validate(options).Failed);
    }

    [Fact]
    public void 房间上限非正失败()
    {
        var options = Valid();
        options.MaxRooms = 0;

        Assert.True(Validate(options).Failed);
    }

    [Fact]
    public void 配了_TURN_地址却没配密钥失败()
    {
        // 没有密钥就生不出时限凭据，中继等于配了个摆设
        var options = Valid();
        options.Turn.Urls = ["turn:p2p.example.com:3478"];
        options.Turn.Secret = "";

        var result = Validate(options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("Turn:Secret", StringComparison.Ordinal));
    }

    [Fact]
    public void 同时配了_TURN_地址与密钥通过()
    {
        var options = Valid();
        options.Turn.Urls = ["turn:p2p.example.com:3478"];
        options.Turn.Secret = "a-shared-secret";

        Assert.True(Validate(options).Succeeded);
    }

    [Fact]
    public void 完全不配_TURN_通过()
    {
        // 不配中继是合法的：只靠 host/srflx 候选直连
        Assert.True(Validate(Valid()).Succeeded);
    }

    [Fact]
    public void 凭据有效期过短失败()
    {
        var options = Valid();
        options.Turn.CredentialTtlSeconds = 30;

        Assert.True(Validate(options).Failed);
    }

    [Fact]
    public void 一次报出全部问题而不是只报第一个()
    {
        // 部署时一次看到所有错配比改一个跑一次高效得多
        var options = new SignalingOptions
        {
            PublicOrigin = "",
            RoomGracePeriodSeconds = -1,
            JoinAttemptsPerMinute = 0,
            MaxRooms = 0,
        };

        var result = Validate(options);

        Assert.True(result.Failed);
        Assert.True(result.Failures!.Count() >= 4, "应该一次列出全部问题");
    }
}
