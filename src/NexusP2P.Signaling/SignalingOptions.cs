using Microsoft.Extensions.Options;
using NexusP2P.Core.Codes;

namespace NexusP2P.Signaling;

/// <summary>
/// 信令服务器的配置。按 AD-8，全部部署相关的值都外置到配置文件，
/// 部署前改、不重新编译。
/// </summary>
public sealed class SignalingOptions
{
    public const string SectionName = "Signaling";

    /// <summary>
    /// 对外公开的基址，用于生成分享链接。例如 <c>https://p2p.example.com</c>。
    ///
    /// <para><b>必须显式配置，不能从请求的 Host 头推断。</b>
    /// 服务器绑定的地址不等于对外公开的 URL —— 反向代理、NAT、端口映射
    /// 都会让两者不同；而 Host 头是客户端可控的，用它生成链接等于把
    /// 一个可污染的值写进用户要分享出去的东西里。</para>
    /// </summary>
    public string PublicOrigin { get; set; } = string.Empty;

    /// <summary>
    /// 两端都断开后房间保留多久（秒）。
    ///
    /// <para>这个宽限期是<b>自动重连能成功的前提</b>（AD-7）：网络抖动时双方的
    /// 信令连接往往同时掉线，若房间立刻释放，自动重连必然扑空。</para>
    /// </summary>
    public int RoomGracePeriodSeconds { get; set; } = 60;

    /// <summary>同一 IP 每分钟允许的入房尝试次数。防止九位码被枚举。</summary>
    public int JoinAttemptsPerMinute { get; set; } = 20;

    /// <summary>房间总数上限。防止有人靠不停建房把内存吃光。</summary>
    public int MaxRooms { get; set; } = 1000;

    /// <summary>
    /// 单个房间的接收方席位上限（V2，AD-15）。建房请求的 <c>maxReceivers</c>
    /// 会被夹到 [1, 此值]。
    ///
    /// <para><b>默认不限制</b>（<see cref="int.MaxValue"/>）：席位数由发送方按
    /// 自己的上行带宽与内存自行决定。真正的约束是物理的 —— 每条链路一个
    /// PeerConnection，N 条链路平分同一条上行 —— 而不是这里的一个数字。</para>
    ///
    /// <para>公开部署想给房间设个天花板时，把它配成具体数值即可；
    /// 超出的建房请求会被夹到该值（客户端从 <c>created</c> 的回显里得知）。</para>
    /// </summary>
    public int MaxReceiversPerRoom { get; set; } = int.MaxValue;

    /// <summary>
    /// 是否跑在反向代理（nginx / Caddy / IIS）后面。
    ///
    /// <para><b>放在代理后面必须打开它，否则入房限速会失效。</b>
    /// 限速是按来源 IP 算的，而经过代理之后每个请求的来源 IP 都是代理自己 ——
    /// 所有人共用一个配额，几十次入房尝试之后全体被 429，
    /// 而真正想枚举文件码的人也一样不受限（他只占大家共用的那一份）。</para>
    ///
    /// <para><b>默认关闭，而且必须显式打开。</b>不在代理后面却信任
    /// <c>X-Forwarded-For</c> 更糟：那个头是客户端可以随便写的，
    /// 于是任何人都能靠伪造它绕过限速。这不是「方便起见默认开着」的选项。</para>
    /// </summary>
    public bool BehindReverseProxy { get; set; }

    /// <summary>
    /// 可信代理的 IP 列表。留空表示只信任本机（代理与本服务同机部署，
    /// 这也是最常见的情况）。
    ///
    /// <para>代理在另一台机器上时必须在这里列出它的地址 ——
    /// 否则转发头会被忽略，限速仍然按代理的 IP 算。</para>
    /// </summary>
    public string[] KnownProxies { get; set; } = [];

    /// <summary>TURN 配置。Task 3.3 才真正用到，这里先把形状定下来。</summary>
    public TurnOptions Turn { get; set; } = new();

    public sealed class TurnOptions
    {
        /// <summary>TURN 服务器地址，例如 <c>turn:p2p.example.com:3478</c>。留空表示不提供中继。</summary>
        public string[] Urls { get; set; } = [];

        /// <summary>生成时限凭据用的共享密钥。配了 <see cref="Urls"/> 就必须配它。</summary>
        public string Secret { get; set; } = string.Empty;

        /// <summary>时限凭据的有效期（秒）。</summary>
        public int CredentialTtlSeconds { get; set; } = 3600;
    }
}

/// <summary>
/// 启动时校验配置。
///
/// <para><b>快速失败而不是带着错配置跑起来。</b>缺了 <c>PublicOrigin</c> 却照样启动，
/// 结果是生成一堆指向 localhost 的废链接，用户分享出去对方打不开 ——
/// 而这种错误很难从现象倒推回配置。</para>
/// </summary>
public sealed class SignalingOptionsValidator : IValidateOptions<SignalingOptions>
{
    public ValidateOptionsResult Validate(string? name, SignalingOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.PublicOrigin))
        {
            failures.Add(
                $"必须配置 {SignalingOptions.SectionName}:PublicOrigin（对外公开的基址，" +
                "如 https://p2p.example.com）。缺了它生成的分享链接对方打不开。");
        }
        else
        {
            try
            {
                // 复用生成分享链接的那套校验，避免两处规则漂移
                _ = new ShareLinkFactory(options.PublicOrigin);
            }
            catch (ArgumentException ex)
            {
                failures.Add($"{SignalingOptions.SectionName}:PublicOrigin 不合法：{ex.Message}");
            }
        }

        if (options.RoomGracePeriodSeconds < 0)
        {
            failures.Add("RoomGracePeriodSeconds 不能为负数。");
        }

        if (options.JoinAttemptsPerMinute < 1)
        {
            failures.Add("JoinAttemptsPerMinute 至少为 1。");
        }

        if (options.MaxRooms < 1)
        {
            failures.Add("MaxRooms 至少为 1。");
        }

        if (options.MaxReceiversPerRoom < 1)
        {
            failures.Add("MaxReceiversPerRoom 至少为 1。");
        }

        if (options.Turn.Urls.Length > 0 && string.IsNullOrWhiteSpace(options.Turn.Secret))
        {
            failures.Add("配置了 Turn:Urls 就必须同时配置 Turn:Secret，否则无法生成时限凭据。");
        }

        if (options.Turn.CredentialTtlSeconds < 60)
        {
            failures.Add("Turn:CredentialTtlSeconds 至少为 60。");
        }

        foreach (var proxy in options.KnownProxies)
        {
            if (!System.Net.IPAddress.TryParse(proxy, out _))
            {
                failures.Add($"KnownProxies 里的 \"{proxy}\" 不是合法的 IP 地址。");
            }
        }

        if (options.KnownProxies.Length > 0 && !options.BehindReverseProxy)
        {
            // 配了却没开，说明部署者以为已经生效了。这种「配置看着对、
            // 行为却不对」的状态必须在启动时打掉，而不是等限速出问题才发现。
            failures.Add(
                "配置了 KnownProxies 但 BehindReverseProxy 是 false —— " +
                "转发头不会被采信，入房限速仍然按代理的 IP 算。要么打开 BehindReverseProxy，要么删掉 KnownProxies。");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
