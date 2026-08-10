using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DataChannelDotnet;
using DataChannelDotnet.Bindings;
using DataChannelDotnet.Data;
using LibDataChannelThroughput;

var options = SpikeOptions.Parse(args);
options.Print();

RtcTools.Preload();
RtcTools.OnUnhandledException = ex => Console.WriteLine($"!! 原生回调里的异常：{ex}");

ApplySctpSettings(options);

// WebRoot 必须在 CreateBuilder 时给：浏览器页面是从 SipSorcery spike 链接过来的，
// 只存在于输出目录里，而默认的 wwwroot 查找路径是项目目录。
// 用 WebHost.UseWebRoot 事后改会抛 NotSupportedException。
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
});
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
builder.Logging.SetMinimumLevel(options.Verbose ? LogLevel.Debug : LogLevel.Warning);
builder.WebHost.UseUrls($"http://localhost:{options.Port}");

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWebSockets();

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    Console.WriteLine("浏览器已连接，开始协商…");

    try
    {
        await RunSessionAsync(socket, options, context.RequestAborted);
    }
    catch (Exception ex)
    {
        Console.WriteLine();
        Console.WriteLine($"!! 会话异常：{ex.GetType().Name}: {ex.Message}");
    }
});

Console.WriteLine($"请用 Chrome 或 Edge 打开  http://localhost:{options.Port}");
Console.WriteLine();
app.Run();
return;


static void ApplySctpSettings(SpikeOptions options)
{
    if (options.SctpSendBufferSize <= 0 && options.SctpMaxChunksOnQueue <= 0)
    {
        return;
    }

    // ⚠️ 实测：只填部分字段会让连接在传输中途失败（RTC_ICE_FAILED）。
    // rtcSetSctpSettings 接收完整结构体，未填字段默认为 0，而库把 0 当成
    // 「设为 0」而不是「保持默认」。ADR-001 的结论是不要动这些设置。
    var settings = new rtcSctpSettings
    {
        sendBufferSize = options.SctpSendBufferSize,
        maxChunksOnQueue = options.SctpMaxChunksOnQueue,
    };

    RtcTools.SetSctpSettings(settings);
    Console.WriteLine("已应用自定义 SCTP 设置（注意：ADR-001 建议不要这么做）。");
}

static async Task RunSessionAsync(WebSocket socket, SpikeOptions options, CancellationToken cancellationToken)
{
    var sendLock = new SemaphoreSlim(1, 1);

    async Task SignalAsync(object payload)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        await sendLock.WaitAsync(cancellationToken);
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
            }
        }
        finally
        {
            sendLock.Release();
        }
    }

    using var peer = new DataChannelDotnet.Impl.RtcPeerConnection(new RtcPeerConfiguration
    {
        IceServers = [],                      // 回环测试不需要 STUN
        MaxMessageSize = options.MaxMessageSize,
        DisableAutoNegotiation = false,       // 建通道时自动生成 offer
    });

    var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var reportReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var offerSent = false;

    // 参数不能叫 _：那样下面的 `_ = SignalAsync(...)` 会被解析成给它赋值
    peer.OnLocalDescriptionSafe += (_unusedPeer, description) =>
    {
        if (description.Type != RtcDescriptionType.Offer || offerSent)
        {
            return;
        }

        offerSent = true;
        _ = SignalAsync(new
        {
            type = "offer",
            sdp = description.Sdp,
            totalBytes = options.TotalBytes,
            chunkBytes = options.ChunkBytes,
            reverse = false,
        });
    };

    peer.OnCandidateSafe += (_unusedPeer2, candidate) => _ = SignalAsync(new
    {
        type = "candidate",
        candidate = candidate.Content,
        sdpMid = candidate.Mid,
        sdpMLineIndex = 0,
    });

    peer.OnConnectionStateChange += (_, state) =>
    {
        Console.WriteLine($"  连接状态 -> {state}");
        if (state is rtcState.RTC_FAILED or rtcState.RTC_CLOSED)
        {
            opened.TrySetException(new IOException($"PeerConnection 进入 {state}。"));
        }
    };

    peer.OnIceStateChange += (_, state) => Console.WriteLine($"  ICE 状态 -> {state}");

    // .NET 作为 offerer 并主动创建通道 —— 正是产品里 exe 发送方的角色
    using var channel = peer.CreateDataChannel(new RtcCreateDataChannelArgs
    {
        Label = "bulk",
        Protocol = RtcDataChannelProtocol.Binary,
    });

    channel.OnOpen += _ =>
    {
        Console.WriteLine("  DataChannel 已打开");
        opened.TrySetResult();
    };
    channel.OnError += (_, error) => opened.TrySetException(new IOException($"DataChannel 错误：{error}"));
    channel.OnClose += _ => reportReceived.TrySetException(new IOException("收到报告前通道已关闭。"));
    channel.OnTextReceivedSafe += (_, e) => reportReceived.TrySetResult(e.Text);

    // ---- 信令接收循环 ----
    var receiveLoop = Task.Run(async () =>
    {
        var buffer = new byte[64 * 1024];
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) break;

            var node = JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count));
            switch (node?["type"]?.GetValue<string>())
            {
                case "answer":
                    peer.SetRemoteDescription(new RtcDescription
                    {
                        Type = RtcDescriptionType.Answer,
                        Sdp = node!["sdp"]!.GetValue<string>(),
                    });
                    Console.WriteLine("  已应用 answer");
                    break;

                case "candidate":
                    peer.AddRemoteCandidate(new RtcCandidate
                    {
                        Content = node!["candidate"]!.GetValue<string>(),
                        Mid = node["sdpMid"]?.GetValue<string>() ?? "0",
                    });
                    break;
            }
        }
    }, cancellationToken);

    await opened.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

    var native = NativeChannel.From(channel);
    Console.WriteLine($"  原生通道 id = {native.Id}，初始缓冲 = {native.BufferedAmount} 字节");
    Console.WriteLine();
    Console.WriteLine("开始传输：");

    var result = await new Blaster(options).RunAsync(channel, native, cancellationToken);

    channel.Send(JsonSerializer.Serialize(new
    {
        type = "eof",
        totalBytes = result.BytesSent,
        chunkBytes = options.ChunkBytes,
    }));

    var reportJson = await reportReceived.Task.WaitAsync(TimeSpan.FromMinutes(2), cancellationToken);
    PrintVerdict(options, result, JsonNode.Parse(reportJson)!);

    await receiveLoop.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None)
        .ContinueWith(_ => { }, CancellationToken.None);
}

static void PrintVerdict(SpikeOptions options, Blaster.Result r, JsonNode browser)
{
    var browserBytes = browser["bytesReceived"]!.GetValue<long>();
    var browserSeconds = browser["seconds"]!.GetValue<double>();
    var sequenceOk = browser["sequenceOk"]!.GetValue<bool>();
    var firstBadSeq = browser["firstBadSeq"]?.GetValue<long>() ?? -1;

    Console.WriteLine();
    Console.WriteLine("================ 结果 ================");
    Console.WriteLine("  库              : libdatachannel (DataChannelDotnet 1.3.1)");
    Console.WriteLine($"  分片大小        : {options.ChunkBytes / 1024:N0} KiB");
    Console.WriteLine($"  背压水位        : {options.HighWaterBytes / 1024.0 / 1024:N0} MiB");
    Console.WriteLine();
    Console.WriteLine($"  发送字节        : {r.BytesSent:N0}");
    Console.WriteLine($"  接收字节        : {browserBytes:N0}  " +
                      $"{(browserBytes == r.BytesSent ? "✓ 一致" : "✗ 不一致")}");
    Console.WriteLine("  序号连续性      : " +
                      (sequenceOk ? "✓ 全部连续，无丢失无错序" : $"✗ 在 seq={firstBadSeq} 处断裂"));
    Console.WriteLine();
    Console.WriteLine($"  .NET 侧耗时     : {r.Seconds:N2} s");
    Console.WriteLine($"  .NET 侧吞吐     : {r.ThroughputMiBps:N1} MiB/s  " +
                      $"({r.ThroughputMiBps * 8 / 1024:N2} Gbit/s)");
    Console.WriteLine($"  浏览器侧耗时    : {browserSeconds:N2} s");
    Console.WriteLine($"  浏览器侧吞吐    : {browserBytes / 1024.0 / 1024 / browserSeconds:N1} MiB/s");
    Console.WriteLine();
    Console.WriteLine($"  托管堆峰值      : {r.PeakManagedBytes / 1024.0 / 1024:N1} MiB");
    Console.WriteLine($"  工作集峰值      : {r.PeakWorkingSetBytes / 1024.0 / 1024:N1} MiB");
    Console.WriteLine($"  bufferedAmount 峰值 : {r.PeakBufferedAmount / 1024.0 / 1024:N1} MiB");
    Console.WriteLine();
    Console.WriteLine($"  背压触发次数    : {r.StallCount:N0}");
    Console.WriteLine($"  背压等待总时长  : {r.StallSeconds:N2} s（占 {r.StallSeconds / r.Seconds * 100:N1}%）");
    Console.WriteLine();

    const double RequiredMiBps = 12.0;
    var dataIntact = browserBytes == r.BytesSent && sequenceOk;
    var backpressureWorks = r.PeakBufferedAmount < options.HighWaterBytes * 4;
    var memorySane = r.PeakWorkingSetBytes < 1024L * 1024 * 1024;
    var fastEnough = r.ThroughputMiBps >= RequiredMiBps;

    Console.WriteLine("  判定：");
    Console.WriteLine($"    数据完整      : {(dataIntact ? "通过" : "失败")}");
    Console.WriteLine($"    背压有效      : {(backpressureWorks ? "通过" : "失败 —— 缓冲冲破水位 4 倍")}");
    Console.WriteLine($"    内存可控      : {(memorySane ? "通过" : "失败 —— 工作集超过 1 GiB")}");
    Console.WriteLine("    吞吐达标      : " +
                      (fastEnough ? "通过" : $"失败 —— {r.ThroughputMiBps:N1} MiB/s 低于门槛 {RequiredMiBps:N0}"));
    Console.WriteLine();
    Console.WriteLine($"  按此吞吐，20 GiB 需 {20.0 * 1024 / r.ThroughputMiBps / 3600:N2} 小时（本机回环）。");
    Console.WriteLine();
    Console.WriteLine(dataIntact && backpressureWorks && memorySane && fastEnough
        ? "  => 假设成立：libdatachannel 可以承担主传输通道。"
        : "  => 未达标，需要继续评估下一个候选。");
    Console.WriteLine("======================================");
}
