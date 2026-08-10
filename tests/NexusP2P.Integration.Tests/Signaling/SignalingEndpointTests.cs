using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

namespace NexusP2P.Integration.Tests.Signaling;

public sealed class SignalingEndpointTests : IAsyncLifetime, IDisposable
{
    private const string PublicOrigin = "https://test.example.com";
    private const int JoinLimit = 5;

    private sealed class SignalingApp : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Signaling:PublicOrigin", PublicOrigin);
            builder.UseSetting("Signaling:JoinAttemptsPerMinute", JoinLimit.ToString());
            builder.UseSetting("Signaling:RoomGracePeriodSeconds", "60");
        }
    }

    private SignalingApp _app = null!;
    private WebSocketClient _wsClient = null!;

    // xunit 2.x 的 IAsyncLifetime 用 Task 而不是 ValueTask
    public Task InitializeAsync()
    {
        _app = new SignalingApp();
        _ = _app.CreateClient();   // 触发主机启动（含配置校验）
        _wsClient = _app.Server.CreateWebSocketClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    /// <summary>只为满足 CA1001（有可释放字段的类型应可释放）。真正的清理在 DisposeAsync。</summary>
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

    /// <summary>建房并返回文件码。</summary>
    private async Task<(WebSocket Socket, string Code)> CreateRoomAsync()
    {
        var socket = await ConnectAsync("/signal/create");
        using var created = await ReceiveAsync(socket);

        Assert.Equal("created", Field(created, "type"));
        return (socket, Field(created, "code"));
    }

    // ---- 建房与入房 ----

    [Fact]
    public async Task 建房返回九位码与分享链接基址()
    {
        using var socket = await ConnectAsync("/signal/create");
        using var created = await ReceiveAsync(socket);

        Assert.Equal("created", Field(created, "type"));

        var code = Field(created, "code");
        Assert.Equal(9, code.Length);
        Assert.All(code, c => Assert.True(char.IsAsciiDigit(c)));

        // 基址来自配置（AD-8），不是从请求的 Host 头推断的
        Assert.Equal($"{PublicOrigin}/r", Field(created, "shareUrlBase"));
    }

    [Fact]
    public async Task 用码入房成功且发送方收到通知()
    {
        var (sender, code) = await CreateRoomAsync();
        using var senderScope = sender;

        using var receiver = await ConnectAsync($"/signal/join/{code}");
        using var joined = await ReceiveAsync(receiver);
        Assert.Equal("joined", Field(joined, "type"));

        using var notice = await ReceiveAsync(sender);
        Assert.Equal("peer-joined", Field(notice, "type"));
    }

    [Fact]
    public async Task 信令被原样转发给对端()
    {
        var (sender, code) = await CreateRoomAsync();
        using var senderScope = sender;

        using var receiver = await ConnectAsync($"/signal/join/{code}");
        using var joined = await ReceiveAsync(receiver);
        using var peerJoined = await ReceiveAsync(sender);

        // 服务器不解析 payload，所以随便塞一个结构进去都该原样出去
        await SendAsync(sender,
            """{"type":"signal","payload":{"sdp":"v=0 fake","custom":[1,2,3]}}""");

        using var forwarded = await ReceiveAsync(receiver);

        Assert.Equal("signal", Field(forwarded, "type"));
        var payload = forwarded.RootElement.GetProperty("payload");
        Assert.Equal("v=0 fake", payload.GetProperty("sdp").GetString());
        Assert.Equal(3, payload.GetProperty("custom").GetArrayLength());
    }

    [Fact]
    public async Task 反向也能转发()
    {
        var (sender, code) = await CreateRoomAsync();
        using var senderScope = sender;

        using var receiver = await ConnectAsync($"/signal/join/{code}");
        using var joined = await ReceiveAsync(receiver);
        using var peerJoined = await ReceiveAsync(sender);

        await SendAsync(receiver, """{"type":"signal","payload":{"answer":"ok"}}""");

        using var forwarded = await ReceiveAsync(sender);
        Assert.Equal("signal", Field(forwarded, "type"));
        Assert.Equal("ok", forwarded.RootElement.GetProperty("payload").GetProperty("answer").GetString());
    }

    [Fact]
    public async Task 对端离开时收到通知()
    {
        var (sender, code) = await CreateRoomAsync();
        using var senderScope = sender;

        using var receiver = await ConnectAsync($"/signal/join/{code}");
        using var joined = await ReceiveAsync(receiver);
        using var peerJoined = await ReceiveAsync(sender);

        // 用 CloseOutputAsync 而不是 CloseAsync + Dispose：
        // TestHost 的 WebSocket 在客户端 Dispose 时会连带撕掉服务端那侧的缓冲区，
        // 于是从另一条连接上读都会失败。CloseOutputAsync 只发关闭帧。
        await receiver.CloseOutputAsync(
            WebSocketCloseStatus.NormalClosure, "走了", CancellationToken.None);

        using var left = await ReceiveAsync(sender);
        Assert.Equal("peer-left", Field(left, "type"));
    }

    // ---- 不做枚举预言机 ----

    /// <summary>
    /// 「码不存在」「码格式不对」「位子已被占」必须给出<b>完全相同</b>的错误消息。
    /// 任何差异都会让九位码有了枚举预言机。
    /// </summary>
    [Fact]
    public async Task 三种入房失败给出完全相同的错误消息()
    {
        var (sender, code) = await CreateRoomAsync();
        using var senderScope = sender;

        // 位子已被占
        using var first = await ConnectAsync($"/signal/join/{code}");
        using var joined = await ReceiveAsync(first);
        using var peerJoined = await ReceiveAsync(sender);

        using var occupiedSocket = await ConnectAsync($"/signal/join/{code}");
        using var occupied = await ReceiveAsync(occupiedSocket);

        // 码不存在（格式合法）
        using var missingSocket = await ConnectAsync("/signal/join/000000001");
        using var missing = await ReceiveAsync(missingSocket);

        // 码格式不对
        using var malformedSocket = await ConnectAsync("/signal/join/abc");
        using var malformed = await ReceiveAsync(malformedSocket);

        Assert.Equal("error", Field(occupied, "type"));
        Assert.Equal("error", Field(missing, "type"));
        Assert.Equal("error", Field(malformed, "type"));

        var occupiedMessage = Field(occupied, "message");
        Assert.Equal(occupiedMessage, Field(missing, "message"));
        Assert.Equal(occupiedMessage, Field(malformed, "message"));
    }

    // ---- 限速 ----

    [Fact]
    public async Task 入房尝试超限返回_429()
    {
        using var client = _app.CreateClient();

        // 限速在 WebSocket 升级之前，所以普通 GET 也会消耗配额并返回 400
        for (var i = 0; i < JoinLimit; i++)
        {
            var allowed = await client.GetAsync(new Uri("/signal/join/000000001", UriKind.Relative));
            Assert.Equal(HttpStatusCode.BadRequest, allowed.StatusCode);
        }

        var limited = await client.GetAsync(new Uri("/signal/join/000000002", UriKind.Relative));

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("60", limited.Headers.RetryAfter?.ToString());
    }

    [Fact]
    public async Task 建房不受入房限速影响()
    {
        // 限速只针对入房尝试。建房是自己发起的，不构成枚举风险。
        using var client = _app.CreateClient();

        for (var i = 0; i < JoinLimit + 3; i++)
        {
            var response = await client.GetAsync(new Uri("/signal/create", UriKind.Relative));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);   // 非 WS 请求
        }
    }

    // ---- 其它 ----

    [Fact]
    public async Task 健康检查返回活跃房间数()
    {
        using var client = _app.CreateClient();
        var (sender, _) = await CreateRoomAsync();
        using var senderScope = sender;

        var response = await client.GetAsync(new Uri("/health", UriKind.Relative));
        response.EnsureSuccessStatusCode();

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("ok", body.RootElement.GetProperty("status").GetString());
        Assert.True(body.RootElement.GetProperty("activeRooms").GetInt32() >= 1);
        Assert.Equal(PublicOrigin, body.RootElement.GetProperty("publicOrigin").GetString());
        Assert.False(body.RootElement.GetProperty("relayConfigured").GetBoolean());
    }

    [Fact]
    public async Task 非_WebSocket_请求返回_400()
    {
        using var client = _app.CreateClient();

        var response = await client.GetAsync(new Uri("/signal/create", UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task 未知类型的客户端消息被忽略而不断开()
    {
        var (sender, code) = await CreateRoomAsync();
        using var senderScope = sender;

        using var receiver = await ConnectAsync($"/signal/join/{code}");
        using var joined = await ReceiveAsync(receiver);
        using var peerJoined = await ReceiveAsync(sender);

        // 未知类型与畸形 JSON 都不该让连接断掉
        await SendAsync(sender, """{"type":"unknown-thing"}""");
        await SendAsync(sender, "这不是 JSON");
        await SendAsync(sender, """{"type":"signal","payload":{"still":"works"}}""");

        using var forwarded = await ReceiveAsync(receiver);
        Assert.Equal("signal", Field(forwarded, "type"));
        Assert.Equal("works", forwarded.RootElement.GetProperty("payload").GetProperty("still").GetString());
    }

    [Fact]
    public async Task 宽限期内可以用同一个码重连回原房间()
    {
        var (sender, code) = await CreateRoomAsync();
        using var senderScope = sender;

        await sender.CloseOutputAsync(
            WebSocketCloseStatus.NormalClosure, "断了", CancellationToken.None);

        // 服务端处理「离开」是异步的，所以重连要重试几次 ——
        // 真实客户端也是这么做的（AD-7 的自动重连本身就带退避）
        for (var attempt = 0; attempt < 20; attempt++)
        {
            using var reconnected = await ConnectAsync($"/signal/join/{code}?role=sender");
            using var response = await ReceiveAsync(reconnected);

            if (Field(response, "type") == "joined")
            {
                return;   // 宽限期内成功回到原房间
            }

            await Task.Delay(50);
        }

        Assert.Fail("宽限期内应该能用同一个码以发送方身份重连回原房间");
    }
}
