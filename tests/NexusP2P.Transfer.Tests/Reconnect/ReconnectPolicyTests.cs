using NexusP2P.Core.Hashing;
using NexusP2P.Core.Manifest;
using NexusP2P.Transfer;
using NexusP2P.Transfer.Protocol;
using NexusP2P.Transfer.Reconnect;
using NexusP2P.Transfer.Storage;
using NexusP2P.Transport.Abstractions;

namespace NexusP2P.Transfer.Tests.Reconnect;

public sealed class ReconnectPolicyTests
{
    [Fact]
    public void 默认自动重试三次()
    {
        // AD-7 定的行为，钉住它
        Assert.Equal(3, ReconnectPolicy.Default.MaxAttempts);
    }

    [Fact]
    public void 退避是指数增长的()
    {
        var policy = new ReconnectPolicy
        {
            InitialDelay = TimeSpan.FromSeconds(1),
            BackoffFactor = 2.0,
        };

        Assert.Equal(TimeSpan.FromSeconds(1), policy.DelayBefore(1));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.DelayBefore(2));
        Assert.Equal(TimeSpan.FromSeconds(4), policy.DelayBefore(3));
    }

    [Fact]
    public void 退避有上限()
    {
        var policy = new ReconnectPolicy
        {
            InitialDelay = TimeSpan.FromSeconds(1),
            BackoffFactor = 10.0,
            MaxDelay = TimeSpan.FromSeconds(5),
        };

        Assert.Equal(TimeSpan.FromSeconds(1), policy.DelayBefore(1));
        Assert.Equal(TimeSpan.FromSeconds(5), policy.DelayBefore(2));
        Assert.Equal(TimeSpan.FromSeconds(5), policy.DelayBefore(9));
    }

    [Fact]
    public void 重试次数从一开始()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReconnectPolicy.Default.DelayBefore(0));
    }

    // ---- 可重试性判定：最容易做错的地方 ----

    [Fact]
    public void 传输层断开可以重试()
    {
        Assert.True(ReconnectPolicy.IsRetryable(new DataChannelClosedException("网线被拔了")));
        Assert.True(ReconnectPolicy.IsRetryable(new ProtocolException("帧不完整")));
        Assert.True(ReconnectPolicy.IsRetryable(new IOException("socket 抖了一下")));
    }

    [Theory]
    [InlineData(TransferErrorCode.InvalidManifest)]
    [InlineData(TransferErrorCode.InsufficientDiskSpace)]
    [InlineData(TransferErrorCode.DestinationNotWritable)]
    [InlineData(TransferErrorCode.ProtocolViolation)]
    [InlineData(TransferErrorCode.PieceVerificationFailed)]
    [InlineData(TransferErrorCode.Cancelled)]
    public void 内容或环境问题不该重试(TransferErrorCode code)
    {
        // 「文件码不对」重试三次只是白等七秒再报同一个错，
        // 反而让用户以为是网络问题
        Assert.False(ReconnectPolicy.IsRetryable(new TransferFailedException(code, "x")));
    }

    [Fact]
    public void 各类本地错误不该重试()
    {
        Assert.False(ReconnectPolicy.IsRetryable(new UnsafePathException("../x", "穿越")));
        Assert.False(ReconnectPolicy.IsRetryable(new InvalidManifestException("坏了")));
        Assert.False(ReconnectPolicy.IsRetryable(new InsufficientDiskSpaceException("C:\\", 100, 1)));
        Assert.False(ReconnectPolicy.IsRetryable(
            new IntegrityException("a.bin", Hash256.Zero, Hash256.Zero)));
    }

    [Fact]
    public void 取消不算失败也不重试()
    {
        Assert.False(ReconnectPolicy.IsRetryable(new OperationCanceledException()));
        Assert.False(ReconnectPolicy.IsRetryable(new TaskCanceledException()));
    }

    [Fact]
    public void 不认识的异常不重试()
    {
        // 盲目重连一个未知故障只会把真正的原因埋在三次重试之后
        Assert.False(ReconnectPolicy.IsRetryable(new NotSupportedException()));
        Assert.False(ReconnectPolicy.IsRetryable(new FormatException()));
        Assert.False(ReconnectPolicy.IsRetryable(new ArgumentOutOfRangeException("x")));
    }

    // ---- 重试循环 ----

    private static readonly ReconnectPolicy Fast = new()
    {
        MaxAttempts = 3,
        InitialDelay = TimeSpan.FromMilliseconds(5),
        BackoffFactor = 1.0,
    };

    private static Task<ProtocolConnection> ConnectAsync(CancellationToken _)
    {
        var pair = InMemoryDataChannelPair.Create();
        return Task.FromResult(new ProtocolConnection(pair.Left));
    }

    [Fact]
    public async Task 首次就成功时不重连()
    {
        var attempts = 0;

        var result = await ResilientSession.RunAsync(
            ConnectAsync,
            (_, _) =>
            {
                attempts++;
                return Task.FromResult("ok");
            },
            Fast);

        Assert.Equal("ok", result);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task 断一次后自动恢复()
    {
        var attempts = 0;

        var result = await ResilientSession.RunAsync(
            ConnectAsync,
            (_, _) =>
            {
                if (++attempts == 1)
                {
                    throw new DataChannelClosedException("第一次断了");
                }

                return Task.FromResult("ok");
            },
            Fast);

        Assert.Equal("ok", result);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task 断三次后第四次成功()
    {
        // 3 次重试 = 总共 4 次尝试
        var attempts = 0;

        var result = await ResilientSession.RunAsync(
            ConnectAsync,
            (_, _) =>
            {
                if (++attempts <= 3)
                {
                    throw new DataChannelClosedException($"第 {attempts} 次断了");
                }

                return Task.FromResult("ok");
            },
            Fast);

        Assert.Equal("ok", result);
        Assert.Equal(4, attempts);
    }

    [Fact]
    public async Task 断四次后转手动()
    {
        var attempts = 0;

        var exhausted = await Assert.ThrowsAsync<ReconnectExhaustedException>(
            () => ResilientSession.RunAsync<string>(
                ConnectAsync,
                (_, _) =>
                {
                    attempts++;
                    throw new DataChannelClosedException($"第 {attempts} 次断了");
                },
                Fast));

        Assert.Equal(4, attempts);   // 首次 + 3 次重试
        Assert.Equal(3, exhausted.Attempts);
        Assert.IsType<DataChannelClosedException>(exhausted.LastFailure);
        Assert.Contains("第 4 次断了", exhausted.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 不可重试的失败立刻抛出且不重连()
    {
        var attempts = 0;

        var failure = await Assert.ThrowsAsync<TransferFailedException>(
            () => ResilientSession.RunAsync<string>(
                ConnectAsync,
                (_, _) =>
                {
                    attempts++;
                    throw new TransferFailedException(
                        TransferErrorCode.InvalidManifest, "文件码不匹配");
                },
                Fast));

        Assert.Equal(1, attempts);
        Assert.Equal(TransferErrorCode.InvalidManifest, failure.Code);
    }

    [Fact]
    public async Task 取消时立刻停止()
    {
        using var cts = new CancellationTokenSource();
        var attempts = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ResilientSession.RunAsync<string>(
                ConnectAsync,
                async (_, ct) =>
                {
                    attempts++;
                    await cts.CancelAsync();
                    ct.ThrowIfCancellationRequested();
                    return "unreachable";
                },
                Fast,
                cancellationToken: cts.Token));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task 重连状态对上层可见()
    {
        // 自动重试必须是可见的，否则 3 次重试会把「网络确实不通」
        // 静默推迟好几秒，用户只看到一个卡住的进度条
        var reported = new List<ReconnectStatus>();
        var gate = new Lock();
        var status = new Progress<ReconnectStatus>(s =>
        {
            lock (gate)
            {
                reported.Add(s);
            }
        });

        var attempts = 0;

        await Assert.ThrowsAsync<ReconnectExhaustedException>(
            () => ResilientSession.RunAsync<string>(
                ConnectAsync,
                (_, _) =>
                {
                    attempts++;
                    throw new DataChannelClosedException("断了");
                },
                Fast,
                status));

        await Task.Delay(150);

        ReconnectStatus[] snapshot;
        lock (gate)
        {
            snapshot = [.. reported];
        }

        Assert.Contains(snapshot, s => s.Phase == ReconnectPhase.Connecting);
        Assert.Contains(snapshot, s => s.Phase == ReconnectPhase.Running);
        Assert.Contains(snapshot, s => s.Phase == ReconnectPhase.WaitingBeforeRetry);
        Assert.Contains(snapshot, s => s.Phase == ReconnectPhase.GaveUp);

        // 「正在重连 2/3」这样的显示需要 Attempt 与 MaxAttempts 都对
        var waiting = snapshot.Where(s => s.Phase == ReconnectPhase.WaitingBeforeRetry).ToArray();
        Assert.Equal(3, waiting.Length);
        Assert.Equal([1, 2, 3], waiting.Select(s => s.Attempt).ToArray());
        Assert.All(waiting, s => Assert.Equal(3, s.MaxAttempts));
        // Reason 要带上真实原因，UI 才能显示「正在重连 2/3 — 数据通道已关闭」
        Assert.All(waiting, s => Assert.Contains("断了", s.Reason!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task 每次尝试都用一条新连接()
    {
        // AD-7：重连不做 ICE restart 也不重协商，直接建新连接
        var connections = new List<ProtocolConnection>();
        var attempts = 0;

        await Assert.ThrowsAsync<ReconnectExhaustedException>(
            () => ResilientSession.RunAsync<string>(
                ct =>
                {
                    var pair = InMemoryDataChannelPair.Create();
                    var connection = new ProtocolConnection(pair.Left);
                    connections.Add(connection);
                    return Task.FromResult(connection);
                },
                (_, _) =>
                {
                    attempts++;
                    throw new DataChannelClosedException("断了");
                },
                Fast));

        Assert.Equal(4, connections.Count);
        Assert.Equal(4, connections.Distinct().Count());
    }

    [Fact]
    public async Task 建连接本身失败也会重试()
    {
        var connectAttempts = 0;

        await Assert.ThrowsAsync<ReconnectExhaustedException>(
            () => ResilientSession.RunAsync<string>(
                _ =>
                {
                    connectAttempts++;
                    throw new IOException("连不上信令服务器");
                },
                (_, _) => Task.FromResult("unreachable"),
                Fast));

        Assert.Equal(4, connectAttempts);
    }
}
