namespace NexusP2P.Agent;

/// <summary>
/// 部署配置（AD-8）。与 <see cref="Settings.AgentSettings"/> 分开 ——
/// 那是用户偏好，这是部署前定好、用户不该改的东西。
/// </summary>
public sealed record AgentOptions
{
    /// <summary>
    /// 信令服务器的基址，如 <c>https://p2p.example.com</c>。
    ///
    /// <para><b>必填。</b>缺了它 exe 连不上任何人，所以启动时就要明确报错，
    /// 而不是等用户输完码才说「连接失败」。</para>
    /// </summary>
    public string SignalingOrigin { get; init; } = string.Empty;

    /// <summary>ICE 服务器。空表示只用 host/srflx 候选（打洞失败就连不上）。</summary>
    public IReadOnlyList<string> IceServers { get; init; } = [];

    /// <summary>本地界面监听的端口。0 表示随机选一个空闲端口。</summary>
    public int LocalPort { get; init; }

    /// <summary>校验配置。返回问题列表，空表示没问题。</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(SignalingOrigin))
        {
            problems.Add("必须配置 SignalingOrigin（信令服务器地址，如 https://p2p.example.com）。");
        }
        else if (!Uri.TryCreate(SignalingOrigin, UriKind.Absolute, out var uri) ||
                 (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            problems.Add($"SignalingOrigin 不是合法的 http/https 地址：\"{SignalingOrigin}\"。");
        }

        if (LocalPort is < 0 or > 65535)
        {
            problems.Add($"LocalPort 必须在 0~65535 之间，实际为 {LocalPort}。");
        }

        return problems;
    }

    /// <summary>
    /// 把 http(s) 基址换成对应的 ws(s) 信令地址。
    ///
    /// <para><b>查询串必须单独传</b>，不能拼进 <paramref name="path"/>：
    /// <see cref="UriBuilder.Path"/> 会把 <c>?</c> 转义成 <c>%3F</c>，
    /// 于是整个查询串变成路径的一部分 —— 服务端会把
    /// <c>123456789%3Frole=receiver</c> 当成文件码，然后判定它非法。
    /// 这种错误很隐蔽：所有码都会「不存在」，看起来像服务器的问题。</para>
    /// </summary>
    public Uri BuildSignalingUri(string path, string? query = null)
    {
        var origin = new Uri(SignalingOrigin.TrimEnd('/'));
        var scheme = origin.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";

        return new UriBuilder(origin)
        {
            Scheme = scheme,
            Path = origin.AbsolutePath.TrimEnd('/') + path,
            Query = query ?? string.Empty,
        }.Uri;
    }
}
