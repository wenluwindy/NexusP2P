using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using NexusP2P.Signaling.Signaling;

namespace NexusP2P.Signaling.Turn;

/// <summary>一组时限 TURN 凭据。</summary>
public readonly record struct TurnCredential(string Username, string Password, DateTimeOffset ExpiresAt);

/// <summary>
/// 生成 coturn 的时限凭据（<c>use-auth-secret</c> 模式，
/// 即 draft-uberti-behave-turn-rest-00）。
///
/// <para><b>为什么不用固定账号密码</b>：分享链接是公开传播的，
/// 而 exe 与网页都要拿到 TURN 凭据。固定密码一旦泄露，
/// 任何人都能白嫖你家的中继带宽，而且改密码要重启服务、所有在传的都断。
/// 时限凭据的密钥只在服务器上，客户端拿到的东西一小时后自动失效。</para>
///
/// <para>格式由 coturn 规定，不能自己发挥：</para>
/// <code>
/// username = "&lt;过期时刻的 Unix 秒&gt;"            （可选加 ":用户名"）
/// password = base64( HMAC-SHA1(username, 共享密钥) )
/// </code>
///
/// <para><b>HMAC-SHA1 是协议规定的</b>，不是选择。coturn 只认这个 ——
/// 换成 SHA-256 会让凭据一律校验失败。这里的 SHA1 不用于抗碰撞，
/// 只作为 MAC，所以不构成安全问题。</para>
/// </summary>
public sealed class TurnCredentialService(IOptions<SignalingOptions> options, TimeProvider timeProvider)
{
    private SignalingOptions.TurnOptions Turn => options.Value.Turn;

    /// <summary>是否配置了中继。没配就只能靠直连。</summary>
    public bool IsConfigured => Turn.Urls.Length > 0 && !string.IsNullOrWhiteSpace(Turn.Secret);

    /// <summary>
    /// 生成一组新凭据。<paramref name="userTag"/> 只用于服务器日志排查，
    /// 不参与鉴权，也不该放任何隐私信息。
    /// </summary>
    public TurnCredential Create(string? userTag = null)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("未配置 TURN（需要同时配置 Turn:Urls 与 Turn:Secret）。");
        }

        var expiresAt = timeProvider.GetUtcNow().AddSeconds(Turn.CredentialTtlSeconds);
        var unixSeconds = expiresAt.ToUnixTimeSeconds();

        var username = string.IsNullOrWhiteSpace(userTag)
            ? unixSeconds.ToString(CultureInfo.InvariantCulture)
            : $"{unixSeconds.ToString(CultureInfo.InvariantCulture)}:{userTag}";

        return new TurnCredential(username, Sign(username, Turn.Secret), expiresAt);
    }

    /// <summary>
    /// 组装下发给客户端的 ICE 服务器列表。
    ///
    /// <para>没配 TURN 时返回空列表 —— 客户端只能靠 host/srflx 候选，
    /// 打洞失败就连不上。这是合法的部署形态（比如只在局域网用）。</para>
    /// 
    /// <para>V2 改进：即使没有配置 Secret，也会返回配置的 STUN 服务器（Urls），
    /// 这样可以改善 NAT 穿透成功率。只有需要 TURN 中继时才必须配 Secret。</para>
    /// </summary>
    public IReadOnlyList<IceServer> BuildIceServers(string? userTag = null)
    {
        // V2: 如果配置了 Urls 但没有 Secret，将 Urls 作为 STUN 服务器返回
        if (Turn.Urls.Length > 0 && string.IsNullOrWhiteSpace(Turn.Secret))
        {
            return
            [
                new IceServer
                {
                    Urls = Turn.Urls,
                },
            ];
        }

        if (!IsConfigured)
        {
            return [];
        }

        var credential = Create(userTag);

        return
        [
            new IceServer
            {
                Urls = Turn.Urls,
                Username = credential.Username,
                Credential = credential.Password,
            },
        ];
    }

    /// <summary>
    /// 校验一组凭据。<b>服务器自己不需要这个</b>（coturn 会验），
    /// 提供它是为了让测试能证明「我们生成的东西确实能被验过」。
    /// </summary>
    public bool Verify(string username, string password, out string? reason)
    {
        if (!IsConfigured)
        {
            reason = "未配置 TURN。";
            return false;
        }

        var expiryText = username.Split(':', 2)[0];
        if (!long.TryParse(expiryText, CultureInfo.InvariantCulture, out var unixSeconds))
        {
            reason = "用户名的前缀不是 Unix 时间戳。";
            return false;
        }

        if (DateTimeOffset.FromUnixTimeSeconds(unixSeconds) <= timeProvider.GetUtcNow())
        {
            reason = "凭据已过期。";
            return false;
        }

        // 固定时间比较：签名校验用普通字符串比较会泄露时序信息
        var expected = Encoding.UTF8.GetBytes(Sign(username, Turn.Secret));
        var actual = Encoding.UTF8.GetBytes(password);

        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            reason = "签名不匹配。";
            return false;
        }

        reason = null;
        return true;
    }

    [SuppressMessage(
        "Security", "CA5350:不要使用弱加密算法",
        Justification =
            "HMAC-SHA1 由 coturn 的 use-auth-secret 协议规定（draft-uberti-behave-turn-rest-00），" +
            "不是可选项 —— 换成 SHA-256 会让所有凭据校验失败。" +
            "此处 SHA-1 仅作 MAC 使用，不依赖其抗碰撞性，因此不构成实际风险。")]
    private static string Sign(string username, string secret)
    {
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(secret));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(username)));
    }
}
