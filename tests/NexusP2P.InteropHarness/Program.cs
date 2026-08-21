using NexusP2P.Agent.Transfers;
using NexusP2P.Core.Crypto;
using NexusP2P.InteropHarness;
using NexusP2P.Transfer;
using NexusP2P.Transfer.Protocol;

// 跨实现互通测试的 C# 一侧。
//
// 由 src/NexusP2P.Web/tests/interop.mjs 启动：Node 跑网页端的 SendSession，
// 这个进程跑 C# 的 ReceiveSession，两者经 stdin/stdout 对接。
//
// 用法：NexusP2P.InteropHarness receive <base64url 密钥> <落盘目录>
//
// V3 起接收端**不使用**那个密钥参数：密钥由发送方在通道里推来。
// 参数位保留只是为了两端脚本的调用形状不变。
//
// **诊断信息一律走 stderr。** stdout 是协议通道，往里写一个字节就会把
// 帧流搞乱，而症状会是「对端收到畸形帧」——完全指不到真正的原因。

if (args.Length < 3 || args[0] is not ("receive" or "send"))
{
    await Console.Error.WriteLineAsync(
        """
        用法：
          NexusP2P.InteropHarness receive <base64url 密钥> <落盘目录>
          NexusP2P.InteropHarness send    <base64url 密钥> <要发送的文件或文件夹>
        """);
    return 2;
}

if (!TransferSecret.TryFromBase64Url(args[1], out var secret))
{
    await Console.Error.WriteLineAsync($"密钥不合法：{args[1]}");
    return 2;
}

// 必须用原始的 stdin/stdout 流：Console.In / Console.Out 是文本的，
// 会按编码转换字节，二进制帧过一遍就废了。
await using var channel = new StdioDataChannel(
    Console.OpenStandardInput(), Console.OpenStandardOutput());

await using var connection = new ProtocolConnection(channel);

// 订阅者（ProtocolConnection）挂好之后才开读 —— 反过来会丢掉头几条消息
channel.Start();

using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));

try
{
    if (args[0] == "receive")
    {
        return await ReceiveAsync(connection, secret, args[2], timeout.Token);
    }

    return await SendAsync(connection, secret, args[2], timeout.Token);
}
catch (Exception ex)
{
    await Console.Error.WriteLineAsync($"FAILED {ex.GetType().Name}: {ex.Message}");
    return 1;
}

async Task<int> ReceiveAsync(
    ProtocolConnection connection, TransferSecret unusedSecret, string destination, CancellationToken token)
{
    _ = unusedSecret;   // V3：密钥由对端在通道里推来
    Directory.CreateDirectory(destination);

    var result = await new ReceiveSession(destination).RunAsync(connection, cancellationToken: token);

    // 结果走 stderr，让 Node 侧能读到又不污染协议通道
    await Console.Error.WriteLineAsync(
        $"OK files={result.LandedFiles.Count} bytes={result.Manifest.TotalLength} " +
        $"hash={result.Manifest.Hash}");

    foreach (var file in result.LandedFiles)
    {
        await Console.Error.WriteLineAsync($"FILE {file}");
    }

    return 0;
}

async Task<int> SendAsync(
    ProtocolConnection connection, TransferSecret secret, string path, CancellationToken token)
{
    var manifest = await ManifestBuilder.BuildAsync(path, cancellationToken: token);
    var root = Path.GetDirectoryName(
        Path.GetFullPath(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))!;

    // 清单哈希先报出来，好让 Node 侧比对两端是否算成同一个值
    await Console.Error.WriteLineAsync(
        $"MANIFEST files={manifest.Entries.Length} bytes={manifest.TotalLength} hash={manifest.Hash}");

    await using var source = new FilePieceSource(manifest, root);
    await new SendSession(manifest, source, secret).RunAsync(connection, cancellationToken: token);

    await Console.Error.WriteLineAsync("OK sent");
    return 0;
}
