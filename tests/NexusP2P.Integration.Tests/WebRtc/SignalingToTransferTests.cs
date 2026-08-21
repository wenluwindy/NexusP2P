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
using NexusP2P.Transport.WebRtc;

namespace NexusP2P.Integration.Tests.WebRtc;

/// <summary>
/// <b>Task 3.4 的核心验收</b>：信令服务器（3.1）+ WebRTC 传输（3.2）+
/// 传输协议（Phase 2）三者接起来，跑通完整的收发。
///
/// <para>与 <c>WebRtcEndToEndTests</c> 的区别：那里的信令是在内存里直接转交的，
/// 这里走的是<b>真的 Kestrel + 真的 WebSocket</b> —— 建房、拿码、进房、
/// 转发 SDP 与 ICE 候选全都是真的。唯一没有的是跨机器的 NAT。</para>
///
/// <para>用真实 Kestrel 而不是 <c>TestServer</c>：后者的 WebSocket 只能被
/// 它自己的客户端连上，而这里要验证的正是「真实的 <c>ClientWebSocket</c> 能连上」。</para>
/// </summary>
public sealed class SignalingToTransferTests : IAsyncLifetime
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

        // 与 Program.cs 走同一条组装路径
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

    /// <summary>跑一次「发送方建房 → 接收方输码 → 传完」的完整流程。</summary>
    private async Task<(Exception? SenderError, Exception? ReceiverError, ReceiveResult? Result, string? Code)>
        RunTransferAsync(TransferHarness harness, string destination)
    {
        var manifest = await harness.BuildManifestAsync();

        var codeReady = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        Exception? senderError = null;
        Exception? receiverError = null;
        ReceiveResult? result = null;

        var senderTask = Task.Run(async () =>
        {
            try
            {
                await using var link = await PeerConnector.OfferAsync(
                    _options,
                    room => codeReady.TrySetResult(room.Code),
                    cancellationToken: timeout.Token);

                await using var source = new MemoryPieceSource(manifest, harness.Files);
                await new SendSession(manifest, source, harness.Secret)
                    .RunAsync(link.Connection, cancellationToken: timeout.Token);
            }
            catch (Exception ex)
            {
                senderError = ex;
                codeReady.TrySetException(ex);
            }
        }, CancellationToken.None);

        string? code = null;
        var receiverTask = Task.Run(async () =>
        {
            try
            {
                code = await codeReady.Task.WaitAsync(timeout.Token);

                await using var link = await PeerConnector.AnswerAsync(_options, code, timeout.Token);

                result = await new ReceiveSession(destination)
                    .RunAsync(link.Connection, cancellationToken: timeout.Token);
            }
            catch (Exception ex)
            {
                receiverError = ex;
            }
        }, CancellationToken.None);

        var both = Task.WhenAll(senderTask, receiverTask);
        var finished = await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(100), CancellationToken.None));

        if (finished != both)
        {
            Assert.Fail(
                "经由真实信令的传输没有在 100 秒内结束。\n" +
                $"  发送端已完成 = {senderTask.IsCompleted}\n" +
                $"  接收端已完成 = {receiverTask.IsCompleted}\n" +
                $"  文件码 = {code ?? "（还没拿到）"}");
        }

        return (senderError, receiverError, result, code);
    }

    [Fact]
    public async Task 信令服务器活着()
    {
        using var client = new HttpClient();

        var response = await client.GetStringAsync(
            new Uri($"{_options.SignalingOrigin}/health"));

        Assert.Contains("\"status\":\"ok\"", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 经由真实信令完成一次传输()
    {
        using var harness = new TransferHarness().With("a.bin", 300_000);
        var destination = harness.CreateTemporaryDirectory();

        var (senderError, receiverError, result, code) = await RunTransferAsync(harness, destination);

        Assert.Null(senderError);
        Assert.Null(receiverError);
        Assert.NotNull(result);

        // 建房拿到的必须是九位数字码
        Assert.NotNull(code);
        Assert.Equal(9, code.Length);
        Assert.All(code, c => Assert.True(char.IsAsciiDigit(c)));

        await harness.AssertLandedAsync(destination);
    }

    [Fact]
    public async Task 经由真实信令传输文件夹()
    {
        using var harness = new TransferHarness()
            .With("proj/readme.md", 400)
            .With("proj/src/a.bin", 150_000)
            .With("proj/empty.dat", 0)
            .WithEmptyDirectory("proj/logs");

        var destination = harness.CreateTemporaryDirectory();

        var (senderError, receiverError, _, _) = await RunTransferAsync(harness, destination);

        Assert.Null(senderError);
        Assert.Null(receiverError);
        await harness.AssertLandedAsync(destination);
    }

    [Fact]
    public async Task 输错的码会得到明确提示而不是挂住()
    {
        // 用户最常见的错误。必须快速失败并给出可读的说明。
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var failure = await Assert.ThrowsAsync<Agent.Signaling.SignalingException>(
            () => PeerConnector.AnswerAsync(_options, "000000001", timeout.Token));

        Assert.Contains("房间不可用", failure.Message, StringComparison.Ordinal);
        Assert.False(timeout.IsCancellationRequested, "应主动失败而不是被超时掐掉");
    }

    [Fact]
    public async Task 建房后能立刻拿到文件码而不必等对方进来()
    {
        // UI 要马上把码显示出来让用户去分享，不能等对方进来才显示
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var codeReady = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var offering = Task.Run(async () =>
        {
            try
            {
                await using var link = await PeerConnector.OfferAsync(
                    _options, room => codeReady.TrySetResult(room.Code), cancellationToken: timeout.Token);
            }
            catch (OperationCanceledException)
            {
                // 没有人进来，超时取消是预期的
            }
        }, CancellationToken.None);

        var code = await codeReady.Task.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(9, code.Length);

        await timeout.CancelAsync();
        await offering;
    }

    [Fact]
    public async Task 连接建立后能判断走的是直连还是中继()
    {
        // 「瓶颈说明」要靠这个。回环环境下应该是 host 直连。
        using var harness = new TransferHarness().With("a.bin", 50_000);
        var destination = harness.CreateTemporaryDirectory();
        var manifest = await harness.BuildManifestAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var codeReady = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        CandidatePairKind kind = CandidatePairKind.Unknown;

        var senderTask = Task.Run(async () =>
        {
            await using var link = await PeerConnector.OfferAsync(
                _options, room => codeReady.TrySetResult(room.Code), cancellationToken: timeout.Token);

            // 连上就立刻读 —— 这正是界面显示「已连接（直连）」的时刻。
            // 等传完再读会拿到 Unknown：对端那时已经把连接拆了，
            // 原生侧的选中候选对随之消失。
            kind = link.CandidateKind;

            await using var source = new MemoryPieceSource(manifest, harness.Files);
            await new SendSession(manifest, source, harness.Secret)
                .RunAsync(link.Connection, cancellationToken: timeout.Token);
        }, CancellationToken.None);

        var receiverTask = Task.Run(async () =>
        {
            var code = await codeReady.Task.WaitAsync(timeout.Token);
            await using var link = await PeerConnector.AnswerAsync(_options, code, timeout.Token);
            await new ReceiveSession(destination)
                .RunAsync(link.Connection, cancellationToken: timeout.Token);
        }, CancellationToken.None);

        await Task.WhenAll(senderTask, receiverTask);

        Assert.Equal(CandidatePairKind.Host, kind);
    }
}
