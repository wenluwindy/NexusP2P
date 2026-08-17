using System.Collections.Concurrent;
using NexusP2P.Core.Crypto;
using NexusP2P.Core.Manifest;
using NexusP2P.Transfer.Protocol;

namespace NexusP2P.Transfer;

/// <summary>一条扇出链路的状态。</summary>
public enum FanOutLinkState
{
    /// <summary>正在传输。</summary>
    Running,

    /// <summary>对端确认收齐并通过整体校验。</summary>
    Completed,

    /// <summary>失败（含重连超限转手动）。不影响其他链路。</summary>
    Failed,
}

/// <summary>一条扇出链路的进度快照。</summary>
public sealed record FanOutLinkSnapshot(
    string PeerId,
    FanOutLinkState State,
    TransferProgress Progress,
    Exception? Error);

/// <summary>
/// 一对多发送的编排器（AD-11）：每个接收方一条独立链路 =
/// 独立 ProtocolConnection + 独立 <see cref="SendSession"/>。
///
/// <para>链路之间唯一共享的是清单与 <see cref="ICipherPieceProvider"/>
/// （AD-13：加密一次发 N 次）。<b>一条链路的任何失败不影响其他链路</b> ——
/// V1 的轮次制防死锁、收敛保证、续传全部逐链路独立成立。</para>
///
/// <para>这个类<b>不管理连接的建立</b>：谁进房、怎么建 PeerConnection
/// 是宿主（Agent / CLI）的事，它只负责「拿到一条连接就开一条链路」。
/// 这让它能在内存管道上被完整测试（AD-1）。</para>
/// </summary>
public sealed class SendFanOut : IDisposable
{
    private readonly TransferManifest _manifest;
    private readonly TransferSecret _secret;
    private readonly ICipherPieceProvider _cipherProvider;
    private readonly ConcurrentDictionary<string, LinkRun> _links = [];
    private bool _disposed;

    /// <summary>一条链路的可变状态。快照的发布是易失写，读方拿到的是完整对象。</summary>
    private sealed class LinkRun(string peerId)
    {
        private volatile FanOutLinkSnapshot _latest =
            new(peerId, FanOutLinkState.Running, default, null);

        public FanOutLinkSnapshot Latest
        {
            get => _latest;
            set => _latest = value;
        }

        public Task Task { get; set; } = Task.CompletedTask;
    }

    public SendFanOut(TransferManifest manifest, TransferSecret secret, ICipherPieceProvider cipherProvider)
    {
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _cipherProvider = cipherProvider ?? throw new ArgumentNullException(nameof(cipherProvider));
        _secret = secret;
    }

    /// <summary>当前所有链路的快照。</summary>
    public IReadOnlyList<FanOutLinkSnapshot> Links => [.. _links.Values.Select(l => l.Latest)];

    /// <summary>
    /// 为一个接收方开一条链路并跑到结束。返回的任务在链路完成/失败时结束；
    /// <b>失败不抛出</b> —— 结果进快照，一条链路的失败不该炸掉编排方的 await。
    /// </summary>
    /// <param name="peerId">链路标识（信令分配的 peerId；内存测试里随便给）。</param>
    /// <param name="connection">这条链路的协议连接。链路结束时由调用方释放。</param>
    /// <param name="progress">这条链路自己的进度回调（在后台线程触发）。</param>
    public Task RunLinkAsync(
        string peerId,
        ProtocolConnection connection,
        IProgress<FanOutLinkSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(peerId);
        ArgumentNullException.ThrowIfNull(connection);

        var run = new LinkRun(peerId);

        // 同一 peerId 不允许两条并存 —— 重连拿的是新 peerId（AD-16）
        if (!_links.TryAdd(peerId, run))
        {
            throw new InvalidOperationException(
                $"peerId \"{peerId}\" 已有一条活动链路。重连应当使用新的 peerId（AD-16）。");
        }

        var task = Task.Run(
            () => RunLinkCoreAsync(run, peerId, connection, progress, cancellationToken),
            CancellationToken.None);

        run.Task = task;
        return task;
    }

    private async Task RunLinkCoreAsync(
        LinkRun run,
        string peerId,
        ProtocolConnection connection,
        IProgress<FanOutLinkSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(run.Latest);

        var session = new SendSession(_manifest, _secret, _cipherProvider);

        try
        {
            // 不用 Progress<T>（它经由 SynchronizationContext 投递，测试里会丢尾）；
            // 直接同步发布快照 —— 事件在后台线程触发是既有约定（宿主自己切线程）。
            var relay = new SynchronousProgress<TransferProgress>(p =>
            {
                var running = new FanOutLinkSnapshot(peerId, FanOutLinkState.Running, p, null);
                run.Latest = running;
                progress?.Report(running);
            });

            await session.RunAsync(connection, relay, cancellationToken).ConfigureAwait(false);

            var completed = run.Latest with { State = FanOutLinkState.Completed };
            run.Latest = completed;
            progress?.Report(completed);
        }
        catch (Exception ex)
        {
            // 失败进快照不抛出：一条链路失败不影响其他链路（AD-11）
            var failed = new FanOutLinkSnapshot(
                peerId, FanOutLinkState.Failed, run.Latest.Progress, ex);
            run.Latest = failed;
            progress?.Report(failed);
        }
    }

    /// <summary>拆掉一条链路的记录（peer-left 之后）。正在跑的任务自然结束。</summary>
    public void ForgetLink(string peerId) => _links.TryRemove(peerId, out _);

    /// <summary>等所有已开的链路结束（不抛出 —— 每条链路的结果看快照）。</summary>
    public Task WhenAllLinksSettledAsync() =>
        Task.WhenAll(_links.Values.Select(l => l.Task));

    public void Dispose() => _disposed = true;

    /// <summary>同步投递的 IProgress：不经过 SynchronizationContext，不丢尾、不乱序。</summary>
    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
