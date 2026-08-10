using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NexusP2P.Agent;
using NexusP2P.Agent.Transfers;
using NexusP2P.Signaling;
using NexusP2P.Transfer.Protocol;
using NexusP2P.Transfer.Reconnect;
using NexusP2P.Transport.Abstractions;

namespace NexusP2P.Integration.Tests.WebRtc;

/// <summary>
/// <b>Task 3.5</b>：真实信令 + 真实 WebRTC 下的自动重连。
///
/// <para>「接收端进程被杀后重开能接着传」在 <c>CrossProcessTests</c> 里 ——
/// 那条是端到端的。这里覆盖的是它压不到的两头：
/// <b>重试用尽之后怎么收场</b>，以及<b>反复重连会不会漏资源</b>。</para>
///
/// <para>真正的拔网线仍然只能人工做：这里的「断」是关掉连接，
/// 而拔网线还会让操作系统的 socket 停在半死不活的状态。</para>
/// </summary>
[Collection(ExclusiveRun.Name)]
public sealed class ReconnectOverSignalingTests : IAsyncLifetime
{
    private WebApplication _server = null!;
    private AgentOptions _options = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        _port = GetFreePort();

        var builder = WebApplication.CreateBuilder();
        builder.Configuration["Signaling:PublicOrigin"] = "https://test.example.com";
        builder.Configuration["Signaling:JoinAttemptsPerMinute"] = "400";
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{_port}");

        SignalingHost.ConfigureServices(builder);
        _server = builder.Build();
        SignalingHost.Configure(_server);
        await _server.StartAsync();

        _options = new AgentOptions { SignalingOrigin = $"http://127.0.0.1:{_port}" };
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

    /// <summary>建一对连好的真实连接。</summary>
    private async Task<(PeerLink Sender, PeerLink Receiver)> ConnectPairAsync(CancellationToken cancellationToken)
    {
        var codeReady = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var senderTask = PeerConnector.OfferAsync(
            _options, room => codeReady.TrySetResult(room.Code), cancellationToken: cancellationToken);

        var code = await codeReady.Task.WaitAsync(cancellationToken);
        var receiver = await PeerConnector.AnswerAsync(_options, code, cancellationToken);

        return (await senderTask, receiver);
    }

    [Fact]
    public async Task 信令服务器不可达时重试三次再转手动()
    {
        // 关掉服务器 —— 等价于「网线拔了，而且一直没插回来」
        await _server.StopAsync();

        var policy = new ReconnectPolicy
        {
            MaxAttempts = 3,
            InitialDelay = TimeSpan.FromMilliseconds(20),
            BackoffFactor = 1.0,
        };

        var phases = new List<ReconnectPhase>();
        var gate = new Lock();

        await using var peers = ReconnectingPeerSource.ForReceiver(_options, "123456789");

        var failure = await Assert.ThrowsAsync<ReconnectExhaustedException>(() =>
            ResilientSession.RunAsync<bool>(
                connect: peers.ConnectAsync,
                session: (_, _) => Task.FromResult(true),
                policy: policy,
                status: new DelegateProgress<ReconnectStatus>(s =>
                {
                    lock (gate)
                    {
                        phases.Add(s.Phase);
                    }
                })));

        Assert.Equal(3, failure.Attempts);

        // 「等待手动重连」这个状态必须真的被报出来 ——
        // 界面要靠它把按钮从「取消」换成「重连」
        lock (gate)
        {
            Assert.Equal(3, phases.Count(p => p == ReconnectPhase.WaitingBeforeRetry));
            Assert.Contains(ReconnectPhase.GaveUp, phases);
        }

        // 原因要能一路带到用户面前，而不是只剩一句「失败了」
        Assert.Contains("信令服务器", failure.LastFailure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 文件码不可用时立刻失败不做重试()
    {
        // 码不对重试三次只是白等，还会让用户以为是网络问题
        var policy = new ReconnectPolicy { MaxAttempts = 3, InitialDelay = TimeSpan.FromSeconds(5) };

        await using var peers = ReconnectingPeerSource.ForReceiver(_options, "000000001");

        var started = DateTime.UtcNow;

        await Assert.ThrowsAnyAsync<Exception>(() =>
            ResilientSession.RunAsync<bool>(
                connect: peers.ConnectAsync,
                session: (_, _) => Task.FromResult(true),
                policy: policy));

        // 有重试的话至少要等 5 秒
        Assert.True(
            DateTime.UtcNow - started < TimeSpan.FromSeconds(4),
            "文件码不可用却走了重试退避，用户会以为是网络问题。");
    }

    [Fact]
    public async Task 重建连接时会把上一条彻底释放()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        var codeReady = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var peers = ReconnectingPeerSource.ForSender(
            _options, room => codeReady.TrySetResult(room.Code));

        var firstConnect = peers.ConnectAsync(timeout.Token);
        var code = await codeReady.Task.WaitAsync(timeout.Token);
        await using var firstReceiver = await PeerConnector.AnswerAsync(_options, code, timeout.Token);
        var first = await firstConnect;

        // 接收端先走，腾出位子
        await firstReceiver.DisposeAsync();
        await WaitUntilAsync(() => first.Channel.State != DataChannelState.Open, timeout.Token);

        var secondConnect = peers.ConnectAsync(timeout.Token);
        await using var secondReceiver = await PeerConnector.AnswerAsync(_options, code, timeout.Token);
        var second = await secondConnect;

        // 重连不换码 —— 换了的话用户得把新码再念一遍
        Assert.Equal(code, peers.Code);

        // 新连接可用，旧连接已经彻底关掉（没关掉就是漏了一条 PeerConnection
        // 加一条 WebSocket，而重连是可能发生很多次的事）
        Assert.Equal(DataChannelState.Open, second.Channel.State);
        Assert.NotEqual(DataChannelState.Open, first.Channel.State);
    }

    [Fact]
    public async Task 反复建连再释放十次不会持续增长()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(180));

        // 先跑两轮把一次性开销（原生库、线程池、JIT）摊掉，再取基线
        for (var warmup = 0; warmup < 2; warmup++)
        {
            var (sender, receiver) = await ConnectPairAsync(timeout.Token);
            await sender.DisposeAsync();
            await receiver.DisposeAsync();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var memoryBefore = GC.GetTotalMemory(true);
        var threadsBefore = System.Diagnostics.Process.GetCurrentProcess().Threads.Count;

        for (var round = 0; round < 10; round++)
        {
            var (sender, receiver) = await ConnectPairAsync(timeout.Token);
            await sender.DisposeAsync();
            await receiver.DisposeAsync();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var memoryAfter = GC.GetTotalMemory(true);
        var threadsAfter = System.Diagnostics.Process.GetCurrentProcess().Threads.Count;

        // 每轮漏一条 PeerConnection 的话，10 轮的增长会远超这个数
        var growth = memoryAfter - memoryBefore;
        Assert.True(growth < 4L * 1024 * 1024,
            $"10 轮建连后托管堆增长了 {growth / 1024} KiB，疑似每轮漏了资源。");

        // 原生线程才是 PeerConnection 泄漏最明显的信号
        Assert.True(threadsAfter - threadsBefore < 10,
            $"10 轮建连后线程数从 {threadsBefore} 涨到 {threadsAfter}，疑似原生连接没被释放。");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50, cancellationToken);
        }

        throw new TimeoutException("等待条件成立超时。");
    }

    private sealed class DelegateProgress<T>(Action<T> onReport) : IProgress<T>
    {
        // Progress<T> 会把回调投到线程池并可能并发，这里直接同步调用，
        // 断言看到的顺序才是真实顺序
        public void Report(T value) => onReport(value);
    }
}
