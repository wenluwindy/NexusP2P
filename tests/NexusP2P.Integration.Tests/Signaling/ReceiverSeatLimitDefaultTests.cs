using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

namespace NexusP2P.Integration.Tests.Signaling;

/// <summary>
/// 席位上限的**默认**行为（AD-15）：不配置 <c>MaxReceiversPerRoom</c> 时不设天花板。
///
/// <para>与 <see cref="MultiReceiverSignalingTests"/> 分开是因为那套显式配了上限 4
/// 来测夹取；这里要的恰恰是「什么都不配」的默认值，两者不能共用同一个宿主。</para>
/// </summary>
public sealed class ReceiverSeatLimitDefaultTests : IAsyncLifetime, IDisposable
{
    private sealed class DefaultLimitApp : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Signaling:PublicOrigin", "https://test.example.com");

            // 建房不受入房限速约束，但与别的测试宿主并行时共用同一套默认配额，
            // 放宽以免这套测试因为邻居的流量而偶发失败（同目录其它类同样处理）。
            builder.UseSetting("Signaling:JoinAttemptsPerMinute", "100");

            // 刻意不设 MaxReceiversPerRoom —— 这条测试要的就是它的默认值
        }
    }

    private DefaultLimitApp _app = null!;
    private WebSocketClient _wsClient = null!;

    public Task InitializeAsync()
    {
        _app = new DefaultLimitApp();
        _ = _app.CreateClient();
        _wsClient = _app.Server.CreateWebSocketClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    public void Dispose() => _app?.Dispose();

    [Theory]
    [InlineData(9)]      // 曾经的上限 8 的下一个数
    [InlineData(64)]
    [InlineData(1000)]
    public async Task 默认配置下不再把席位数压到_8(int requested)
    {
        using var socket = await ConnectAsync($"/signal/create?maxReceivers={requested}");
        using var created = await ReceiveAsync(socket);

        Assert.Equal(requested, created.RootElement.GetProperty("maxReceivers").GetInt32());
    }

    [Fact]
    public async Task 不带参数时仍然是一对一()
    {
        using var socket = await ConnectAsync("/signal/create");
        using var created = await ReceiveAsync(socket);

        Assert.Equal(1, created.RootElement.GetProperty("maxReceivers").GetInt32());
    }

    [Fact]
    public async Task 请求_0_或负数仍被抬回_1()
    {
        using var zero = await ConnectAsync("/signal/create?maxReceivers=0");
        using var createdZero = await ReceiveAsync(zero);
        Assert.Equal(1, createdZero.RootElement.GetProperty("maxReceivers").GetInt32());

        using var negative = await ConnectAsync("/signal/create?maxReceivers=-5");
        using var createdNegative = await ReceiveAsync(negative);
        Assert.Equal(1, createdNegative.RootElement.GetProperty("maxReceivers").GetInt32());
    }

    private async Task<WebSocket> ConnectAsync(string path) =>
        await _wsClient.ConnectAsync(new Uri(_app.Server.BaseAddress, path), CancellationToken.None);

    private static async Task<JsonDocument> ReceiveAsync(WebSocket socket)
    {
        var buffer = new byte[16 * 1024];
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
        return JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count));
    }
}
