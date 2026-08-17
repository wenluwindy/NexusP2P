using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

namespace NexusP2P.Integration.Tests.Signaling;

/// <summary>
/// V2 多接收方信令的端点级验证（AD-12/15）：peerId 分配、from/to 路由、
/// 接收方之间互不可见、旧客户端在默认房间里行为与 V1 一致。
/// </summary>
public sealed class MultiReceiverSignalingTests : IAsyncLifetime, IDisposable
{
    private const string PublicOrigin = "https://test.example.com";

    private sealed class SignalingApp : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Signaling:PublicOrigin", PublicOrigin);
            builder.UseSetting("Signaling:JoinAttemptsPerMinute", "100");
            builder.UseSetting("Signaling:RoomGracePeriodSeconds", "60");
            builder.UseSetting("Signaling:MaxReceiversPerRoom", "4");
        }
    }

    private SignalingApp _app = null!;
    private WebSocketClient _wsClient = null!;

    public Task InitializeAsync()
    {
        _app = new SignalingApp();
        _ = _app.CreateClient();
        _wsClient = _app.Server.CreateWebSocketClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    public void Dispose() => _app?.Dispose();

    // ---- 辅助 ----

    private Task<WebSocket> ConnectAsync(string path) =>
        _wsClient.ConnectAsync(new Uri($"ws://localhost{path}"), CancellationToken.None);

    private static async Task<JsonDocument> ReceiveAsync(WebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await socket.ReceiveAsync(buffer, cts.Token);
        return JsonDocument.Parse(buffer.AsMemory(0, result.Count));
    }

    private static Task SendAsync(WebSocket socket, string json) =>
        socket.SendAsync(
            Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

    private static string Field(JsonDocument document, string name) =>
        document.RootElement.GetProperty(name).GetString()!;

    private async Task<(WebSocket Sender, string Code)> CreateRoomAsync(int maxReceivers)
    {
        var socket = await ConnectAsync($"/signal/create?maxReceivers={maxReceivers}");
        using var created = await ReceiveAsync(socket);

        Assert.Equal("created", Field(created, "type"));
        Assert.Equal(maxReceivers, created.RootElement.GetProperty("maxReceivers").GetInt32());
        return (socket, Field(created, "code"));
    }

    /// <summary>入房并吃掉发送方的 peer-joined 通知，返回 (socket, peerId)。</summary>
    private async Task<(WebSocket Socket, string PeerId)> JoinAsync(WebSocket sender, string code)
    {
        var socket = await ConnectAsync($"/signal/join/{code}");
        using var joined = await ReceiveAsync(socket);
        Assert.Equal("joined", Field(joined, "type"));
        var peerId = Field(joined, "peerId");

        using var notice = await ReceiveAsync(sender);
        Assert.Equal("peer-joined", Field(notice, "type"));
        Assert.Equal(peerId, Field(notice, "peerId"));

        return (socket, peerId);
    }

    // ---- 席位与 peerId ----

    [Fact]
    public async Task 建房回显生效的席位数且夹到上限()
    {
        // 配置上限 4，请求 99 → 生效 4
        using var socket = await ConnectAsync("/signal/create?maxReceivers=99");
        using var created = await ReceiveAsync(socket);

        Assert.Equal(4, created.RootElement.GetProperty("maxReceivers").GetInt32());
    }

    [Fact]
    public async Task 不带_maxReceivers_默认_1_第二个接收方被拒且错误消息与码不存在一致()
    {
        using var socket = await ConnectAsync("/signal/create");
        using var created = await ReceiveAsync(socket);
        Assert.Equal(1, created.RootElement.GetProperty("maxReceivers").GetInt32());
        var code = Field(created, "code");

        var (first, _) = await JoinAsync(socket, code);
        using var firstScope = first;

        using var second = await ConnectAsync($"/signal/join/{code}");
        using var rejected = await ReceiveAsync(second);
        using var missingSocket = await ConnectAsync("/signal/join/000000001");
        using var missing = await ReceiveAsync(missingSocket);

        Assert.Equal("error", Field(rejected, "type"));
        Assert.Equal(Field(missing, "message"), Field(rejected, "message"));
    }

    [Fact]
    public async Task 每个接收方拿到互不相同的_peerId_发送方逐个收到通知()
    {
        var (sender, code) = await CreateRoomAsync(3);
        using var senderScope = sender;

        var ids = new HashSet<string>();
        var sockets = new List<WebSocket>();
        try
        {
            for (var i = 0; i < 3; i++)
            {
                var (socket, peerId) = await JoinAsync(sender, code);
                sockets.Add(socket);
                Assert.True(ids.Add(peerId), $"peerId {peerId} 重复了");
            }
        }
        finally
        {
            foreach (var socket in sockets)
            {
                socket.Dispose();
            }
        }
    }

    // ---- from / to 路由 ----

    [Fact]
    public async Task 发送方按_to_路由_各接收方只收到发给自己的()
    {
        var (sender, code) = await CreateRoomAsync(2);
        using var senderScope = sender;

        var (r1, id1) = await JoinAsync(sender, code);
        using var r1Scope = r1;
        var (r2, id2) = await JoinAsync(sender, code);
        using var r2Scope = r2;

        await SendAsync(sender, $$"""{"type":"signal","payload":{"seq":1},"to":"{{id1}}"}""");
        await SendAsync(sender, $$"""{"type":"signal","payload":{"seq":2},"to":"{{id2}}"}""");

        using var got1 = await ReceiveAsync(r1);
        Assert.Equal("signal", Field(got1, "type"));
        Assert.Equal(1, got1.RootElement.GetProperty("payload").GetProperty("seq").GetInt32());

        using var got2 = await ReceiveAsync(r2);
        Assert.Equal(2, got2.RootElement.GetProperty("payload").GetProperty("seq").GetInt32());
    }

    [Fact]
    public async Task 接收方的信令带_from_到达发送方()
    {
        var (sender, code) = await CreateRoomAsync(2);
        using var senderScope = sender;

        var (r1, id1) = await JoinAsync(sender, code);
        using var r1Scope = r1;

        await SendAsync(r1, """{"type":"signal","payload":{"answer":"ok"}}""");

        using var forwarded = await ReceiveAsync(sender);
        Assert.Equal("signal", Field(forwarded, "type"));
        Assert.Equal(id1, Field(forwarded, "from"));
        Assert.Equal("ok", forwarded.RootElement.GetProperty("payload").GetProperty("answer").GetString());
    }

    [Fact]
    public async Task 接收方带_to_也只会到发送方()
    {
        // 接收方之间互不可见：r1 指名发给 r2，消息仍然到发送方，r2 什么都收不到
        var (sender, code) = await CreateRoomAsync(2);
        using var senderScope = sender;

        var (r1, _) = await JoinAsync(sender, code);
        using var r1Scope = r1;
        var (r2, id2) = await JoinAsync(sender, code);
        using var r2Scope = r2;

        await SendAsync(r1, $$"""{"type":"signal","payload":{"sneaky":true},"to":"{{id2}}"}""");

        using var forwarded = await ReceiveAsync(sender);
        Assert.Equal("signal", Field(forwarded, "type"));
        Assert.True(forwarded.RootElement.GetProperty("payload").GetProperty("sneaky").GetBoolean());

        // r2 不该收到任何东西：用一条后续消息证明「没有插队的」
        await SendAsync(sender, $$"""{"type":"signal","payload":{"marker":1},"to":"{{id2}}"}""");
        using var markers = await ReceiveAsync(r2);
        Assert.Equal(1, markers.RootElement.GetProperty("payload").GetProperty("marker").GetInt32());
    }

    [Fact]
    public async Task to_指向不存在的_peerId_静默丢弃不断开()
    {
        var (sender, code) = await CreateRoomAsync(2);
        using var senderScope = sender;

        var (r1, id1) = await JoinAsync(sender, code);
        using var r1Scope = r1;

        await SendAsync(sender, """{"type":"signal","payload":{"lost":true},"to":"deadbeef"}""");
        await SendAsync(sender, $$"""{"type":"signal","payload":{"kept":true},"to":"{{id1}}"}""");

        using var got = await ReceiveAsync(r1);
        Assert.True(got.RootElement.GetProperty("payload").GetProperty("kept").GetBoolean());
    }

    [Fact]
    public async Task 多接收方房间里不带_to_的发送方消息被静默丢弃()
    {
        var (sender, code) = await CreateRoomAsync(2);
        using var senderScope = sender;

        var (r1, id1) = await JoinAsync(sender, code);
        using var r1Scope = r1;
        var (r2, id2) = await JoinAsync(sender, code);
        using var r2Scope = r2;

        // 两个接收方在房：不带 to 没有明确目标
        await SendAsync(sender, """{"type":"signal","payload":{"ambiguous":true}}""");
        await SendAsync(sender, $$"""{"type":"signal","payload":{"ok":1},"to":"{{id1}}"}""");
        await SendAsync(sender, $$"""{"type":"signal","payload":{"ok":2},"to":"{{id2}}"}""");

        using var got1 = await ReceiveAsync(r1);
        Assert.Equal(1, got1.RootElement.GetProperty("payload").GetProperty("ok").GetInt32());
        using var got2 = await ReceiveAsync(r2);
        Assert.Equal(2, got2.RootElement.GetProperty("payload").GetProperty("ok").GetInt32());
    }

    // ---- 离开 ----

    [Fact]
    public async Task 接收方离开时发送方收到带_peerId_的_peer_left()
    {
        var (sender, code) = await CreateRoomAsync(2);
        using var senderScope = sender;

        var (r1, id1) = await JoinAsync(sender, code);
        using var r1Scope = r1;

        await r1.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "走了", CancellationToken.None);

        using var left = await ReceiveAsync(sender);
        Assert.Equal("peer-left", Field(left, "type"));
        Assert.Equal(id1, Field(left, "peerId"));
    }

    [Fact]
    public async Task 发送方重连的进房应答携带在房接收方列表()
    {
        var (sender, code) = await CreateRoomAsync(2);

        var (r1, id1) = await JoinAsync(sender, code);
        using var r1Scope = r1;
        var (r2, id2) = await JoinAsync(sender, code);
        using var r2Scope = r2;

        await sender.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "断了", CancellationToken.None);
        sender.Dispose();

        // 服务端处理「离开」是异步的，重连要重试几次（真实客户端也带退避）
        for (var attempt = 0; attempt < 20; attempt++)
        {
            using var reconnected = await ConnectAsync($"/signal/join/{code}?role=sender");
            using var response = await ReceiveAsync(reconnected);

            if (Field(response, "type") == "joined")
            {
                Assert.True(response.RootElement.GetProperty("peerPresent").GetBoolean());
                var peers = response.RootElement.GetProperty("peers").EnumerateArray()
                    .Select(e => e.GetString()).ToHashSet();
                Assert.Equal([id1, id2], peers);
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail("宽限期内发送方应能重连回原房间并拿到接收方列表");
    }
}
