using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using SipSorceryThroughput;

if (args.Contains("--api"))
{
    ApiDump.Run(args.SkipWhile(a => a != "--api").Skip(1).ToArray());
    return;
}

var options = SpikeOptions.Parse(args);
options.Print();

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
builder.Logging.SetMinimumLevel(options.Verbose ? LogLevel.Debug : LogLevel.Warning);
builder.WebHost.UseUrls($"http://localhost:{options.Port}");

var app = builder.Build();

if (options.Verbose)
{
    SIPSorcery.LogFactory.Set(app.Services.GetRequiredService<ILoggerFactory>());
}

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

    using var ws = await context.WebSockets.AcceptWebSocketAsync();
    Console.WriteLine("浏览器已连接，开始协商…");
    try
    {
        await RunSessionAsync(ws, options, context.RequestAborted);
    }
    catch (Exception ex)
    {
        Console.WriteLine();
        Console.WriteLine($"!! 会话异常：{ex.GetType().Name}: {ex.Message}");
    }
});

Console.WriteLine($"请用 Chrome 或 Edge 打开  http://localhost:{options.Port}");
Console.WriteLine("（localhost 属于 secure context，所以 http 下 WebRTC 也可用）");
Console.WriteLine();
app.Run();
return;


static async Task RunSessionAsync(WebSocket ws, SpikeOptions options, CancellationToken ct)
{
    var config = new RTCConfiguration
    {
        // 本机回环测试不需要 STUN；留空即可，ICE 会用 host candidate 直连。
        iceServers = [],
    };

    using var pc = new RTCPeerConnection(config);
    var reportTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var openTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    var sendLock = new SemaphoreSlim(1, 1);

    async Task SignalAsync(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await sendLock.WaitAsync(ct);
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            sendLock.Release();
        }
    }

    pc.onicecandidate += candidate =>
    {
        if (candidate is null) return;
        _ = SignalAsync(new
        {
            type = "candidate",
            candidate = candidate.candidate,
            sdpMid = candidate.sdpMid,
            sdpMLineIndex = candidate.sdpMLineIndex,
        });
    };

    pc.onconnectionstatechange += state =>
    {
        Console.WriteLine($"  连接状态 -> {state}");
        if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed)
            openTcs.TrySetException(new IOException($"PeerConnection 进入 {state} 状态"));
    };

    pc.oniceconnectionstatechange += state => Console.WriteLine($"  ICE 状态 -> {state}");

    // .NET 作为 offerer 并主动创建通道 —— 这正是产品里 exe 发送方的角色。
    var dc = await pc.createDataChannel("bulk", new RTCDataChannelInit
    {
        ordered = true,
    });

    dc.onopen += () =>
    {
        Console.WriteLine("  DataChannel 已打开");
        openTcs.TrySetResult();
    };
    dc.onerror += err => openTcs.TrySetException(new IOException($"DataChannel 错误：{err}"));
    dc.onclose += () => reportTcs.TrySetException(new IOException("DataChannel 在收到报告前关闭"));

    var receiver = new Receiver();

    dc.onmessage += (_, protocol, data) =>
    {
        if (protocol == DataChannelPayloadProtocols.WebRTC_String)
        {
            reportTcs.TrySetResult(Encoding.UTF8.GetString(data));
            return;
        }

        if (options.Reverse) receiver.OnChunk(data);
    };

    var offer = pc.createOffer(null);
    await pc.setLocalDescription(offer);
    await SignalAsync(new
    {
        type = "offer",
        sdp = offer.sdp,
        totalBytes = options.TotalBytes,
        chunkBytes = options.ChunkBytes,
        reverse = options.Reverse,
    });

    // ---- 信令接收循环 ----
    var recvLoop = Task.Run(async () =>
    {
        var buffer = new byte[64 * 1024];
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) break;

            var node = JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count));
            var type = node?["type"]?.GetValue<string>();

            switch (type)
            {
                case "answer":
                    var setResult = pc.setRemoteDescription(new RTCSessionDescriptionInit
                    {
                        type = RTCSdpType.answer,
                        sdp = node!["sdp"]!.GetValue<string>(),
                    });
                    Console.WriteLine($"  已应用 answer：{setResult}");
                    if (setResult != SetDescriptionResultEnum.OK)
                        openTcs.TrySetException(new IOException($"setRemoteDescription 失败：{setResult}"));
                    break;

                case "candidate":
                    pc.addIceCandidate(new RTCIceCandidateInit
                    {
                        candidate = node!["candidate"]!.GetValue<string>(),
                        sdpMid = node["sdpMid"]?.GetValue<string>(),
                        sdpMLineIndex = (ushort)(node["sdpMLineIndex"]?.GetValue<int>() ?? 0),
                    });
                    break;
            }
        }
    }, ct);

    // ---- 等待通道就绪，然后开灌 ----
    await openTcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);

    var probe = SctpProbe.TryCreate(pc);
    if (probe is not null)
    {
        Console.WriteLine($"  SCTP 初始状态: {probe.Snapshot()}");
        if (options.BurstPeriodMs > 0)
        {
            probe.BurstPeriodMs = options.BurstPeriodMs;
            Console.WriteLine($"  已把发送节拍从 50 ms 改写为 {options.BurstPeriodMs} ms（反射，仅诊断用）");
        }
    }

    Console.WriteLine();
    Console.WriteLine("开始传输：");

    if (options.Reverse)
    {
        // 浏览器负责灌数据；这里只等它发完的通知。
        await reportTcs.Task.WaitAsync(TimeSpan.FromMinutes(20), ct);
        receiver.Stop();
        PrintReverseVerdict(options, receiver, probe);
    }
    else
    {
        var blaster = new Blaster(options);
        var result = await blaster.RunAsync(dc, probe, ct);

        dc.send(JsonSerializer.Serialize(new
        {
            type = "eof",
            totalBytes = result.BytesSent,
            chunkBytes = options.ChunkBytes,
        }));

        var reportJson = await reportTcs.Task.WaitAsync(TimeSpan.FromMinutes(2), ct);
        var report = JsonNode.Parse(reportJson)!;

        if (probe is not null)
        {
            Console.WriteLine();
            Console.WriteLine($"  SCTP 终态: {probe.Snapshot()}");
        }

        PrintVerdict(options, result, report);
    }

    await recvLoop.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None)
        .ContinueWith(_ => { }, CancellationToken.None);
}

static void PrintReverseVerdict(SpikeOptions options, Receiver rx, SctpProbe? probe)
{
    Console.WriteLine();
    Console.WriteLine("========== 反向结果（浏览器发 -> .NET 收） ==========");
    Console.WriteLine($"  接收字节        : {rx.Bytes:N0}  " +
                      $"{(rx.Bytes == options.TotalBytes ? "✓ 与预期一致" : $"✗ 预期 {options.TotalBytes:N0}")}");
    Console.WriteLine($"  序号连续性      : {(rx.SequenceOk ? "✓ 全部连续" : $"✗ 在 seq={rx.FirstBadSeq} 处断裂")}");
    Console.WriteLine($"  耗时            : {rx.Seconds:N2} s");
    Console.WriteLine($"  吞吐            : {rx.ThroughputMiBps:N1} MiB/s  ({rx.ThroughputMiBps * 8 / 1024:N2} Gbit/s)");
    if (probe is not null) Console.WriteLine($"  SCTP 终态       : {probe.Snapshot()}");
    Console.WriteLine();
    Console.WriteLine($"  按此吞吐，20 GiB 需 {20.0 * 1024 / rx.ThroughputMiBps / 3600:N2} 小时。");
    Console.WriteLine("====================================================");
}

static void PrintVerdict(SpikeOptions options, Blaster.Result r, JsonNode browser)
{
    var browserBytes = browser["bytesReceived"]!.GetValue<long>();
    var browserSeconds = browser["seconds"]!.GetValue<double>();
    var seqOk = browser["sequenceOk"]!.GetValue<bool>();
    var firstBadSeq = browser["firstBadSeq"]?.GetValue<long>() ?? -1;

    Console.WriteLine();
    Console.WriteLine("================ 结果 ================");
    Console.WriteLine($"  分片大小        : {options.ChunkBytes / 1024:N0} KiB");
    Console.WriteLine($"  背压水位        : {options.HighWaterBytes / 1024.0 / 1024:N0} MiB");
    Console.WriteLine();
    Console.WriteLine($"  发送字节        : {r.BytesSent:N0}");
    Console.WriteLine($"  接收字节        : {browserBytes:N0}  {(browserBytes == r.BytesSent ? "✓ 一致" : "✗ 不一致")}");
    Console.WriteLine($"  序号连续性      : {(seqOk ? "✓ 全部连续，无丢失无错序" : $"✗ 在 seq={firstBadSeq} 处断裂")}");
    Console.WriteLine();
    Console.WriteLine($"  .NET 侧耗时     : {r.Seconds:N2} s");
    Console.WriteLine($"  .NET 侧吞吐     : {r.ThroughputMiBps:N1} MiB/s  ({r.ThroughputMiBps * 8 / 1024:N2} Gbit/s)");
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

    // ---- 判定 ----
    var backpressureWorks = r.PeakBufferedAmount < (ulong)(options.HighWaterBytes * 4);
    var memorySane = r.PeakWorkingSetBytes < 1024L * 1024 * 1024;
    var dataIntact = browserBytes == r.BytesSent && seqOk;

    // 门槛的依据：产品要跑满家庭上行。50 Mbit/s ≈ 6 MiB/s，
    // 协议栈至少要能达到它的两倍才谈得上「瓶颈在网络而不在库」。
    const double RequiredMiBps = 12.0;
    var fastEnough = r.ThroughputMiBps >= RequiredMiBps;

    Console.WriteLine("  判定：");
    Console.WriteLine($"    数据完整      : {(dataIntact ? "通过" : "失败")}");
    Console.WriteLine($"    背压有效      : {(backpressureWorks ? "通过" : "失败 —— bufferedAmount 冲破水位 4 倍，说明轮询压不住")}");
    Console.WriteLine($"    内存可控      : {(memorySane ? "通过" : "失败 —— 工作集超过 1 GiB")}");
    Console.WriteLine($"    吞吐达标      : {(fastEnough ? "通过" : $"失败 —— {r.ThroughputMiBps:N1} MiB/s，低于门槛 {RequiredMiBps:N0} MiB/s")}");
    Console.WriteLine();

    var hours20Gb = 20.0 * 1024 / r.ThroughputMiBps / 3600;
    Console.WriteLine($"  按此吞吐，20 GiB 需 {hours20Gb:N2} 小时（本机回环，无任何网络开销）。");
    Console.WriteLine();

    if (dataIntact && backpressureWorks && memorySane && fastEnough)
    {
        Console.WriteLine("  => 假设成立：SIPSorcery 可以承担主传输通道。");
    }
    else if (dataIntact && backpressureWorks && memorySane)
    {
        Console.WriteLine("  => 假设不成立。互通性和稳定性没问题，但吞吐远不够：");
        Console.WriteLine("     回环上都跑不到家庭上行带宽，说明瓶颈在库内部而非网络。");
        Console.WriteLine("     需要走备胎方案（网页只走中继，P2P 仅在 exe 之间用 QUIC）。");
    }
    else
    {
        Console.WriteLine("  => 假设不成立：连基本的完整性/稳定性都没达到。");
    }

    Console.WriteLine("======================================");
}
