using NexusP2P.Agent.Settings;
using NexusP2P.Core.Codes;
using NexusP2P.Core.Crypto;
using NexusP2P.Core.Manifest;
using NexusP2P.Transfer;
using NexusP2P.Transfer.Protocol;
using NexusP2P.Transfer.Reconnect;
using NexusP2P.Transfer.Storage;
using NexusP2P.Transport.WebRtc;

namespace NexusP2P.Agent.Transfers;

/// <summary>
/// 一次传输的完整生命周期，包成 UI 能直接用的形状。
///
/// <para><b>为什么需要这一层</b>：<see cref="SendSession"/> 与
/// <see cref="ReceiveSession"/> 是纯粹的协议状态机，它们对「算清单要多久」、
/// 「重连到第几次了」、「当前瓶颈是什么」这些界面关心的事一无所知。
/// 把这些散在 UI 里会让同样的逻辑在 WPF 与将来任何别的宿主里各写一遍。</para>
///
/// <para>UI 只做两件事：调 <see cref="StartSendAsync"/> 或
/// <see cref="StartReceiveAsync"/>，然后订阅 <see cref="SnapshotChanged"/>。
/// 全部状态都在 <see cref="TransferSnapshot"/> 里。</para>
///
/// <para><b>事件在后台线程上触发。</b>WPF 的订阅方必须自己 Dispatcher.Invoke ——
/// 这里刻意不引入对 WPF 的依赖，否则这个类就只能给 WPF 用了。</para>
/// </summary>
public sealed class TransferManager : IAsyncDisposable
{
    private readonly AgentOptions _options;
    private readonly SettingsStore _settings;
    private readonly RateTracker _rate = new();
    private readonly Lock _gate = new();

    private CancellationTokenSource? _cancellation;
    private TransferSnapshot _snapshot;
    private bool _disposed;

    public TransferManager(AgentOptions options, SettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(settings);

        _options = options;
        _settings = settings;
        _snapshot = Idle();
    }

    /// <summary>状态变了。<b>在后台线程上触发</b>，UI 侧要自己切回界面线程。</summary>
    public event Action<TransferSnapshot>? SnapshotChanged;

    public TransferSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    /// <summary>有没有正在进行的传输。关窗前要问这个（AD：关窗不中断传输）。</summary>
    public bool IsBusy => Snapshot.Phase is TransferPhase.Preparing or TransferPhase.WaitingForPeer
        or TransferPhase.Connecting or TransferPhase.Transferring or TransferPhase.Verifying;

    private static TransferSnapshot Idle() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        IsSending = false,
        Phase = TransferPhase.Completed,
        TotalBytes = 0,
    };

    /// <summary>
    /// 发送一个文件或文件夹。
    ///
    /// <para>返回的 Task 在传输结束（成功、失败、取消）时完成。
    /// 界面不该 await 它 —— 该订阅 <see cref="SnapshotChanged"/>。</para>
    /// </summary>
    public async Task StartSendAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var cancellation = BeginOperation(isSending: true);

        try
        {
            // 1. 算清单。20 GB 要跑十几秒，全程报进度。
            Update(s => s with { Phase = TransferPhase.Preparing, Bottleneck = Bottleneck.Hashing });

            var manifest = await ManifestBuilder
                .BuildAsync(
                    path,
                    progress: new Progress<long>(hashed =>
                        Update(s => s with { CompletedBytes = hashed })),
                    cancellationToken: cancellation.Token)
                .ConfigureAwait(false);

            var secret = TransferSecret.Generate();
            var root = ResolveSourceRoot(path);

            Update(s => s with
            {
                Phase = TransferPhase.WaitingForPeer,
                CompletedBytes = 0,
                TotalBytes = manifest.TotalLength,
                Bottleneck = Bottleneck.Unknown,
            });

            // 2. 建房等对方。重连时回同一个房间，不换码。
            await using var peers = ReconnectingPeerSource.ForSender(
                _options,
                onRoomCreated: room => Update(s => s with
                {
                    Code = TransferCode.Parse(room.Code).ToString(),
                    ShareUrl = BuildShareUrl(room, secret),
                }),
                onPeerArrived: () => Update(s => s with { Phase = TransferPhase.Connecting }));

            // 3. 传。断线自动重连 3 次（AD-7），进度按位图续。
            await ResilientSession.RunAsync(
                connect: peers.ConnectAsync,
                session: async (connection, token) =>
                {
                    OnConnected(peers);

                    // 每次尝试都用一份新的分片源：上一条连接失败时它可能停在
                    // 半途的读取状态，复用等于把上一次的故障带进新连接
                    await using var source = new FilePieceSource(manifest, root);

                    await new SendSession(manifest, source, secret)
                        .RunAsync(connection, ReportProgress(manifest.TotalLength, peers), token)
                        .ConfigureAwait(false);

                    return true;
                },
                status: new ReconnectReporter(this),
                cancellationToken: cancellation.Token)
                .ConfigureAwait(false);

            Update(s => s with
            {
                Phase = TransferPhase.Completed,
                CompletedBytes = manifest.TotalLength,
                Error = null,
            });
        }
        catch (OperationCanceledException)
        {
            Update(s => s with { Phase = TransferPhase.Cancelled, Error = null });
        }
        catch (Exception ex)
        {
            Update(s => s with { Phase = TransferPhase.Failed, Error = Explain(ex) });
        }
        finally
        {
            EndOperation(cancellation);
        }
    }

    /// <summary>
    /// 用分享链接或「文件码 + 密钥」接收。
    ///
    /// <para><paramref name="destination"/> 为 null 时用设置里记住的目录（AD-9）。</para>
    /// </summary>
    public async Task StartReceiveAsync(string target, string? key = null, string? destination = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var cancellation = BeginOperation(isSending: false);

        try
        {
            var (code, secret) = ParseTarget(target, key);
            var folder = destination ?? _settings.Load().EffectiveReceiveDirectory;

            // 传输开始前就检查，而不是传到一半才发现目标不可写或空间不够
            var check = DestinationCheck.Check(folder);
            if (!check.IsUsable)
            {
                throw new InvalidOperationException(check.Problem);
            }

            Directory.CreateDirectory(folder);

            Update(s => s with
            {
                Phase = TransferPhase.Connecting,
                Code = TransferCode.Parse(code).ToString(),
            });

            await using var peers = ReconnectingPeerSource.ForReceiver(_options, code);

            var result = await ResilientSession.RunAsync(
                connect: peers.ConnectAsync,
                session: (connection, token) =>
                {
                    OnConnected(peers);

                    return new ReceiveSession(secret, folder).RunAsync(
                        connection,
                        ReportProgress(0, peers),
                        new Progress<RescanProgress>(rescan => Update(s => s with
                        {
                            Phase = TransferPhase.Verifying,
                            CompletedBytes = rescan.BytesScanned,
                            TotalBytes = rescan.BytesTotal > 0 ? rescan.BytesTotal : s.TotalBytes,
                        })),
                        token);
                },
                status: new ReconnectReporter(this),
                cancellationToken: cancellation.Token)
                .ConfigureAwait(false);

            Update(s => s with
            {
                Phase = TransferPhase.Completed,
                CompletedBytes = result.Manifest.TotalLength,
                TotalBytes = result.Manifest.TotalLength,
                LandedFiles = [.. result.LandedFiles],
                Error = null,
            });
        }
        catch (OperationCanceledException)
        {
            Update(s => s with { Phase = TransferPhase.Cancelled, Error = null });
        }
        catch (Exception ex)
        {
            Update(s => s with { Phase = TransferPhase.Failed, Error = Explain(ex) });
        }
        finally
        {
            EndOperation(cancellation);
        }
    }

    /// <summary>
    /// 一对多发送（V2，AD-15）。<paramref name="maxReceivers"/> 为 1 时
    /// 直接走 <see cref="StartSendAsync"/> —— 一对一的行为一个字都不变。
    ///
    /// <para>返回的 Task 在房间关闭（用户取消）或出错时完成。
    /// 一对多没有「自动结束」：发送方不知道还会不会有人来，
    /// 守到用户主动停止（<see cref="Cancel"/>）为止。</para>
    /// </summary>
    public async Task StartSendManyAsync(string path, int maxReceivers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxReceivers, 1);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (maxReceivers == 1)
        {
            await StartSendAsync(path).ConfigureAwait(false);
            return;
        }

        var cancellation = BeginOperation(isSending: true);

        try
        {
            Update(s => s with { Phase = TransferPhase.Preparing, Bottleneck = Bottleneck.Hashing });

            var manifest = await ManifestBuilder
                .BuildAsync(
                    path,
                    progress: new Progress<long>(hashed =>
                        Update(s => s with { CompletedBytes = hashed })),
                    cancellationToken: cancellation.Token)
                .ConfigureAwait(false);

            var secret = TransferSecret.Generate();
            var root = ResolveSourceRoot(path);

            Update(s => s with
            {
                Phase = TransferPhase.WaitingForPeer,
                CompletedBytes = 0,
                TotalBytes = manifest.TotalLength,
                Bottleneck = Bottleneck.Unknown,
                MaxReceivers = maxReceivers,
            });

            await using var source = new FilePieceSource(manifest, root);
            using var cache = new CipherPieceCache(manifest, source, secret);
            await using var fanOut = new FanOutSender(_options, manifest, secret, cache);

            var board = new ReceiverBoard(manifest.TotalLength);
            fanOut.PeerStatusChanged += status =>
                Update(s => board.Apply(s, status));

            try
            {
                await fanOut.RunAsync(
                    maxReceivers,
                    onRoomCreated: room => Update(s => s with
                    {
                        Code = TransferCode.Parse(room.Code).ToString(),
                        ShareUrl = string.IsNullOrEmpty(room.ShareUrlBase)
                            ? null
                            : $"{room.ShareUrlBase}/{room.Code}#{secret.ToBase64Url()}",
                        MaxReceivers = room.MaxReceivers,
                    }),
                    until: null,
                    cancellationToken: cancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 用户点了停止：不再接纳新人，等已开的链路自然结束
            }

            await fanOut.WhenAllLinksSettledAsync().ConfigureAwait(false);

            Update(s =>
            {
                var completed = s.Receivers.Count(r => r.Completed);
                return s with
                {
                    Phase = completed == s.Receivers.Count && completed > 0
                        ? TransferPhase.Completed
                        : s.Receivers.Count == 0 ? TransferPhase.Cancelled : TransferPhase.Failed,
                    Error = completed == s.Receivers.Count || s.Receivers.Count == 0
                        ? null
                        : $"{s.Receivers.Count - completed} 个接收方没有收完。",
                };
            });
        }
        catch (OperationCanceledException)
        {
            Update(s => s with { Phase = TransferPhase.Cancelled, Error = null });
        }
        catch (Exception ex)
        {
            Update(s => s with { Phase = TransferPhase.Failed, Error = Explain(ex) });
        }
        finally
        {
            EndOperation(cancellation);
        }
    }

    /// <summary>取消当前传输。已收到的部分保留在磁盘上，之后可以续传。</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            _cancellation?.Cancel();
        }
    }

    private CancellationTokenSource BeginOperation(bool isSending)
    {
        var cancellation = new CancellationTokenSource();

        lock (_gate)
        {
            if (IsBusy)
            {
                throw new InvalidOperationException("已经有一个传输在进行中。");
            }

            _cancellation?.Dispose();
            _cancellation = cancellation;
            _snapshot = new TransferSnapshot
            {
                Id = Guid.NewGuid().ToString("N"),
                IsSending = isSending,
                Phase = TransferPhase.Preparing,
            };
        }

        SnapshotChanged?.Invoke(Snapshot);
        return cancellation;
    }

    private void EndOperation(CancellationTokenSource cancellation)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_cancellation, cancellation))
            {
                _cancellation = null;
            }
        }

        cancellation.Dispose();
    }

    /// <summary>连上之后把「直连还是中继」记进快照 —— 这就是瓶颈说明的输入。</summary>
    private void OnConnected(ReconnectingPeerSource peers)
    {
        var kind = peers.CandidateKind ?? CandidatePairKind.Unknown;

        Update(s => s with
        {
            Phase = TransferPhase.Transferring,
            Bottleneck = TransferSnapshot.FromCandidatePair(kind),
            ReconnectAttempt = 0,
            Error = null,
        });
    }

    private Progress<TransferProgress> ReportProgress(long totalBytes, ReconnectingPeerSource peers)
    {
        return new Progress<TransferProgress>(progress =>
        {
            var now = DateTimeOffset.UtcNow;
            _rate.Record(progress.CompletedBytes, now);

            Update(s => s with
            {
                Phase = TransferPhase.Transferring,
                CompletedBytes = progress.CompletedBytes,
                TotalBytes = progress.TotalBytes > 0 ? progress.TotalBytes : totalBytes,
                BytesPerSecond = _rate.BytesPerSecond(now),
                Bottleneck = TransferSnapshot.FromCandidatePair(
                    peers.CandidateKind ?? CandidatePairKind.Unknown),
            });
        });
    }

    private void Update(Func<TransferSnapshot, TransferSnapshot> change)
    {
        TransferSnapshot updated;

        lock (_gate)
        {
            updated = change(_snapshot);
            _snapshot = updated;
        }

        SnapshotChanged?.Invoke(updated);
    }

    internal void ReportReconnect(ReconnectStatus status) =>
        Update(s => s.WithReconnect(status));

    /// <summary>发送端读分片的基准目录：文件取其所在目录，文件夹取其上级。</summary>
    private static string ResolveSourceRoot(string path)
    {
        var full = Path.GetFullPath(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return Path.GetDirectoryName(full) ?? full;
    }

    private static string? BuildShareUrl(Signaling.RoomCreated room, TransferSecret secret)
    {
        if (string.IsNullOrEmpty(room.ShareUrlBase))
        {
            return null;
        }

        return $"{room.ShareUrlBase}/{room.Code}#{secret.ToBase64Url()}";
    }

    private static (string Code, TransferSecret Secret) ParseTarget(string target, string? key)
    {
        if (ShareLinkFactory.TryParse(target, out var link))
        {
            return (link.Code.Digits, link.Secret);
        }

        if (!TransferCode.TryParse(target, out var code))
        {
            throw new ArgumentException($"\"{target}\" 既不是分享链接，也不是九位文件码。");
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("用文件码接收时必须同时提供密钥（分享链接里 # 后面那一串）。");
        }

        if (!TransferSecret.TryFromBase64Url(key.Trim(), out var secret))
        {
            throw new ArgumentException("密钥格式不对。它应该是 43 个字符的一长串。");
        }

        return (code.Digits, secret);
    }

    /// <summary>
    /// 把异常翻译成用户能看懂的话。
    ///
    /// <para>直接把 <c>ex.Message</c> 甩到界面上，用户会看到
    /// 「对端报错：连续 16 个分片校验失败，放弃。」这种只有开发者才懂的句子。</para>
    /// </summary>
    private static string Explain(Exception ex) => ex switch
    {
        Signaling.SignalingException signaling => signaling.Message,

        TransferFailedException { Code: TransferErrorCode.InvalidManifest } =>
            "对方的文件码或密钥与这次传输不匹配。请确认复制的是完整的分享链接。",

        TransferFailedException { Code: TransferErrorCode.InsufficientDiskSpace } =>
            "磁盘空间不足，无法容纳这次传输的全部内容。",

        TransferFailedException { Code: TransferErrorCode.DestinationNotWritable } =>
            "接收目录不可写。换一个目录再试。",

        TransferFailedException failed => failed.Message,

        FileNotFoundException => "找不到要发送的文件。它可能已被移动或删除。",

        UnauthorizedAccessException => "没有权限读写相关文件。",

        _ => ex.Message,
    };

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Cancel();

        lock (_gate)
        {
            _cancellation?.Dispose();
            _cancellation = null;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}

/// <summary>把重连状态转发进快照。让「正在重连 2/3」在界面上可见。</summary>
internal sealed class ReconnectReporter(TransferManager manager) : IProgress<ReconnectStatus>
{
    public void Report(ReconnectStatus value) => manager.ReportReconnect(value);
}

/// <summary>
/// 把逐链路的 <see cref="FanOutPeerStatus"/> 累积成快照上的接收方列表，
/// 并维护整体进度（各链路已传字节之和 / 字节数×人数）。
///
/// <para>速率是逐链路算的 —— 把所有链路的字节混进一个 RateTracker
/// 会把「3 个人各 2 MB/s」显示成「6 MB/s」，对单个接收方的观感是错的。
/// 整体那行显示的是各链路速率之和，语义是「本机上行的实际消耗」。</para>
/// </summary>
public sealed class ReceiverBoard(long totalBytesPerReceiver)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ReceiverView> _views = [];
    private readonly Dictionary<string, RateTracker> _rates = [];

    public TransferSnapshot Apply(TransferSnapshot snapshot, FanOutPeerStatus status)
    {
        lock (_gate)
        {
            if (!_rates.TryGetValue(status.PeerId, out var rate))
            {
                rate = new RateTracker();
                _rates[status.PeerId] = rate;
            }

            var now = DateTimeOffset.UtcNow;
            rate.Record(status.Progress.CompletedBytes, now);

            var total = status.Progress.TotalBytes > 0 ? status.Progress.TotalBytes : totalBytesPerReceiver;

            _views[status.PeerId] = new ReceiverView
            {
                PeerId = status.PeerId,
                Completed = status.State == Transfer.FanOutLinkState.Completed,
                CompletedBytes = status.State == Transfer.FanOutLinkState.Completed
                    ? total
                    : status.Progress.CompletedBytes,
                TotalBytes = total,
                BytesPerSecond = status.State == Transfer.FanOutLinkState.Running
                    ? rate.BytesPerSecond(now)
                    : 0,
                Bottleneck = status.CandidateKind is { } kind
                    ? TransferSnapshot.FromCandidatePair(kind)
                    : Bottleneck.Unknown,
                Error = status.State == Transfer.FanOutLinkState.Failed
                    ? status.Error?.Message
                    : null,
            };

            // 按 peerId 排序保证列表稳定 —— 字典顺序会让行在界面上跳来跳去
            var receivers = _views.Values.OrderBy(v => v.PeerId, StringComparer.Ordinal).ToArray();

            return snapshot with
            {
                Phase = TransferPhase.Transferring,
                Receivers = receivers,
                CompletedBytes = receivers.Sum(v => v.CompletedBytes),
                TotalBytes = totalBytesPerReceiver * Math.Max(1, receivers.Length),
                BytesPerSecond = receivers.Sum(v => v.BytesPerSecond),
                Bottleneck = snapshot.Bottleneck,
            };
        }
    }
}
