using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexusP2P.Signaling.Signaling;

/// <summary>
/// 服务器发给客户端的消息。
///
/// <para><see cref="SignalPayload"/> 里装的是 SDP 或 ICE 候选，
/// 服务器<b>只转发不解析</b>。所以它在这里是一个不透明的
/// <see cref="JsonElement"/> —— 让「服务器看不到内容」这件事由类型保证，
/// 而不是靠我们记得不去读它。</para>
/// </summary>
public sealed record ServerMessage
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>建房成功时返回的九位码（无分隔）。</summary>
    [JsonPropertyName("code")]
    public string? Code { get; init; }

    /// <summary>完整分享链接。基址来自配置（AD-8）。</summary>
    [JsonPropertyName("shareUrlBase")]
    public string? ShareUrlBase { get; init; }

    /// <summary>ICE 服务器配置，直接交给 RTCPeerConnection。</summary>
    [JsonPropertyName("iceServers")]
    public IReadOnlyList<IceServer>? IceServers { get; init; }

    /// <summary>转发的信令内容，服务器不解析。</summary>
    [JsonPropertyName("payload")]
    public JsonElement? Payload { get; init; }

    /// <summary>面向用户的错误说明。</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    /// <summary>
    /// 进房那一刻对端是否已经在房里。
    ///
    /// <para><b>重连要靠它。</b>断线重连是回到<b>已经有人的</b>房间，
    /// 而 <c>peer-joined</c> 只在对方「进来的那一刻」发一次 ——
    /// 晚回来的那一方永远等不到它，会一直卡到超时。</para>
    /// </summary>
    [JsonPropertyName("peerPresent")]
    public bool? PeerPresent { get; init; }

    /// <summary>
    /// 相关接收方的 peerId（AD-12）：<c>joined</c> 里是自己的；
    /// <c>peer-joined</c> / <c>peer-left</c> / 发给发送方的 <c>signal</c> 里
    /// 是那个接收方的。发送方没有 peerId。
    /// </summary>
    [JsonPropertyName("peerId")]
    public string? PeerId { get; init; }

    /// <summary>转发的信令来自哪个接收方。只出现在发给发送方的 <c>signal</c> 里。</summary>
    [JsonPropertyName("from")]
    public string? From { get; init; }

    /// <summary>建房时生效的接收方席位数（AD-15，回显给客户端）。</summary>
    [JsonPropertyName("maxReceivers")]
    public int? MaxReceivers { get; init; }

    /// <summary>
    /// 建房时口令是否已生效（回显给客户端）。
    ///
    /// <para>客户端据此识别「我带了口令、但旧服务器没认」的静默降级 ——
    /// 与 <see cref="MaxReceivers"/> 回显的用途一致：宁可明说，不让用户
    /// 以为自己设了口令而实际没有。旧服务器不回这个字段。</para>
    /// </summary>
    [JsonPropertyName("passwordProtected")]
    public bool? PasswordProtected { get; init; }

    /// <summary>
    /// 发送方进房（重连）那一刻已在房的接收方 peerId 列表（AD-12）。
    /// 只发给发送方 —— 接收方之间互不可见。
    /// </summary>
    [JsonPropertyName("peers")]
    public IReadOnlyList<string>? Peers { get; init; }

    public static ServerMessage Created(
        string code, string shareUrlBase, IReadOnlyList<IceServer> iceServers, int maxReceivers,
        bool passwordProtected = false) =>
        new()
        {
            Type = "created",
            Code = code,
            ShareUrlBase = shareUrlBase,
            IceServers = iceServers,
            MaxReceivers = maxReceivers,
            PasswordProtected = passwordProtected,
        };

    /// <summary>接收方的进房应答：带自己的 peerId。</summary>
    public static ServerMessage ReceiverJoined(
        IReadOnlyList<IceServer> iceServers, bool peerPresent, string peerId) =>
        new() { Type = "joined", IceServers = iceServers, PeerPresent = peerPresent, PeerId = peerId };

    /// <summary>发送方重连的进房应答：带当前在房的接收方列表。</summary>
    public static ServerMessage SenderJoined(
        IReadOnlyList<IceServer> iceServers, IReadOnlyList<string> peers) =>
        new()
        {
            Type = "joined",
            IceServers = iceServers,
            PeerPresent = peers.Count > 0,
            Peers = peers,
        };

    public static ServerMessage PeerJoined(string peerId) => new() { Type = "peer-joined", PeerId = peerId };

    public static ServerMessage PeerLeft(string peerId) => new() { Type = "peer-left", PeerId = peerId };

    /// <summary>发送方离开时发给每个接收方。接收方的对端只有发送方，不需要 peerId。</summary>
    public static ServerMessage SenderLeft() => new() { Type = "peer-left" };

    /// <summary>转发给接收方的信令（来源必然是发送方，不需要 from）。</summary>
    public static ServerMessage Signal(JsonElement payload) =>
        new() { Type = "signal", Payload = payload };

    /// <summary>转发给发送方的信令：带来源接收方的 peerId。</summary>
    public static ServerMessage SignalFrom(JsonElement payload, string from) =>
        new() { Type = "signal", Payload = payload, From = from };

    public static ServerMessage Error(string message) => new() { Type = "error", Message = message };
}

/// <summary>ICE 服务器条目，字段名与 WebRTC 的 <c>RTCIceServer</c> 对齐。</summary>
public sealed record IceServer
{
    [JsonPropertyName("urls")]
    public required IReadOnlyList<string> Urls { get; init; }

    [JsonPropertyName("username")]
    public string? Username { get; init; }

    [JsonPropertyName("credential")]
    public string? Credential { get; init; }
}

/// <summary>客户端发来的消息。服务器只认 <c>signal</c> 一种。</summary>
public sealed record ClientMessage
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("payload")]
    public JsonElement? Payload { get; init; }

    /// <summary>
    /// 目标接收方的 peerId。<b>发送方</b>在多接收方房间里必须带；
    /// 接收方带了也被忽略（只能发给发送方）。指向不存在的 peerId 时
    /// 静默丢弃 —— 接收方刚断线时发送方手里有过期的 peerId，
    /// 这是正常时序而不是协议违规（AD-12）。
    /// </summary>
    [JsonPropertyName("to")]
    public string? To { get; init; }
}
