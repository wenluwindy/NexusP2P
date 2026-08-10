using System.Diagnostics;
using System.Text;
using NexusP2P.Agent;
using NexusP2P.Agent.Settings;
using NexusP2P.Agent.Transfers;
using NexusP2P.Core.Codes;
using NexusP2P.Core.Crypto;
using NexusP2P.Transfer;
using NexusP2P.Transfer.Reconnect;
using NexusP2P.Transfer.Storage;
using NexusP2P.Transport.WebRtc;

// NexusP2P 命令行客户端。
//
// 两个用途：GUI 出来之前先能用；以及 Task 7.1 的验收就靠它跑
// （跨机器 15 GB + 拔网线 + 关程序 + 续传）。
return await CliApp.RunAsync(args);

internal static class CliApp
{
    public static async Task<int> RunAsync(string[] args)
    {
        UseUtf8Output();

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            // 让传输优雅退出，已收到的进度得以保留
            e.Cancel = true;
            Console.WriteLine();
            Console.WriteLine("正在取消…");
            cancellation.Cancel();
        };

        try
        {
            return args[0] switch
            {
                "send" => await SendAsync(args, cancellation.Token),
                "receive" => await ReceiveAsync(args, cancellation.Token),
                _ => Fail($"未知命令 \"{args[0]}\"。用 --help 查看用法。"),
            };
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("已取消。已收到的部分保留在磁盘上，可以稍后续传。");
            return 130;
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    /// <summary>
    /// 强制 UTF-8 输出。
    ///
    /// <para>Windows 控制台默认是 GBK（936），中文提示与文件路径都会变成乱码；
    /// 输出被重定向到管道时更糟 —— 调用方按 UTF-8 读会拿到一堆问号。</para>
    /// </summary>
    private static void UseUtf8Output()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // 某些宿主环境不允许改，那就只能接受默认编码
        }
    }

    private static async Task<int> SendAsync(string[] args, CancellationToken cancellationToken)
    {
        var path = Positional(args, 1) ?? throw new ArgumentException("send 需要一个文件或文件夹路径。");
        var options = ReadAgentOptions(args);

        Console.WriteLine($"正在计算校验和：{path}");
        var stopwatch = Stopwatch.StartNew();

        var manifest = await ManifestBuilder.BuildAsync(
            path,
            progress: new HashProgress(stopwatch),
            cancellationToken: cancellationToken);

        Console.WriteLine(
            $"共 {manifest.Entries.Length} 个文件，{Format(manifest.TotalLength)}，" +
            $"{manifest.TotalPieces} 个分片（耗时 {stopwatch.Elapsed.TotalSeconds:N1} 秒）");

        var secret = TransferSecret.Generate();
        var root = ResolveSourceRoot(path);

        await using var peers = ReconnectingPeerSource.ForSender(
            options,
            room =>
            {
                Console.WriteLine();
                Console.WriteLine($"  文件码：{TransferCode.Parse(room.Code)}");
                if (!string.IsNullOrEmpty(room.ShareUrlBase))
                {
                    Console.WriteLine($"  分享链接：{room.ShareUrlBase}/{room.Code}#{secret.ToBase64Url()}");
                }

                Console.WriteLine($"  密钥：{secret.ToBase64Url()}");
                Console.WriteLine();
                Console.WriteLine("等待对方接收…");
            },
            onPeerArrived: () => Console.WriteLine("对方已进入，正在建立连接…"));

        await ResilientSession.RunAsync(
            connect: peers.ConnectAsync,
            session: async (connection, token) =>
            {
                Console.WriteLine($"已连接（{Describe(peers.CandidateKind)}）。开始传输。");

                // 每次尝试都用一份新的分片源：上一条连接失败时它可能
                // 停在半途的读取状态，复用等于把上一次的故障带进新连接
                await using var source = new FilePieceSource(manifest, root);
                await new SendSession(manifest, source, secret)
                    .RunAsync(connection, new ProgressReporter(manifest.TotalLength), token);

                return true;
            },
            status: new ReconnectPrinter(),
            cancellationToken: cancellationToken);

        Console.WriteLine();
        Console.WriteLine("传输完成，对方已确认收齐并通过校验。");
        return 0;
    }

    private static async Task<int> ReceiveAsync(string[] args, CancellationToken cancellationToken)
    {
        var target = Positional(args, 1)
                     ?? throw new ArgumentException("receive 需要一个分享链接，或用文件码加 --key。");

        var options = ReadAgentOptions(args);
        var destination = Option(args, "--dest") ?? Directory.GetCurrentDirectory();

        string code;
        TransferSecret secret;

        if (ShareLinkFactory.TryParse(target, out var shareLink))
        {
            code = shareLink.Code.Digits;
            secret = shareLink.Secret;
        }
        else
        {
            code = TransferCode.Parse(target).Digits;
            var key = Option(args, "--key")
                      ?? throw new ArgumentException("用文件码接收时必须同时给出 --key。");

            if (!TransferSecret.TryFromBase64Url(key, out secret))
            {
                throw new ArgumentException("--key 不是合法的密钥。");
            }
        }

        // 传输开始前就检查，而不是传到一半才发现目标不可写
        var check = DestinationCheck.Check(destination);
        if (!check.IsUsable)
        {
            return Fail(check.Problem!);
        }

        Console.WriteLine($"正在连接（文件码 {TransferCode.Parse(code)}）…");

        await using var peers = ReconnectingPeerSource.ForReceiver(options, code);

        var result = await ResilientSession.RunAsync(
            connect: peers.ConnectAsync,
            session: (connection, token) =>
            {
                Console.WriteLine($"已连接（{Describe(peers.CandidateKind)}）。");
                return new ReceiveSession(secret, destination)
                    .RunAsync(connection, new ProgressReporter(0), new RescanReporter(), token);
            },
            status: new ReconnectPrinter(),
            cancellationToken: cancellationToken);

        Console.WriteLine();
        if (result.ResumedPieces > 0)
        {
            Console.WriteLine($"（其中 {Format(result.ResumedBytes)} 是上次留下的，本次续传）");
        }

        Console.WriteLine($"接收完成，共 {result.LandedFiles.Count} 个文件：");
        foreach (var file in result.LandedFiles)
        {
            Console.WriteLine($"  {file}");
        }

        return 0;
    }

    /// <summary>发送端读分片的基准目录：文件取其所在目录，文件夹取其上级。</summary>
    private static string ResolveSourceRoot(string path)
    {
        var full = Path.GetFullPath(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return Path.GetDirectoryName(full) ?? full;
    }

    /// <summary>
    /// 决定信令地址，按优先级：命令行 → 环境变量 → 配置文件。
    ///
    /// <para>命令行排最前是因为它最具体（「就这一次连别的服务器」）；
    /// 配置文件排最后，是那个「设一次就不用再管」的默认值。</para>
    /// </summary>
    private static AgentOptions ReadAgentOptions(string[] args)
    {
        var config = AgentConfigFile.TryLoad(out var configWarning);

        // 配置文件写坏了必须说出来。静默忽略的话，用户明明配好了地址，
        // 却对着「必须指定信令服务器」这句话完全不知道问题在哪。
        if (configWarning is not null)
        {
            Console.Error.WriteLine($"警告：{configWarning}");
        }

        var origin = Option(args, "--signaling")
                     ?? Environment.GetEnvironmentVariable("NEXUSP2P_SIGNALING")
                     ?? config?.Signaling;

        if (string.IsNullOrWhiteSpace(origin))
        {
            // $$ 让 {{ }} 成为插值定界符，于是 JSON 的单个 { } 可以直接写
            throw new ArgumentException(
                $$"""
                  没有信令服务器地址。三种指定方式，任选一种：

                    1. 命令行：  --signaling https://p2p.你的域名
                    2. 配置文件：在 {{AgentConfigFile.DefaultPath}}
                                 写 { "signaling": "https://p2p.你的域名" }
                    3. 环境变量：NEXUSP2P_SIGNALING=https://p2p.你的域名
                  """);
        }

        var options = new AgentOptions
        {
            SignalingOrigin = origin,
            IceServers = config?.IceServers ?? [],
        };

        var problems = options.Validate();
        if (problems.Count > 0)
        {
            throw new ArgumentException(string.Join("；", problems));
        }

        return options;
    }

    private static string? Positional(string[] args, int index) =>
        index < args.Length && !args[index].StartsWith('-') ? args[index] : null;

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"错误：{message}");
        return 1;
    }

    /// <summary>
    /// 把候选类型说成人话。<b>这就是「速度瓶颈说明」</b> ——
    /// 走中继时速度受限于服务器上行，用户看到「直连」还是「中继」
    /// 才知道该不该期待更快。
    /// </summary>
    internal static string Describe(CandidatePairKind? kind) => kind switch
    {
        CandidatePairKind.Host => "同局域网直连",
        CandidatePairKind.ServerReflexive => "打洞成功，公网直连",
        CandidatePairKind.Relay => "经服务器中继，速度受服务器上行带宽限制",
        _ => "连接类型未知",
    };

    internal static string Format(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):N2} GiB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):N1} MiB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):N0} KiB",
        _ => $"{bytes} B",
    };

    private static void PrintUsage() => Console.WriteLine(
        """
        NexusP2P 命令行客户端

        用法：
          nexusp2p send <文件或文件夹> --signaling <地址>
          nexusp2p receive <分享链接> --dest <目录> --signaling <地址>
          nexusp2p receive <文件码> --key <密钥> --dest <目录> --signaling <地址>

        选项：
          --signaling <地址>   信令服务器，如 https://p2p.example.com
          --dest <目录>        接收目录，默认为当前目录
          --key <密钥>         用文件码（而非完整链接）接收时的密钥

        信令地址来源，按优先级：
          1. --signaling 参数
          2. 环境变量 NEXUSP2P_SIGNALING
          3. 可执行文件旁的 nexusp2p.json 里的 "signaling"

        配置好 nexusp2p.json 之后，日常就不用再带 --signaling 了。

        断线会自动重连 3 次。3 次都失败后退出，重新运行同一条命令即可续传 ——
        进度按内容记录，与会话无关。
        """);
}

/// <summary>
/// 把重连状态打出来。
///
/// <para><b>自动重试必须是可见的。</b>悄悄重试三次只会让「网络确实不通」
/// 这件事被推迟十几秒，而用户对着一个不动的进度条完全不知道发生了什么。</para>
/// </summary>
internal sealed class ReconnectPrinter : IProgress<ReconnectStatus>
{
    public void Report(ReconnectStatus value)
    {
        switch (value.Phase)
        {
            case ReconnectPhase.WaitingBeforeRetry:
                Console.WriteLine();
                Console.WriteLine(
                    $"连接断开（{value.Reason}）。{value.Delay.TotalSeconds:N0} 秒后重连" +
                    $"（第 {value.Attempt}/{value.MaxAttempts} 次）…");
                break;

            case ReconnectPhase.GaveUp:
                Console.WriteLine();
                Console.WriteLine($"自动重连 {value.MaxAttempts} 次都没成功。最后一次的原因：{value.Reason}");
                break;
        }
    }
}

/// <summary>
/// 校验已有进度时的输出。
///
/// <para>续传前要把本地已有的 <c>.part</c> 重新校验一遍。20 GB 大约十几秒 ——
/// 不说话的话，用户看到的就是一个连上了却什么都不动的程序。</para>
/// </summary>
internal sealed class RescanReporter : IProgress<RescanProgress>
{
    private readonly Lock _gate = new();
    private bool _announced;

    public void Report(RescanProgress value)
    {
        lock (_gate)
        {
            if (!_announced)
            {
                _announced = true;
                Console.WriteLine("正在校验本地已有的部分，以便接着传…");
            }

            if (value.BytesTotal > 0)
            {
                Console.Write(
                    $"\r  已校验 {CliApp.Format(value.BytesScanned)} / {CliApp.Format(value.BytesTotal)}        ");
            }
        }
    }
}

/// <summary>算校验和的进度。限流到每秒一次，免得刷屏。</summary>
internal sealed class HashProgress(Stopwatch stopwatch) : IProgress<long>
{
    private readonly Lock _gate = new();
    private TimeSpan _lastReport = TimeSpan.Zero;

    public void Report(long hashedBytes)
    {
        // Progress<T> 的回调可能并发投递
        lock (_gate)
        {
            if (stopwatch.Elapsed - _lastReport < TimeSpan.FromSeconds(1))
            {
                return;
            }

            _lastReport = stopwatch.Elapsed;
            Console.WriteLine($"  已处理 {CliApp.Format(hashedBytes)}");
        }
    }
}

/// <summary>把传输进度打成一行不断刷新的文本。</summary>
internal sealed class ProgressReporter(long totalBytes) : IProgress<TransferProgress>
{
    private readonly RateTracker _rate = new();
    private readonly Lock _gate = new();
    private DateTimeOffset _lastDrawn = DateTimeOffset.MinValue;

    public void Report(TransferProgress value)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            _rate.Record(value.CompletedBytes, now);

            if (now - _lastDrawn < TimeSpan.FromMilliseconds(250))
            {
                return;
            }

            _lastDrawn = now;

            var total = value.TotalBytes > 0 ? value.TotalBytes : totalBytes;
            var speed = _rate.BytesPerSecond(now);
            var percent = total > 0 ? value.CompletedBytes * 100.0 / total : 0;

            Console.Write(
                $"\r  {percent,5:N1}%  {CliApp.Format(value.CompletedBytes)} / {CliApp.Format(total)}" +
                $"  {CliApp.Format((long)speed)}/s        ");
        }
    }
}
