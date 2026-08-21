using NexusP2P.Transfer;
using NexusP2P.Transfer.Protocol;
using NexusP2P.Transfer.Reconnect;
using NexusP2P.Transport.Abstractions;

namespace NexusP2P.Integration.Tests;

/// <summary>
/// 自动重连的端到端验收（AD-7）。
///
/// <para>每次重试都建一条<b>全新的连接</b>，续传靠接收端的位图 ——
/// 所以「维持连接」没有价值，也就不需要 ICE restart 或 SDP 重协商。</para>
/// </summary>
public sealed class ReconnectEndToEndTests
{
    private static readonly ReconnectPolicy Fast = new()
    {
        MaxAttempts = 3,
        InitialDelay = TimeSpan.FromMilliseconds(10),
        BackoffFactor = 1.0,
    };

    /// <summary>
    /// 跑一次带自动重连的接收。<paramref name="faultsPerAttempt"/> 给出每次尝试
    /// 注入的故障，用完之后的尝试不注入故障（即会成功）。
    /// </summary>
    private static async Task<ReceiveResult> RunWithReconnectAsync(
        TransferHarness harness,
        string destination,
        FaultProfile?[] faultsPerAttempt,
        IProgress<ReconnectStatus>? status = null,
        ReconnectPolicy? policy = null)
    {
        var manifest = await harness.BuildManifestAsync();
        var attempt = 0;
        var senderTasks = new List<Task>();

        var result = await ResilientSession.RunAsync(
            connect: _ =>
            {
                var faults = attempt < faultsPerAttempt.Length ? faultsPerAttempt[attempt] : null;
                attempt++;

                // 每次尝试一条全新的管道 —— 对应真实场景里的新 PeerConnection
                var pair = InMemoryDataChannelPair.Create(faults);
                var senderConnection = new ProtocolConnection(pair.Left);
                var receiverConnection = new ProtocolConnection(pair.Right);

                // 发送端也随每次重连重新起一个会话
                senderTasks.Add(Task.Run(async () =>
                {
                    await using var source = new MemoryPieceSource(manifest, harness.Files);
                    var sender = new SendSession(manifest, source, harness.Secret);
                    try
                    {
                        await sender.RunAsync(senderConnection);
                    }
                    catch
                    {
                        // 断线时发送端也会失败；由接收端那侧的重连驱动整体流程
                    }
                    finally
                    {
                        await senderConnection.DisposeAsync();
                    }
                }, CancellationToken.None));

                return Task.FromResult(receiverConnection);
            },
            session: (connection, ct) =>
                new ReceiveSession(destination)
                    .RunAsync(connection, progress: null, rescanProgress: null, cancellationToken: ct),
            policy: policy ?? Fast,
            status: status);

        await Task.WhenAll(senderTasks);
        return result;
    }

    [Fact]
    public async Task 断一次后自动恢复且最终一致()
    {
        using var harness = new TransferHarness().With("a.bin", 150_000);
        var destination = harness.CreateTemporaryDirectory();

        var result = await RunWithReconnectAsync(
            harness, destination, [FaultProfile.DisconnectAfter(50 * 1024)]);

        Assert.NotNull(result);
        await harness.AssertLandedAsync(destination);
    }

    [Fact]
    public async Task 断三次后仍能自动完成()
    {
        using var harness = new TransferHarness().With("a.bin", 300_000);
        var destination = harness.CreateTemporaryDirectory();

        var result = await RunWithReconnectAsync(
            harness,
            destination,
            [
                FaultProfile.DisconnectAfter(40 * 1024),
                FaultProfile.DisconnectAfter(40 * 1024),
                FaultProfile.DisconnectAfter(40 * 1024),
            ]);

        Assert.NotNull(result);
        await harness.AssertLandedAsync(destination);
    }

    [Fact]
    public async Task 断四次后转手动并保留已有进度()
    {
        using var harness = new TransferHarness().With("a.bin", 400_000);
        var destination = harness.CreateTemporaryDirectory();

        var alwaysDisconnect = Enumerable.Repeat<FaultProfile?>(
            FaultProfile.DisconnectAfter(30 * 1024), 10).ToArray();

        await Assert.ThrowsAsync<ReconnectExhaustedException>(
            () => RunWithReconnectAsync(harness, destination, alwaysDisconnect));

        // 转手动之后，已经收下的部分必须还在 —— 否则用户手动重连要从头开始
        var manifest = await harness.BuildManifestAsync();
        await using var store = await NexusP2P.Transfer.Storage.PieceStore
            .OpenAsync(destination, manifest);

        Assert.True(store.Bitfield.SetCount > 0, "转手动后应保留已有进度");
        Assert.False(store.Bitfield.IsComplete);
    }

    [Fact]
    public async Task 转手动后用户手动重连能接着传完()
    {
        using var harness = new TransferHarness().With("a.bin", 400_000);
        var destination = harness.CreateTemporaryDirectory();

        var alwaysDisconnect = Enumerable.Repeat<FaultProfile?>(
            FaultProfile.DisconnectAfter(30 * 1024), 10).ToArray();

        await Assert.ThrowsAsync<ReconnectExhaustedException>(
            () => RunWithReconnectAsync(harness, destination, alwaysDisconnect));

        // 用户点「重连」：等价于重新发起一次不带故障的传输
        var resumed = await harness.RunAsync(destination);

        Assert.True(resumed.Completed, $"{resumed.SenderError}；{resumed.ReceiverError}");
        await harness.AssertLandedAsync(destination);
    }

    [Fact]
    public async Task 清单解不开时不重连而是立刻报错()
    {
        // 这是最重要的一条：重试「清单解不开」只是白等几秒再报同一个错，
        // 还会让用户误以为是网络问题
        using var harness = new TransferHarness().With("a.bin", 10_000);
        var destination = harness.CreateTemporaryDirectory();
        var manifest = await harness.BuildManifestAsync();

        var connectCount = 0;

        var failure = await Assert.ThrowsAsync<TransferFailedException>(
            () => ResilientSession.RunAsync(
                // 参数不能叫 _：那样下面的 `_ = Task.Run(...)` 会被解析成
                // 给这个 CancellationToken 参数赋值，而不是丢弃
                connect: unusedToken =>
                {
                    connectCount++;
                    var pair = InMemoryDataChannelPair.Create();
                    var senderConnection = new ProtocolConnection(pair.Left);

                    _ = unusedToken;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // 推一把密钥，却用另一把密封清单 —— 接收方解不开
                            await senderConnection.SendAsync(
                                MessageType.KeyOffer,
                                new KeyOfferPayload(
                                    NexusP2P.Core.Crypto.TransferSecret.Generate()).Serialize());

                            var manifestKey =
                                NexusP2P.Core.Crypto.KeyDerivation.DeriveManifestKey(harness.Secret);
                            await senderConnection.SendAsync(
                                MessageType.Manifest,
                                NexusP2P.Core.Crypto.BlobCipher.Seal(manifestKey, manifest.Serialize()));
                        }
                        catch
                        {
                            // 对端会报错并关闭
                        }
                    }, CancellationToken.None);

                    return Task.FromResult(new ProtocolConnection(pair.Right));
                },
                session: (connection, ct) =>
                    new ReceiveSession(destination)
                        .RunAsync(connection, progress: null, rescanProgress: null, cancellationToken: ct),
                policy: Fast));

        Assert.Equal(TransferErrorCode.InvalidManifest, failure.Code);
        Assert.Equal(1, connectCount);
    }

    [Fact]
    public async Task 重连过程对上层可见()
    {
        using var harness = new TransferHarness().With("a.bin", 150_000);
        var destination = harness.CreateTemporaryDirectory();

        var phases = new List<ReconnectPhase>();
        var gate = new Lock();
        var status = new Progress<ReconnectStatus>(s =>
        {
            lock (gate)
            {
                phases.Add(s.Phase);
            }
        });

        await RunWithReconnectAsync(
            harness, destination, [FaultProfile.DisconnectAfter(50 * 1024)], status);

        await Task.Delay(150);

        ReconnectPhase[] snapshot;
        lock (gate)
        {
            snapshot = [.. phases];
        }

        Assert.Contains(ReconnectPhase.WaitingBeforeRetry, snapshot);
        Assert.DoesNotContain(ReconnectPhase.GaveUp, snapshot);
    }
}
