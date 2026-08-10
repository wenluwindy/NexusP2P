using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NexusP2P.Agent;
using NexusP2P.Agent.Signaling;
using NexusP2P.Signaling;
using NexusP2P.Transport.WebRtc;

namespace NexusP2P.Integration.Tests.Signaling;

/// <summary>
/// 锁住一个曾经真实发生过的竞态：<b>进房成功到挂上信令处理器之间的窗口</b>。
///
/// <para>接收端一进房，服务器立刻通知发送端，发送端马上就发 offer。
/// 但接收端还要先把 WebRTC 对象建出来（原生库初始化几百毫秒）才挂得上处理器 ——
/// 这条 offer 正好落在窗口里。修复前它被投给一个 null 事件然后永远消失，
/// 两端各等 30 秒超时，而且<b>机器越快越容易中招</b>。</para>
///
/// <para>跨进程测试只能概率性地撞到它。这里把窗口显式拉长，让它必然发生。</para>
/// </summary>
public sealed class SignalingClientRaceTests : IAsyncLifetime
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
    public async Task 挂处理器之前到达的描述不会丢()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await using var sender = new SignalingClient(_options);
        var room = await sender.CreateRoomAsync(timeout.Token);

        await using var receiver = new SignalingClient(_options);
        await receiver.JoinRoomAsync(room.Code, asSender: false, timeout.Token);

        // 发送端一看到有人进来就发 offer —— 真实行为就是这样
        await sender.WaitForPeerAsync(timeout.Token);
        await sender.SendDescriptionAsync(new SessionDescription("v=0\r\nfake-offer", "offer"), timeout.Token);

        // 接收端这时候还在建 WebRTC 对象。用一段明确的延迟代替那几百毫秒。
        await Task.Delay(TimeSpan.FromMilliseconds(500), timeout.Token);

        var arrived = new TaskCompletionSource<SessionDescription>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.RemoteDescriptionReceived += description => arrived.TrySetResult(description);
        receiver.BeginSignalDelivery();

        var received = await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5), timeout.Token);

        Assert.Equal("offer", received.Type);
        Assert.Equal("v=0\r\nfake-offer", received.Sdp);
    }

    [Fact]
    public async Task 补发时描述仍然排在候选前面()
    {
        // 候选必须在描述之后到达，否则 WebRTC 直接把它丢掉
        //（还不知道这是哪个会话的候选）。补发不能打乱这个顺序。
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await using var sender = new SignalingClient(_options);
        var room = await sender.CreateRoomAsync(timeout.Token);

        await using var receiver = new SignalingClient(_options);
        await receiver.JoinRoomAsync(room.Code, asSender: false, timeout.Token);

        await sender.WaitForPeerAsync(timeout.Token);
        await sender.SendDescriptionAsync(new SessionDescription("v=0\r\nfake-offer", "offer"), timeout.Token);
        await sender.SendCandidateAsync(new IceCandidate("candidate:1 1 udp 1 127.0.0.1 1 typ host", "0"), timeout.Token);
        await sender.SendCandidateAsync(new IceCandidate("candidate:2 1 udp 2 127.0.0.1 2 typ host", "0"), timeout.Token);

        await Task.Delay(TimeSpan.FromMilliseconds(500), timeout.Token);

        var order = new List<string>();
        var gate = new Lock();
        var allThree = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void Record(string entry)
        {
            lock (gate)
            {
                order.Add(entry);
                if (order.Count == 3)
                {
                    allThree.TrySetResult();
                }
            }
        }

        receiver.RemoteDescriptionReceived += description => Record($"描述:{description.Type}");
        receiver.RemoteCandidateReceived += candidate => Record($"候选:{candidate.Candidate[10]}");
        receiver.BeginSignalDelivery();

        await allThree.Task.WaitAsync(TimeSpan.FromSeconds(5), timeout.Token);

        Assert.Equal(["描述:offer", "候选:1", "候选:2"], order);
    }

    [Fact]
    public async Task 开闸之后到达的信令直接投递()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await using var sender = new SignalingClient(_options);
        var room = await sender.CreateRoomAsync(timeout.Token);

        await using var receiver = new SignalingClient(_options);
        await receiver.JoinRoomAsync(room.Code, asSender: false, timeout.Token);

        var arrived = new TaskCompletionSource<SessionDescription>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.RemoteDescriptionReceived += description => arrived.TrySetResult(description);
        receiver.BeginSignalDelivery();

        // 开闸之后才发 —— 不该被攒起来
        await sender.WaitForPeerAsync(timeout.Token);
        await sender.SendDescriptionAsync(new SessionDescription("v=0\r\nlate", "answer"), timeout.Token);

        var received = await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5), timeout.Token);
        Assert.Equal("answer", received.Type);
    }

    [Fact]
    public async Task 重复开闸无害()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await using var sender = new SignalingClient(_options);
        var room = await sender.CreateRoomAsync(timeout.Token);

        await using var receiver = new SignalingClient(_options);
        await receiver.JoinRoomAsync(room.Code, asSender: false, timeout.Token);

        await sender.WaitForPeerAsync(timeout.Token);
        await sender.SendDescriptionAsync(new SessionDescription("v=0\r\nfake-offer", "offer"), timeout.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(300), timeout.Token);

        var count = 0;
        var gate = new Lock();
        receiver.RemoteDescriptionReceived += _ =>
        {
            lock (gate)
            {
                count++;
            }
        };

        receiver.BeginSignalDelivery();
        receiver.BeginSignalDelivery();
        receiver.BeginSignalDelivery();

        await Task.Delay(TimeSpan.FromMilliseconds(300), timeout.Token);

        lock (gate)
        {
            Assert.Equal(1, count);
        }
    }
}
