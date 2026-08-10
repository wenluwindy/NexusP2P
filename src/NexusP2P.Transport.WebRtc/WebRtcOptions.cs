namespace NexusP2P.Transport.WebRtc;

/// <summary>
/// PeerConnection 的配置。按 AD-8，这些值最终都来自配置文件。
/// </summary>
public sealed record WebRtcOptions
{
    public static WebRtcOptions Default { get; } = new();

    /// <summary>
    /// ICE 服务器，形如 <c>stun:stun.example.com:3478</c> 或
    /// <c>turn:user:pass@turn.example.com:3478</c>。
    /// 空列表表示只用 host 候选（同机或同局域网可用，跨公网不行）。
    /// </summary>
    public IReadOnlyList<string> IceServers { get; init; } = [];

    /// <summary>
    /// 单条消息上限。默认取保守值 —— 见
    /// <see cref="WebRtcDataChannel.SafeMaxMessageSize"/> 的说明。
    /// </summary>
    public int MaxMessageSize { get; init; } = WebRtcDataChannel.SafeMaxMessageSize;

    /// <summary>
    /// 限定本地 UDP 端口范围。0 表示不限。
    ///
    /// <para>家用路由器上把范围收窄能让端口转发规则简单些，
    /// 但会降低打洞成功率（可用端口变少）。默认不限。</para>
    /// </summary>
    public ushort PortRangeBegin { get; init; }

    public ushort PortRangeEnd { get; init; }

    /// <summary>
    /// 强制只走中继。**仅用于测试中继路径** ——
    /// 生产环境应让 ICE 自己选最优路径。
    /// </summary>
    public bool ForceRelay { get; init; }

    /// <summary>建立连接的超时。</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
