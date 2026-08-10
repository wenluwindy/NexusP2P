using System.Diagnostics.CodeAnalysis;
using NexusP2P.Core.Crypto;

namespace NexusP2P.Core.Codes;

/// <summary>分享链接里承载的两样东西：房间码与密钥材料。</summary>
public readonly record struct ShareLink(TransferCode Code, TransferSecret Secret);

/// <summary>
/// 生成分享链接。形如：
/// <c>https://域名/r/111111111#&lt;base64url 密钥&gt;</c>
///
/// <para><b>密钥必须位于 <c>#</c> 之后</b>。URL fragment 按规范永不随请求
///发送到服务器，这是「服务器即使中继流量也无法解密」的全部依据。
/// 一旦有人把它挪到查询串里，端到端加密就在事实上失效了 ——
/// 所以有一条专门的测试盯着这件事。</para>
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

    public string Create(ShareLink link) => Create(link.Code, link.Secret);

    public string Create(TransferCode code, TransferSecret secret)
    {
        var basePath = PublicOrigin.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return $"{basePath}/{RoomPathSegment}/{code.Digits}#{secret.ToBase64Url()}";
    }

    /// <summary>
    /// 解析分享链接。<b>与基址无关</b> —— 接收方拿到的链接可能来自任何域名，
    /// 所以只看路径与片段，不校验主机。
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

        // 片段以 '#' 开头；空片段说明密钥没带上
        var fragment = uri.Fragment;
        if (fragment.Length <= 1)
        {
            return false;
        }

        if (!TransferSecret.TryFromBase64Url(fragment[1..], out var secret))
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

        link = new ShareLink(code, secret);
        return true;
    }
}
