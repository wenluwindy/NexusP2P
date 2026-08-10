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

    public static ServerMessage Created(string code, string shareUrlBase, IReadOnlyList<IceServer> iceServers) =>
        new() { Type = "created", Code = code, ShareUrlBase = shareUrlBase, IceServers = iceServers };

    public static ServerMessage Joined(IReadOnlyList<IceServer> iceServers, bool peerPresent) =>
        new() { Type = "joined", IceServers = iceServers, PeerPresent = peerPresent };

    public static ServerMessage PeerJoined() => new() { Type = "peer-joined" };

    public static ServerMessage PeerLeft() => new() { Type = "peer-left" };

    public static ServerMessage Signal(JsonElement payload) =>
        new() { Type = "signal", Payload = payload };

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
}
