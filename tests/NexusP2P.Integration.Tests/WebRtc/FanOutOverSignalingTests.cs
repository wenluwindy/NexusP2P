using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NexusP2P.Agent;
using NexusP2P.Agent.Transfers;
using NexusP2P.Signaling;
using NexusP2P.Transfer;
using NexusP2P.Transfer.Storage;

namespace NexusP2P.Integration.Tests.WebRtc;

/// <summary>
/// <b>Task 9.3 的核心验收</b>：真实信令（Kestrel + WebSocket）+ 真实 WebRTC，
/// 一个发送方对多个接收方同时传输（AD-10/11/12/13）。
///
/// <para>与 <c>FanOutEndToEndTests</c> 的区别：那里是内存管道，验证编排逻辑；
/// 这里建房声明 <c>maxReceivers</c>、接收方逐个输码进房、信令按 peerId 路由、
/// 每个接收方一条真实 DataChannel —— 全都是真的。唯一没有的是跨机器 NAT。</para>
/// </summary>
public sealed class FanOutOverSignalingTests : IAsyncLifetime
{
    private WebApplication _server = null!;
    private AgentOptions _options = null!;

    public async Task InitializeAsync()
    {
        var port = GetFreePort();

        var builder = WebApplication.CreateBuilder();
        builder.Configuration["Signaling:PublicOrigin"] = "https://test.example.com";
        builder.Configuration["Signaling:JoinAttemptsPerMinute"] = "200";
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

        SignalingHost.ConfigureServices(builder);
        _server = builder.Build();
        SignalingHost.Configure(_server);

        await _server.StartAsync();

        _options = new AgentOptions { SignalingOrigin = $"http://127.0.0.1:{port}" };
    }

    public async Task DisposeAsync()
    {
        await _server.StopAsync();
        await _server.DisposeAsync();
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task 一个发送方经真实信令同时传给三个接收方()
    {
        using var harness = new TransferHarness()
            .With("payload/data.bin", 400_000, seed: 11)
            .With("payload/note.txt", 2_000, seed: 12);

        var manifest = await harness.BuildManifestAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var codeReady = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        const int receiverCount = 3;
        var destinations = Enumerable.Range(0, receiverCount)
            .Select(_ => harness.CreateTemporaryDirectory())
            .ToArray();

        // 「所有接收端都收完」就是接纳循环的退出条件
        var allReceiversDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var doneCount = 0;

        await using var source = new MemoryPieceSource(manifest, harness.Files);
        using var cache = new CipherPieceCache(manifest, source, harness.Secret);
        await using var sender = new FanOutSender(_options, manifest, harness.Secret, cache);

        Exception? senderError = null;
        var senderTask = Task.Run(async () =>
        {
            try
            {
                var room = await sender.RunAsync(
                    maxReceivers: receiverCount,
                    onRoomCreated: created => codeReady.TrySetResult(created.Code),
                    until: allReceiversDone.Task,
                    cancellationToken: timeout.Token);

                Assert.Equal(receiverCount, room.MaxReceivers);
                await sender.WhenAllLinksSettledAsync();
            }
            catch (Exception ex)
            {
                senderError = ex;
                codeReady.TrySetException(ex);
            }
        }, CancellationToken.None);

        var receiverErrors = new Exception?[receiverCount];
        var results = new ReceiveResult?[receiverCount];

        var receiverTasks = Enumerable.Range(0, receiverCount).Select(i => Task.Run(async () =>
        {
            try
            {
                var code = await codeReady.Task.WaitAsync(timeout.Token);
                await using var link = await PeerConnector.AnswerAsync(_options, code, timeout.Token);

                results[i] = await new ReceiveSession(harness.Secret, destinations[i])
                    .RunAsync(link.Connection, cancellationToken: timeout.Token);
            }
            catch (Exception ex)
            {
                receiverErrors[i] = ex;
            }
            finally
            {
                if (Interlocked.Increment(ref doneCount) == receiverCount)
                {
                    allReceiversDone.TrySetResult();
                }
            }
        }, CancellationToken.None)).ToArray();

        var all = Task.WhenAll(receiverTasks.Append(senderTask));
        var finished = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(150), CancellationToken.None));

        if (finished != all)
        {
            Assert.Fail(
                "扇出传输没有在 150 秒内结束。\n" +
                $"  发送端已完成 = {senderTask.IsCompleted}（错误 = {senderError?.Message ?? "无"}）\n" +
                $"  接收端已完成 = [{string.Join(", ", receiverTasks.Select(t => t.IsCompleted))}]\n" +
                $"  接收端错误 = [{string.Join("；", receiverErrors.Select(e => e?.Message ?? "无"))}]");
        }

        Assert.Null(senderError);
        for (var i = 0; i < receiverCount; i++)
        {
            Assert.True(receiverErrors[i] is null, $"接收端 {i} 失败：{receiverErrors[i]}");
            Assert.NotNull(results[i]);
            await harness.AssertLandedAsync(destinations[i]);
        }

        // AD-13：三条链路共享密文 —— 分片只加密了一遍
        var locator = new PieceLocator(manifest);
        Assert.Equal(locator.TotalPieces, (int)cache.Encryptions);

        // 三条链路都完成
        var links = sender.Peers;
        Assert.Equal(receiverCount, links.Count);
        Assert.All(links, link => Assert.Equal(FanOutLinkState.Completed, link.State));
    }

    [Fact]
    public async Task 席位满后再进来的人得到与码不存在相同的错误()
    {
        using var harness = new TransferHarness().With("a.bin", 60_000);
        var manifest = await harness.BuildManifestAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var codeReady = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var source = new MemoryPieceSource(manifest, harness.Files);
        using var cache = new CipherPieceCache(manifest, source, harness.Secret);
        await using var sender = new FanOutSender(_options, manifest, harness.Secret, cache);

        var destination = harness.CreateTemporaryDirectory();

        var senderTask = Task.Run(async () =>
        {
            try
            {
                await sender.RunAsync(
                    maxReceivers: 1,
                    onRoomCreated: created => codeReady.TrySetResult(created.Code),
                    until: stop.Task,
                    cancellationToken: timeout.Token);
            }
            catch (Exception ex)
            {
                codeReady.TrySetException(ex);
            }
        }, CancellationToken.None);

        var code = await codeReady.Task.WaitAsync(timeout.Token);

        // 第一个接收方占掉唯一的席位并收完
        var firstTask = Task.Run(async () =>
        {
            await using var link = await PeerConnector.AnswerAsync(_options, code, timeout.Token);
            return await new ReceiveSession(harness.Secret, destination)
                .RunAsync(link.Connection, cancellationToken: timeout.Token);
        }, CancellationToken.None);

        // 等第一个真的进了房（席位被占住）再试第二个
        await Task.Delay(500, timeout.Token);

        var failure = await Assert.ThrowsAsync<Agent.Signaling.SignalingException>(
            () => PeerConnector.AnswerAsync(_options, code, timeout.Token));

        // 防枚举（AD-12）：席位满与码不存在的措辞必须一字不差
        Assert.Contains("房间不可用", failure.Message, StringComparison.Ordinal);

        var result = await firstTask;
        Assert.NotNull(result);
        await harness.AssertLandedAsync(destination);

        stop.TrySetResult();
        await senderTask;
    }
}
