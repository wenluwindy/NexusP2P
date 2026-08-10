using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NexusP2P.Signaling;
using NexusP2P.Signaling.Turn;

namespace NexusP2P.Integration.Tests.Signaling;

/// <summary>
/// TURN 时限凭据。
///
/// <para>格式由 coturn 的 <c>use-auth-secret</c> 协议规定，不能自己发挥 ——
/// 所以这里既验行为，也把<b>线上格式</b>钉死。格式一旦漂移，
/// 现象是「中继配了却永远连不上」，而那极难从客户端侧倒推。</para>
/// </summary>
public sealed class TurnCredentialServiceTests
{
    private const string Secret = "a-very-secret-value";

    private static (TurnCredentialService Service, FakeTimeProvider Clock) Create(
        string[]? urls = null, string? secret = Secret, int ttlSeconds = 3600)
    {
        var options = Options.Create(new SignalingOptions
        {
            PublicOrigin = "https://p2p.example.com",
            Turn = new SignalingOptions.TurnOptions
            {
                Urls = urls ?? ["turn:p2p.example.com:3478"],
                Secret = secret ?? string.Empty,
                CredentialTtlSeconds = ttlSeconds,
            },
        });

        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-08T12:00:00Z"));
        return (new TurnCredentialService(options, clock), clock);
    }

    [Fact]
    public void 配了地址与密钥才算配置好()
    {
        Assert.True(Create().Service.IsConfigured);
        Assert.False(Create(urls: []).Service.IsConfigured);
        Assert.False(Create(secret: "").Service.IsConfigured);
    }

    [Fact]
    public void 未配置时不下发_ICE_服务器()
    {
        // 只在局域网用是合法的部署形态：没有中继，只靠 host 候选
        var (service, _) = Create(urls: []);

        Assert.Empty(service.BuildIceServers());
    }

    [Fact]
    public void 未配置时生成凭据会抛异常()
    {
        var (service, _) = Create(secret: "");

        Assert.Throws<InvalidOperationException>(() => service.Create());
    }

    // ---- 线上格式：由 coturn 规定，不能漂移 ----

    [Fact]
    public void 用户名是过期时刻的_Unix_秒()
    {
        var (service, clock) = Create(ttlSeconds: 3600);

        var credential = service.Create();

        var expected = clock.GetUtcNow().AddSeconds(3600).ToUnixTimeSeconds();
        Assert.Equal(expected.ToString(System.Globalization.CultureInfo.InvariantCulture), credential.Username);
    }

    [Fact]
    public void 带标签时用户名是_时间戳冒号标签()
    {
        var (service, clock) = Create();

        var credential = service.Create("room-123456789");

        var expected = clock.GetUtcNow().AddSeconds(3600).ToUnixTimeSeconds();
        Assert.Equal($"{expected}:room-123456789", credential.Username);
    }

    [Fact]
    [SuppressMessage(
        "Security", "CA5350:不要使用弱加密算法",
        Justification = "刻意用 HMAC-SHA1 独立复算，以钉死 coturn 规定的线上格式。")]
    public void 密码是用户名的_HMAC_SHA1_再_base64()
    {
        // 把算法钉死。换成 SHA-256 会让 coturn 一律拒绝，
        // 而现象只是「中继连不上」—— 极难倒推。
        var (service, _) = Create();
        var credential = service.Create();

        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(Secret));
        var expected = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(credential.Username)));

        Assert.Equal(expected, credential.Password);
    }

    [Fact]
    public void 生成的凭据能被自己验过()
    {
        var (service, _) = Create();
        var credential = service.Create("room-x");

        Assert.True(service.Verify(credential.Username, credential.Password, out var reason), reason);
        Assert.Null(reason);
    }

    // ---- 时限 ----

    [Fact]
    public void 过期后不再有效()
    {
        var (service, clock) = Create(ttlSeconds: 3600);
        var credential = service.Create();

        clock.Advance(TimeSpan.FromSeconds(3601));

        Assert.False(service.Verify(credential.Username, credential.Password, out var reason));
        Assert.Contains("过期", reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void 有效期内仍然有效()
    {
        var (service, clock) = Create(ttlSeconds: 3600);
        var credential = service.Create();

        clock.Advance(TimeSpan.FromSeconds(3599));

        Assert.True(service.Verify(credential.Username, credential.Password, out _));
    }

    [Fact]
    public void 不同时刻生成的凭据不同()
    {
        var (service, clock) = Create();

        var first = service.Create();
        clock.Advance(TimeSpan.FromSeconds(1));
        var second = service.Create();

        Assert.NotEqual(first.Username, second.Username);
        Assert.NotEqual(first.Password, second.Password);
    }

    // ---- 伪造 ----

    [Fact]
    public void 签名不对时被拒()
    {
        var (service, _) = Create();
        var credential = service.Create();

        Assert.False(service.Verify(credential.Username, "伪造的签名", out var reason));
        Assert.Contains("签名", reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void 改了用户名但沿用旧签名会被拒()
    {
        // 攻击者想把过期时间往后延
        var (service, _) = Create();
        var credential = service.Create();

        var forged = (long.Parse(credential.Username.Split(':')[0]) + 999_999).ToString();

        Assert.False(service.Verify(forged, credential.Password, out _));
    }

    [Fact]
    public void 用户名格式不对时被拒()
    {
        var (service, _) = Create();

        Assert.False(service.Verify("不是时间戳", "任意", out var reason));
        Assert.Contains("时间戳", reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void 换一个密钥签出的凭据验不过()
    {
        var (mine, _) = Create();
        var (other, _) = Create(secret: "另一个密钥");

        var credential = other.Create();

        Assert.False(mine.Verify(credential.Username, credential.Password, out _));
    }

    // ---- ICE 下发 ----

    [Fact]
    public void 下发的_ICE_条目带上凭据()
    {
        var (service, _) = Create(urls: ["turn:a.example.com:3478", "turns:a.example.com:5349"]);

        var servers = service.BuildIceServers("room-1");

        var entry = Assert.Single(servers);
        Assert.Equal(["turn:a.example.com:3478", "turns:a.example.com:5349"], entry.Urls);
        Assert.False(string.IsNullOrEmpty(entry.Username));
        Assert.False(string.IsNullOrEmpty(entry.Credential));
        Assert.True(service.Verify(entry.Username!, entry.Credential!, out _));
    }

    [Fact]
    public void 密钥不出现在下发内容里()
    {
        // 共享密钥只能留在服务器上。泄露了任何人都能白嫖中继带宽。
        var (service, _) = Create();

        var entry = Assert.Single(service.BuildIceServers("room-1"));

        Assert.DoesNotContain(Secret, entry.Username, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, entry.Credential, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, string.Join(",", entry.Urls), StringComparison.Ordinal);
    }
}
