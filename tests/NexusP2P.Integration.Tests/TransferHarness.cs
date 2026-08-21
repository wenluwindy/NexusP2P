using NexusP2P.Core.Crypto;
using NexusP2P.Core.Hashing;
using NexusP2P.Core.Manifest;
using NexusP2P.Transfer;
using NexusP2P.Transfer.Protocol;
using NexusP2P.Transport.Abstractions;

namespace NexusP2P.Integration.Tests;

/// <summary>一次内存管道传输的结果。</summary>
internal sealed record HarnessRun(
    bool Completed,
    Exception? SenderError,
    Exception? ReceiverError,
    ReceiveResult? Result,
    int PiecesSent);

/// <summary>
/// 在内存管道上跑完整的收发流程。
///
/// <para>这是 AD-1 的兑现之处：分片、加密、校验、落盘、续传全部在这里被验证，
/// 而完全不涉及 WebRTC、信令、网络。真实传输只是把
/// <see cref="InMemoryDataChannelPair"/> 换成 WebRTC 实现。</para>
/// </summary>
internal sealed class TransferHarness : IDisposable
{
    public static readonly MerkleParameters SmallParameters = new(1024, 4096);

    private readonly List<string> _temporaryDirectories = [];

    public MerkleParameters Parameters { get; init; } = SmallParameters;

    public Dictionary<string, byte[]> Files { get; } = [];

    public List<string> EmptyDirectories { get; } = [];

    public TransferSecret Secret { get; init; } = TransferSecret.Generate();

    public static byte[] Content(int length, int seed = 0)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)((((i + 1) * (seed + 37)) ^ (i >> 5)) & 0xFF);
        }

        return bytes;
    }

    public TransferHarness With(string path, byte[] content)
    {
        Files[path] = content;
        return this;
    }

    public TransferHarness With(string path, int length, int seed = 0) => With(path, Content(length, seed));

    public TransferHarness WithEmptyDirectory(string path)
    {
        EmptyDirectories.Add(path);
        return this;
    }

    public async Task<TransferManifest> BuildManifestAsync()
    {
        var entries = new List<ManifestEntry>(Files.Count);

        foreach (var (path, content) in Files)
        {
            using var hasher = new FileHasher(Parameters);
            using var stream = new MemoryStream(content, writable: false);
            entries.Add(ManifestEntry.FromHashResult(path, await hasher.ComputeAsync(stream)));
        }

        return TransferManifest.Create(Parameters, entries, EmptyDirectories);
    }

    public string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "nexusp2p-itest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _temporaryDirectories.Add(path);
        return path;
    }

    /// <summary>
    /// 跑一次传输。<paramref name="destinationRoot"/> 复用同一个目录即可测续传。
    ///
    /// <para>两端都不抛异常出来 —— 失败信息放进 <see cref="HarnessRun"/>，
    /// 这样测试可以对「谁失败了、为什么」做断言，而不是只看到一个
    /// AggregateException。</para>
    /// </summary>
    public async Task<HarnessRun> RunAsync(
        string destinationRoot,
        FaultProfile? faults = null,
        TransferManifest? manifest = null,
        int maxMessageSize = 64 * 1024,
        CancellationToken cancellationToken = default)
    {
        manifest ??= await BuildManifestAsync();

        await using var pair = InMemoryDataChannelPair.Create(faults, maxMessageSize);
        await using var senderConnection = new ProtocolConnection(pair.Left);
        await using var receiverConnection = new ProtocolConnection(pair.Right);

        await using var source = new MemoryPieceSource(manifest, Files);
        var sender = new SendSession(manifest, source, Secret);
        var receiver = new ReceiveSession(destinationRoot);

        Exception? senderError = null;
        Exception? receiverError = null;
        ReceiveResult? result = null;

        var senderTask = Task.Run(async () =>
        {
            try
            {
                await sender.RunAsync(senderConnection, cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                senderError = ex;
            }
        }, CancellationToken.None);

        var receiverTask = Task.Run(async () =>
        {
            try
            {
                result = await receiver.RunAsync(receiverConnection, cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                receiverError = ex;
            }
        }, CancellationToken.None);

        // 硬超时：协议死锁的表现是两边各等各的，测试主机永久空转，
        // vstest 只会报「主机崩溃」而不给任何线索。把它变成一条能定位的失败。
        var both = Task.WhenAll(senderTask, receiverTask);
        // 刻意不传 cancellationToken：这个超时是用来兜住死锁的，
        // 不该被调用方的令牌取消掉
        var finished = await Task.WhenAny(
            both, Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None));

        if (finished != both)
        {
            Assert.Fail(
                "传输在 30 秒内没有结束，疑似协议死锁。\n" +
                $"  发送端已完成 = {senderTask.IsCompleted}\n" +
                $"  接收端已完成 = {receiverTask.IsCompleted}\n" +
                $"  发送端已投递分片 = {sender.PiecesSent}\n" +
                $"  发送端错误 = {senderError?.Message ?? "无"}\n" +
                $"  接收端错误 = {receiverError?.Message ?? "无"}");
        }

        return new HarnessRun(
            senderError is null && receiverError is null,
            senderError,
            receiverError,
            result,
            sender.PiecesSent);
    }

    /// <summary>断言目标目录里的内容与源完全一致。</summary>
    public async Task AssertLandedAsync(string destinationRoot)
    {
        foreach (var (path, expected) in Files)
        {
            var landed = Path.Combine(destinationRoot, path.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(landed), $"{path} 没有落地");
            Assert.Equal(expected, await File.ReadAllBytesAsync(landed));
        }

        foreach (var directory in EmptyDirectories)
        {
            var landed = Path.Combine(destinationRoot, directory.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(Directory.Exists(landed), $"空目录 {directory} 没有创建");
        }
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
                // 清理失败不该让测试失败
            }
        }
    }
}
