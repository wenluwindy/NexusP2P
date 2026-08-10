using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NexusP2P.Signaling;

namespace NexusP2P.Integration.Tests.WebRtc;

/// <summary>
/// <b>Task 3.4 的完整验收</b>：两个<b>独立的操作系统进程</b>，
/// 经由真实信令服务器和真实 WebRTC 互传文件。
///
/// <para>与 <c>SignalingToTransferTests</c> 的区别：那里两端在同一个进程里，
/// 共享同一份 libdatachannel 实例与同一个 .NET 运行时。这里是两个真的进程 ——
/// 各自加载原生库、各自握手、经由真实 socket 通信。
/// 这是「两个 exe 能互传」的最接近证明。</para>
///
/// <para>唯一还没验证的是<b>跨机器的 NAT 穿透</b>，那需要两台在不同网络下的
/// 真实机器加上部署好的 coturn。</para>
/// </summary>
[Collection(ExclusiveRun.Name)]
public sealed class CrossProcessTests : IAsyncLifetime, IDisposable
{
    private WebApplication _signaling = null!;
    private string _signalingOrigin = null!;
    private string _cliPath = null!;
    private readonly List<string> _temporaryDirectories = [];

    public async Task InitializeAsync()
    {
        var port = GetFreePort();

        var builder = WebApplication.CreateBuilder();
        builder.Configuration["Signaling:PublicOrigin"] = $"http://127.0.0.1:{port}";
        builder.Configuration["Signaling:JoinAttemptsPerMinute"] = "200";
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

        SignalingHost.ConfigureServices(builder);
        _signaling = builder.Build();
        SignalingHost.Configure(_signaling);
        await _signaling.StartAsync();

        _signalingOrigin = $"http://127.0.0.1:{port}";
        _cliPath = LocateCli();
    }

    public async Task DisposeAsync()
    {
        await _signaling.StopAsync();
        await _signaling.DisposeAsync();
    }

    public void Dispose()
    {
        foreach (var directory in _temporaryDirectories)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// 找到已构建的 CLI。测试项目并不引用它（那会把它拖进测试的依赖图），
    /// 所以按约定的输出路径去找。
    /// </summary>
    private static string LocateCli()
    {
        var here = AppContext.BaseDirectory;
        var configuration = here.Contains("Release", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";

        var root = here;
        for (var i = 0; i < 8 && root is not null; i++)
        {
            var candidate = Path.Combine(
                root, "src", "NexusP2P.Cli", "bin", configuration, "net9.0", "nexusp2p.dll");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            root = Path.GetDirectoryName(root);
        }

        throw new InvalidOperationException(
            "找不到已构建的 NexusP2P.Cli。请先执行 dotnet build 构建整个解决方案。");
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "nexusp2p-xproc", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _temporaryDirectories.Add(path);
        return path;
    }

    private sealed record ProcessRun(Process Process, StringWriter Output)
    {
        public string Text
        {
            get
            {
                lock (Output)
                {
                    return Output.ToString();
                }
            }
        }
    }

    private ProcessRun StartCli(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        startInfo.ArgumentList.Add(_cliPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add("--signaling");
        startInfo.ArgumentList.Add(_signalingOrigin);

        var output = new StringWriter();
        var process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException("启动 CLI 进程失败。");

        void Capture(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (output)
            {
                output.WriteLine(line);
            }
        }

        process.OutputDataReceived += (_, e) => Capture(e.Data);
        process.ErrorDataReceived += (_, e) => Capture(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return new ProcessRun(process, output);
    }

    /// <summary>等待进程输出里出现某个模式，返回第一个捕获组。</summary>
    private static async Task<string> WaitForPatternAsync(
        ProcessRun run, string pattern, TimeSpan timeout)
    {
        var regex = new System.Text.RegularExpressions.Regex(pattern);
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            var match = regex.Match(run.Text);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }

            if (run.Process.HasExited)
            {
                throw new InvalidOperationException(
                    $"进程已退出（代码 {run.Process.ExitCode}）但没有输出匹配 \"{pattern}\" 的内容。\n输出：\n{run.Text}");
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"等待 \"{pattern}\" 超时。\n输出：\n{run.Text}");
    }

    /// <param name="other">
    /// 另一端。超时时一并打出来 —— <b>一端卡住的原因几乎总在另一端</b>，
    /// 只报卡住那一端的输出等于什么都没报。
    /// </param>
    private static async Task<int> WaitForExitAsync(
        ProcessRun run, TimeSpan timeout, ProcessRun? other = null)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await run.Process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                run.Process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            var counterpart = other is null
                ? string.Empty
                : $"\n── 另一端 ─────────────────────\n{other.Text}";

            throw new TimeoutException(
                $"进程未在 {timeout.TotalSeconds:N0} 秒内退出。\n输出：\n{run.Text}{counterpart}");
        }

        return run.Process.ExitCode;
    }

    /// <summary>大文件比内容本身重要 —— 只比对哈希，不把两份 96 MiB 摆进内存对比。</summary>
    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);

        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static async Task<string> WriteLargeRandomFileAsync(string path, long bytes)
    {
        await using (var stream = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
        {
            var chunk = new byte[1024 * 1024];
            for (long written = 0; written < bytes; written += chunk.Length)
            {
                RandomNumberGenerator.Fill(chunk);
                await stream.WriteAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, bytes - written)));
            }
        }

        return await HashFileAsync(path);
    }

    private static byte[] WriteRandomFile(string path, int bytes)
    {
        var content = RandomNumberGenerator.GetBytes(bytes);
        File.WriteAllBytes(path, content);
        return content;
    }

    [Fact]
    public async Task 两个独立进程之间传一个文件()
    {
        var sourceDirectory = CreateTemporaryDirectory();
        var destination = CreateTemporaryDirectory();
        var sourceFile = Path.Combine(sourceDirectory, "payload.bin");
        var expected = WriteRandomFile(sourceFile, 2 * 1024 * 1024);

        var sender = StartCli("send", sourceFile);

        try
        {
            // 发送端会先打出分享链接，接收端拿它就能连
            var shareUrl = await WaitForPatternAsync(
                sender, @"分享链接：(\S+)", TimeSpan.FromSeconds(60));

            var receiver = StartCli("receive", shareUrl, "--dest", destination);

            var receiverExit = await WaitForExitAsync(receiver, TimeSpan.FromSeconds(120));
            var senderExit = await WaitForExitAsync(sender, TimeSpan.FromSeconds(30));

            Assert.True(receiverExit == 0, $"接收端退出码 {receiverExit}\n{receiver.Text}");
            Assert.True(senderExit == 0, $"发送端退出码 {senderExit}\n{sender.Text}");

            var landed = Path.Combine(destination, "payload.bin");
            Assert.True(File.Exists(landed), $"文件没有落地\n接收端输出：\n{receiver.Text}");
            Assert.Equal(expected, await File.ReadAllBytesAsync(landed));
        }
        finally
        {
            KillIfRunning(sender);
        }
    }

    [Fact]
    public async Task 两个独立进程之间传一个文件夹()
    {
        var sourceDirectory = CreateTemporaryDirectory();
        var destination = CreateTemporaryDirectory();

        var folder = Path.Combine(sourceDirectory, "MyStuff");
        Directory.CreateDirectory(Path.Combine(folder, "sub", "deep"));
        Directory.CreateDirectory(Path.Combine(folder, "logs"));   // 空目录

        var a = WriteRandomFile(Path.Combine(folder, "a.bin"), 300_000);
        var b = WriteRandomFile(Path.Combine(folder, "sub", "deep", "b.bin"), 500_000);
        File.WriteAllBytes(Path.Combine(folder, "empty.dat"), []);

        var sender = StartCli("send", folder);

        try
        {
            var shareUrl = await WaitForPatternAsync(
                sender, @"分享链接：(\S+)", TimeSpan.FromSeconds(60));

            var receiver = StartCli("receive", shareUrl, "--dest", destination);

            var receiverExit = await WaitForExitAsync(receiver, TimeSpan.FromSeconds(120));
            Assert.True(receiverExit == 0, Report(receiverExit, sender, receiver));

            var senderExit = await WaitForExitAsync(sender, TimeSpan.FromSeconds(30));
            Assert.True(senderExit == 0, Report(senderExit, sender, receiver));

            var landedRoot = Path.Combine(destination, "MyStuff");
            Assert.Equal(a, await File.ReadAllBytesAsync(Path.Combine(landedRoot, "a.bin")));
            Assert.Equal(b, await File.ReadAllBytesAsync(Path.Combine(landedRoot, "sub", "deep", "b.bin")));
            Assert.True(File.Exists(Path.Combine(landedRoot, "empty.dat")));
            Assert.True(Directory.Exists(Path.Combine(landedRoot, "logs")), "空目录没有被创建");
        }
        finally
        {
            KillIfRunning(sender);
        }
    }

    [Fact]
    public async Task 用文件码加密钥也能接收()
    {
        // 用户可能只口头念了码，密钥另外发。两条路径都要能用。
        var sourceDirectory = CreateTemporaryDirectory();
        var destination = CreateTemporaryDirectory();
        var sourceFile = Path.Combine(sourceDirectory, "payload.bin");
        var expected = WriteRandomFile(sourceFile, 200_000);

        var sender = StartCli("send", sourceFile);

        try
        {
            var code = await WaitForPatternAsync(sender, @"文件码：([\d-]+)", TimeSpan.FromSeconds(60));
            var key = await WaitForPatternAsync(sender, @"密钥：(\S+)", TimeSpan.FromSeconds(10));

            var receiver = StartCli("receive", code, "--key", key, "--dest", destination);

            var receiverExit = await WaitForExitAsync(receiver, TimeSpan.FromSeconds(120));
            Assert.True(receiverExit == 0, Report(receiverExit, sender, receiver));

            var senderExit = await WaitForExitAsync(sender, TimeSpan.FromSeconds(30));
            Assert.True(senderExit == 0, Report(senderExit, sender, receiver));

            Assert.Equal(expected, await File.ReadAllBytesAsync(Path.Combine(destination, "payload.bin")));
        }
        finally
        {
            KillIfRunning(sender);
        }
    }

    [Fact]
    public async Task 接收端用错的码会快速失败()
    {
        var destination = CreateTemporaryDirectory();
        var key = new string('A', 43);   // 长度合法但内容无关紧要

        var receiver = StartCli("receive", "000-000-001", "--key", key, "--dest", destination);

        var exitCode = await WaitForExitAsync(receiver, TimeSpan.FromSeconds(60));

        Assert.NotEqual(0, exitCode);
        Assert.Contains("房间不可用", receiver.Text, StringComparison.Ordinal);
    }

    /// <summary>失败时把两端的完整输出都摆出来 —— 只报一个退出码等于没报。</summary>
    private static string Report(int exitCode, ProcessRun sender, ProcessRun receiver) =>
        $"""
        退出码 {exitCode}

        ── 发送端 ─────────────────────
        {sender.Text}
        ── 接收端 ─────────────────────
        {receiver.Text}
        """;

    /// <summary>
    /// <b>Task 3.5 的核心验收</b>：接收端进程被<b>直接杀掉</b>（等同于拔网线、
    /// 崩溃、或用户直接关窗口），重新打开后接着传完。
    ///
    /// <para>这一条同时压到了三件事：发送端不换文件码地回到原房间；
    /// 进房应答里的 <c>peerPresent</c>（对端已经先回来了时不能干等 peer-joined）；
    /// 以及<b>硬杀之后的进度恢复</b> —— 进程被杀时没有任何收尾代码跑过，
    /// 位图只能靠重扫 <c>.part</c> 重建。</para>
    /// </summary>
    [Fact]
    public async Task 接收端进程被杀后重开能接着传()
    {
        var sourceDirectory = CreateTemporaryDirectory();
        var destination = CreateTemporaryDirectory();
        var sourceFile = Path.Combine(sourceDirectory, "big.bin");

        // 要大到「杀之前来得及传一部分、又还没传完」
        var expected = await WriteLargeRandomFileAsync(sourceFile, 96L * 1024 * 1024);

        var sender = StartCli("send", sourceFile);

        try
        {
            var shareUrl = await WaitForPatternAsync(
                sender, @"分享链接：(\S+)", TimeSpan.FromSeconds(120));

            var first = StartCli("receive", shareUrl, "--dest", destination);
            await WaitForPatternAsync(first, @"(已连接)", TimeSpan.FromSeconds(120));

            // 传一会儿再杀，确保磁盘上留下了真实进度
            await Task.Delay(TimeSpan.FromMilliseconds(400));
            var partialText = first.Text;
            KillIfRunning(first);

            var second = StartCli("receive", shareUrl, "--dest", destination);

            var receiverExit = await WaitForExitAsync(second, TimeSpan.FromSeconds(120), sender);
            Assert.True(receiverExit == 0, Report(receiverExit, sender, second));

            // 没有这一句就只能证明「重传一遍也能成功」，证明不了续传
            Assert.True(
                second.Text.Contains("本次续传", StringComparison.Ordinal),
                $"""
                 第二次接收没有报告续传，说明整个文件被重传了。

                 ── 第一次（被杀掉的那次）─────────
                 {partialText}
                 ── 第二次 ───────────────────────
                 {second.Text}
                 """);

            var senderExit = await WaitForExitAsync(sender, TimeSpan.FromSeconds(60));
            Assert.True(senderExit == 0, Report(senderExit, sender, second));

            Assert.Equal(expected, await HashFileAsync(Path.Combine(destination, "big.bin")));
        }
        finally
        {
            KillIfRunning(sender);
        }
    }

    private static void KillIfRunning(ProcessRun run)
    {
        try
        {
            if (!run.Process.HasExited)
            {
                run.Process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }

        run.Process.Dispose();
    }
}
