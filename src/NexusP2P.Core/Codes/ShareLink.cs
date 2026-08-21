using System.Diagnostics.CodeAnalysis;

namespace NexusP2P.Core.Codes;

/// <summary>分享链接里承载的东西。V3 起只有房间码。</summary>
public readonly record struct ShareLink(TransferCode Code);

/// <summary>
/// 生成分享链接。形如：
/// <c>https://域名/r/111111111</c>
///
/// <para><b>V3 起链接里不再有密钥。</b>密钥由发送方在数据通道建立后直接推给
/// 接收方（见 <c>MessageType.KeyOffer</c>），所以链接就是「文件码的可点击
/// 形式」，本身不含任何秘密。</para>
///
/// <para>这解决的是一个产品问题而不是技术问题：43 个字符的密钥根本无法口头
/// 转述，用户只能把它和链接一起发到聊天工具里 —— 而既然要用聊天工具，
/// 不如直接发文件。把密钥从人的手里拿走，九位码才真正可用。</para>
///
/// <para><b>代价必须写明</b>：V1/V2 里密钥在 URL fragment 中，规范保证它永不
/// 发往服务器，所以信令服务器从密码学上无法解密任何字节；V3 里服务器若
/// <b>主动</b>在 SDP 交换阶段做中间人就能拿到密钥。即从「服务器无能为力」
/// 退化为「服务器不主动作恶即安全」。被动记录流量的服务器仍然一无所获 ——
/// 密钥在 DTLS 里传输。</para>
///
/// <para>基址来自配置而非硬编码（AD-8）。服务器<b>绑定的地址不等于对外公开的
/// URL</b> —— 反向代理、NAT、端口映射都会让两者不同，所以必须显式配置，
/// 不能从请求的 Host 头推断（那个可以被污染）。</para>
/// </summary>
public sealed class ShareLinkFactory
{
    /// <summary>链接里房间码前面的路径段。</summary>
    public const string RoomPathSegment = "r";

    public Uri PublicOrigin { get; }

    /// <param name="publicOrigin">
    /// 对外公开的基址，如 <c>https://p2p.example.com</c>。必须是 http/https 绝对地址。
    /// </param>
    public ShareLinkFactory(string publicOrigin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicOrigin);

        if (!Uri.TryCreate(publicOrigin.TrimEnd('/'), UriKind.Absolute, out var uri))
        {
            throw new ArgumentException(
                $"PublicOrigin 不是合法的绝对 URL：\"{publicOrigin}\"。", nameof(publicOrigin));
        }

        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            throw new ArgumentException(
                $"PublicOrigin 的协议必须是 http 或 https，实际为 \"{uri.Scheme}\"。", nameof(publicOrigin));
        }

        if (!string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.Query))
        {
            throw new ArgumentException(
                "PublicOrigin 不能带查询串或片段。", nameof(publicOrigin));
        }

        PublicOrigin = uri;
    }

    public string Create(ShareLink link) => Create(link.Code);

    public string Create(TransferCode code)
    {
        var basePath = PublicOrigin.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return $"{basePath}/{RoomPathSegment}/{code.Digits}";
    }

    /// <summary>
    /// 解析分享链接。<b>与基址无关</b> —— 接收方拿到的链接可能来自任何域名，
    /// 所以只看路径，不校验主机。
    ///
    /// <para><b>片段被忽略</b>：V1/V2 生成的链接带 <c>#密钥</c>，
    /// 它们仍然要能解析出文件码。用户不该因为拿到的是一条旧链接就被卡住 ——
    /// 而那段密钥现在只是一段无用的字符。</para>
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? url, out ShareLink link)
    {
        link = default;

        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 ||
            !string.Equals(segments[^2], RoomPathSegment, StringComparison.Ordinal))
        {
            return false;
        }

        if (!TransferCode.TryParse(segments[^1], out var code))
        {
            return false;
        }

        link = new ShareLink(code);
        return true;
    }
}
