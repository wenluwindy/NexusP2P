using NexusP2P.Core.Crypto;
using NexusP2P.Transfer;
using NexusP2P.Transfer.Protocol;
using NexusP2P.Transfer.Storage;
using NexusP2P.Transport.WebRtc;

namespace NexusP2P.Integration.Tests.WebRtc;

/// <summary>
/// <b>Task 3.2 的核心验收</b>：把在内存管道上证明过的协议，
/// 原封不动地跑在真实 WebRTC 上。
///
/// <para>这是 AD-1 的兑现方式 —— 传输层被换成真的 DTLS + SCTP + ICE，
/// 而分片、加密、校验、落盘、续传、重传那一整套代码<b>一行都没改</b>。
/// 若这些测试通过，说明抽象边界画对了。</para>
///
/// <para>这些用例刻意与 <c>InMemoryEndToEndTests</c> 一一对应，
/// 方便对照「内存能过、真网也能过」。</para>
/// </summary>
public sealed class WebRtcEndToEndTests
{
    /// <summary>真实 WebRTC 上跑一次完整传输。</summary>
    private static async Task<(Exception? SenderError, Exception? ReceiverError, ReceiveResult? Result, int PiecesSent)>
        RunAsync(TransferHarness harness, string destination, TransferManifestOverride? manifestOverride = null)
    {
        var manifest = manifestOverride?.Manifest ?? await harness.BuildManifestAsync();

        await using var pair = await LoopbackPeerPair.ConnectAsync();
        await using var senderConnection = new ProtocolConnection(pair.Offerer);
        await using var receiverConnection = new ProtocolConnection(pair.Answerer);
        await using var source = new MemoryPieceSource(manifest, harness.Files);

        var sender = new SendSession(manifest, source, harness.Secret);
        var receiver = new ReceiveSession(destination);

        Exception? senderError = null;
        Exception? receiverError = null;
        ReceiveResult? result = null;

        var senderTask = Task.Run(async () =>
        {
            try
            {
                await sender.RunAsync(senderConnection);
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
                result = await receiver.RunAsync(receiverConnection);
            }
            catch (Exception ex)
            {
                receiverError = ex;
            }
        }, CancellationToken.None);

        var both = Task.WhenAll(senderTask, receiverTask);
        var finished = await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(120), CancellationToken.None));

        if (finished != both)
        {
            Assert.Fail(
                "真实 WebRTC 上的传输在 120 秒内没有结束，疑似死锁。\n" +
                $"  发送端已完成 = {senderTask.IsCompleted}\n" +
                $"  接收端已完成 = {receiverTask.IsCompleted}\n" +
                $"  发送端已投递分片 = {sender.PiecesSent}");
        }

        return (senderError, receiverError, result, sender.PiecesSent);
    }

    internal sealed record TransferManifestOverride(Core.Manifest.TransferManifest Manifest);

    [Fact]
    public async Task 单文件在真实_WebRTC_上端到端一致()
    {
        using var harness = new TransferHarness().With("a.bin", 200_000);
        var destination = harness.CreateTemporaryDirectory();

        var (senderError, receiverError, result, _) = await RunAsync(harness, destination);

        Assert.Null(senderError);
        Assert.Null(receiverError);
        Assert.NotNull(result);
        await harness.AssertLandedAsync(destination);
    }

    [Fact]
    public async Task 文件夹含嵌套目录空文件与空目录()
    {
        using var harness = new TransferHarness()
            .With("proj/readme.md", 500)
            .With("proj/src/deep/nested/a.bin", 120_000)
            .With("proj/src/b.bin", 1)
            .With("proj/empty.dat", 0)
            .With("proj/我的文件.txt", 2000)
            .WithEmptyDirectory("proj/logs")
            .WithEmptyDirectory("proj/tmp/cache");

        var destination = harness.CreateTemporaryDirectory();

        var (senderError, receiverError, result, _) = await RunAsync(harness, destination);

        Assert.Null(senderError);
        Assert.Null(receiverError);
        await harness.AssertLandedAsync(destination);
        Assert.Equal(5, result!.LandedFiles.Count);
    }

    [Fact]
    public async Task 内容完全相同的多个文件()
    {
        // 曾经会撞名丢数据的那个 bug，在真实传输上也要不复现
        var identical = TransferHarness.Content(80_000, seed: 42);

        using var harness = new TransferHarness()
            .With("dup/a.bin", identical)
            .With("dup/b.bin", identical)
            .With("dup/empty1.dat", [])
            .With("dup/empty2.dat", []);

        var destination = harness.CreateTemporaryDirectory();

        var (senderError, receiverError, _, _) = await RunAsync(harness, destination);

        Assert.Null(senderError);
        Assert.Null(receiverError);
        await harness.AssertLandedAsync(destination);
    }

    [Fact]
    public async Task 真实分片参数下的多_MiB_内容()
    {
        // 默认参数：64 KiB 叶子 / 1 MiB 分片。一个分片要切成多条 64 KiB 消息，
        // 这条路径在真实 SCTP 上必须走通
        using var harness = new TransferHarness
        {
            Parameters = Core.Hashing.MerkleParameters.Default,
        }.With("big.bin", 8 * 1024 * 1024);

        var destination = harness.CreateTemporaryDirectory();

        var (senderError, receiverError, _, _) = await RunAsync(harness, destination);

        Assert.Null(senderError);
        Assert.Null(receiverError);
        await harness.AssertLandedAsync(destination);
    }

    [Fact]
    public async Task 断点续传在真实_WebRTC_上成立()
    {
        // 第一次只传一部分：用一个小到装不下全部内容的目标来制造中断很麻烦，
        // 所以改成先用内存管道传一半（进度落到磁盘），再用真实 WebRTC 续完。
        // 这正好验证了「.part 与传输层无关」—— 续传的锚点是内容而非连接。
        using var harness = new TransferHarness().With("a.bin", 400_000);
        var destination = harness.CreateTemporaryDirectory();

        await harness.RunAsync(
            destination, Transport.Abstractions.FaultProfile.DisconnectAfter(80 * 1024));

        var manifest = await harness.BuildManifestAsync();
        int alreadyDone;
        await using (var store = await PieceStore.OpenAsync(destination, manifest))
        {
            alreadyDone = store.Bitfield.SetCount;
        }

        Assert.True(alreadyDone > 0, "第一次应该已经落下一部分进度");
        Assert.True(alreadyDone < manifest.TotalPieces, "第一次不该已经传完");

        // 换成真实 WebRTC 续传
        var (senderError, receiverError, _, piecesSent) = await RunAsync(harness, destination);

        Assert.Null(senderError);
        Assert.Null(receiverError);
        await harness.AssertLandedAsync(destination);

        Assert.True(piecesSent < manifest.TotalPieces,
            $"续传发了 {piecesSent} 个分片，总共 {manifest.TotalPieces} 个 —— 进度没被利用");
    }

    [Fact]
    public async Task 密钥要约与清单不匹配时在真实_WebRTC_上也明确报错()
    {
        using var harness = new TransferHarness().With("a.bin", 20_000);
        var destination = harness.CreateTemporaryDirectory();
        var manifest = await harness.BuildManifestAsync();

        await using var pair = await LoopbackPeerPair.ConnectAsync();
        await using var senderConnection = new ProtocolConnection(pair.Offerer);
        await using var receiverConnection = new ProtocolConnection(pair.Answerer);

        var senderTask = Task.Run(async () =>
        {
            try
            {
                // 推一把密钥，却用另一把密封清单
                await senderConnection.SendAsync(
                    MessageType.KeyOffer,
                    new KeyOfferPayload(TransferSecret.Generate()).Serialize());

                var manifestKey = NexusP2P.Core.Crypto.KeyDerivation.DeriveManifestKey(harness.Secret);
                await senderConnection.SendAsync(
                    MessageType.Manifest,
                    NexusP2P.Core.Crypto.BlobCipher.Seal(manifestKey, manifest.Serialize()));
            }
            catch
            {
                // 对端会报错并关闭
            }
        }, CancellationToken.None);

        var failure = await Assert.ThrowsAsync<TransferFailedException>(
            () => new ReceiveSession(destination).RunAsync(receiverConnection));

        Assert.Equal(TransferErrorCode.InvalidManifest, failure.Code);
        await senderTask;
    }

    [Fact]
    public async Task 明文不会以原样出现在真实链路上()
    {
        // 端到端加密在真实传输上同样要成立。这里抓的是交给 SCTP 之前的帧，
        // 而 SCTP 之外还有 DTLS 一层 —— 所以实际线上比这更严。
        using var harness = new TransferHarness();
        var marker = System.Text.Encoding.UTF8.GetBytes("SECRET-MARKER-DO-NOT-LEAK-0123456789");
        var content = new byte[60_000];
        for (var offset = 0; offset + marker.Length <= content.Length; offset += 4096)
        {
            marker.CopyTo(content.AsSpan(offset));
        }

        harness.With("secret.txt", content);
        var manifest = await harness.BuildManifestAsync();
        var destination = harness.CreateTemporaryDirectory();

        var captured = new List<byte[]>();
        var gate = new Lock();

        await using var pair = await LoopbackPeerPair.ConnectAsync();
        pair.Answerer.MessageReceived += frame =>
        {
            lock (gate)
            {
                captured.Add(frame.ToArray());
            }
        };

        await using var senderConnection = new ProtocolConnection(pair.Offerer);
        await using var receiverConnection = new ProtocolConnection(pair.Answerer);
        await using var source = new MemoryPieceSource(manifest, harness.Files);

        await Task.WhenAll(
            Task.Run(() => new SendSession(manifest, source, harness.Secret).RunAsync(senderConnection)),
            Task.Run(() => new ReceiveSession(destination).RunAsync(receiverConnection)));

        byte[][] frames;
        lock (gate)
        {
            frames = [.. captured];
        }

        Assert.NotEmpty(frames);

        var nameBytes = System.Text.Encoding.UTF8.GetBytes("secret.txt");
        foreach (var frame in frames)
        {
            Assert.False(Contains(frame, marker), "真实链路上出现了明文标记");
            Assert.False(Contains(frame, nameBytes), "真实链路上出现了明文文件名");
        }
    }

    private static bool Contains(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.IsEmpty || haystack.Length < needle.Length)
        {
            return false;
        }

        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }
}
