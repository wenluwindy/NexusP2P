using System.Runtime.InteropServices;
using DataChannelDotnet.Bindings;
using NexusP2P.Transport.Abstractions;
using NexusP2P.Transport.WebRtc.Interop;

namespace NexusP2P.Transport.WebRtc;

/// <summary>
/// <see cref="IDataChannel"/> 在 libdatachannel 上的实现。
///
/// <para><b>比内存实现好的一点</b>：这里有真正的低水位<b>回调</b>
/// （<c>rtcSetBufferedAmountLowCallback</c>），所以背压不用轮询。
/// SIPSorcery 没有这个事件，只能 <c>Task.Delay(1)</c> 轮询，
/// 而那在 Windows 上实际睡 15.6ms —— 光轮询就能吃掉大半吞吐。</para>
///
/// <para><b>最危险的地方是生命周期</b>：原生回调跑在 libdatachannel 自己的线程上，
/// 且可能在我们正在拆连接的时候触发。所以：</para>
/// <list type="bullet">
/// <item>释放时<b>先</b>把用户指针清成 0，静态回调看到 0 就直接返回</item>
/// <item>GCHandle 在原生对象删除<b>之后</b>才释放</item>
/// <item>回调里任何异常都必须吞掉 —— 让异常穿回原生栈是未定义行为</item>
/// </list>
/// </summary>
public sealed class WebRtcDataChannel : IDataChannel, IDisposable
{
    /// <summary>
    /// 对外报告的单条消息上限。
    ///
    /// <para>刻意保守：浏览器跨实现的安全上限是 256 KiB，但恰好贴着上限发
    /// 容易在边界上失败。64 KiB 在所有实现里都稳，而 spike 已证明
    /// 64 KiB 分片就能跑到 76 MiB/s —— 没有理由去冒边界的风险。</para>
    /// </summary>
    public const int SafeMaxMessageSize = 64 * 1024;

    private readonly int _id;
    private readonly GCHandle _self;
    private readonly Lock _stateGate = new();

    private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _drainPulse = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _bufferedAmountLowThreshold;
    /// <summary>
    /// 接管之前最多攒多少条消息。
    ///
    /// <para>真实窗口只有几毫秒，撑死几条。这个上限只是为了让
    /// 「没人订阅」这种编码错误不至于把内存吃光 —— 到顶之后不再攒，
    /// 并在有人订阅时立刻报错，而不是假装一切正常。</para>
    /// </summary>
    private const int MaxBacklogMessages = 1024;

    private readonly Lock _deliveryGate = new();
    private readonly Queue<ReadOnlyMemory<byte>> _backlog = new();
    private Action<ReadOnlyMemory<byte>>? _messageReceived;
    private bool _backlogOverflowed;

    private readonly TimeSpan _openTimeout;
    private int _closedFlag;
    private bool _disposed;

    internal unsafe WebRtcDataChannel(int id, int maxMessageSize, TimeSpan openTimeout)
    {
        _id = id;
        MaxMessageSize = maxMessageSize;
        _openTimeout = openTimeout;

        // 用户指针是静态回调找回实例的桥。必须在挂回调之前设好，
        // 否则一个抢先触发的回调会拿到 0 而丢事件。
        _self = GCHandle.Alloc(this, GCHandleType.Normal);
        NativeRtc.SetUserPointer(id, GCHandle.ToIntPtr(_self));

        // 回调注册失败会让对应事件永远收不到，那是致命的（比如永远等不到 Open）。
        // 但 libdatachannel 只在 id 无效时才失败，而 id 刚从它自己那儿拿到，
        // 所以这里显式丢弃返回码并在下一行验证通道仍然存活。
        _ = Rtc.rtcSetOpenCallback(id, &OnOpenThunk);
        _ = Rtc.rtcSetClosedCallback(id, &OnClosedThunk);
        _ = Rtc.rtcSetErrorCallback(id, &OnErrorThunk);
        _ = Rtc.rtcSetMessageCallback(id, &OnMessageThunk);
        _ = Rtc.rtcSetBufferedAmountLowCallback(id, &OnBufferedAmountLowThunk);

        // 建通道时可能已经是打开状态（比如作为 answerer 收到的通道）
        if (!NativeRtc.IsClosed(id))
        {
            State = DataChannelState.Connecting;
        }
    }

    public DataChannelState State { get; private set; } = DataChannelState.Connecting;

    public int MaxMessageSize { get; }

    public long BufferedAmount => _disposed ? 0 : NativeRtc.GetBufferedAmount(_id);

    public long BufferedAmountLowThreshold
    {
        get => Interlocked.Read(ref _bufferedAmountLowThreshold);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            Interlocked.Exchange(ref _bufferedAmountLowThreshold, value);

            if (!_disposed)
            {
                NativeRtc.SetBufferedAmountLowThreshold(_id, (int)Math.Min(value, int.MaxValue));
            }
        }
    }

    public event Action? BufferedAmountLow;

    /// <summary>
    /// 收到一条完整消息。
    ///
    /// <para><b>在挂上处理器之前到达的消息会被攒住，挂上时按原顺序补发。</b>
    /// 原生消息回调在构造函数里就注册好了，而上层要等通道被交出去之后才订阅 ——
    /// 中间这个窗口里到达的消息，直接投给一个空事件就是<b>永久丢失</b>。</para>
    ///
    /// <para>对 answerer 尤其致命：它的通道是对端建过来的，对端一看到通道打开
    /// 就立刻发第一条消息（清单）。丢掉之后的现象是<b>两端一起干等、谁都不报错</b>，
    /// 而且机器越快、对端越「热」（比如断线重连的第二次）越容易发生。</para>
    /// </summary>
    public event Action<ReadOnlyMemory<byte>>? MessageReceived
    {
        add
        {
            lock (_deliveryGate)
            {
                _messageReceived += value;

                while (_backlog.TryDequeue(out var pending))
                {
                    value?.Invoke(pending);
                }

                if (_backlogOverflowed)
                {
                    _backlogOverflowed = false;

                    // 攒过头说明根本没人来订阅。丢了的消息补不回来，
                    // 但至少要让上层立刻失败而不是永远等下去。
                    //
                    // **推迟到访问器返回之后再关。** 典型的订阅方
                    // （ProtocolConnection）是在构造函数里先挂 MessageReceived、
                    // 下一行才挂 Closed —— 在这里同步关掉，那条通知正好落空。
                    ThreadPool.QueueUserWorkItem(static state =>
                        _ = state.CloseAsync(
                            $"通道在被接管前积压超过 {MaxBacklogMessages} 条消息，已有数据丢失。"),
                        this, preferLocal: false);
                }
            }
        }

        remove
        {
            lock (_deliveryGate)
            {
                _messageReceived -= value;
            }
        }
    }

    public event Action<string?>? Closed;

    /// <summary>原生通道 id。诊断用。</summary>
    public int NativeId => _id;

    public void Send(ReadOnlySpan<byte> message)
    {
        if (State != DataChannelState.Open)
        {
            throw new DataChannelClosedException($"通道当前状态为 {State}。");
        }

        if (message.Length > MaxMessageSize)
        {
            throw new ArgumentException(
                $"消息 {message.Length} 字节超过上限 {MaxMessageSize} 字节。", nameof(message));
        }

        NativeRtc.SendMessage(_id, message);
    }

    /// <summary>
    /// 等通道打开。
    ///
    /// <para><b>必须有超时。</b>ICE 打洞失败时原生侧不会给出任何回调 ——
    /// 没有超时就是永久挂起：界面停在「等待对方接收」，没有错误、没有进度、
    /// 也没有可重试的时机。这与 answerer 侧的
    /// <see cref="WebRtcPeerConnection.WaitForIncomingChannelAsync"/> 对称。</para>
    /// </summary>
    public Task WaitForOpenAsync(CancellationToken cancellationToken = default) =>
        _opened.Task.WaitAsync(_openTimeout, cancellationToken);

    public async Task WaitForDrainAsync(long threshold, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(threshold);

        // 有低水位回调就用事件驱动。阈值设成调用方要等的水位，
        // 这样每次排空到位时会被原生侧主动唤醒，不必轮询。
        var previousThreshold = BufferedAmountLowThreshold;
        BufferedAmountLowThreshold = threshold;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 先抓脉冲再判断，否则会错过判断与等待之间发生的那次排空
                var pulse = Volatile.Read(ref _drainPulse).Task;

                if (BufferedAmount <= threshold)
                {
                    return;
                }

                if (State is DataChannelState.Closed or DataChannelState.Closing)
                {
                    throw new DataChannelClosedException("等待排空期间通道关闭。");
                }

                try
                {
                    // 兜一个超时：万一低水位回调因为某种原因没来，
                    // 退化为低频轮询而不是永久挂死。
                    await pulse.WaitAsync(TimeSpan.FromMilliseconds(200), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // 这一轮没等到脉冲，回到循环顶上重新读一次实际水位。
                    //
                    // **catch 必须在循环里面。**放在循环外面的话，一次超时就直接
                    // 退出整个方法 —— 调用方以为排空完成，实际缓冲还是满的，
                    // 背压就此失效：发送端会继续往一个塞满的缓冲里灌。
                    // 机器一忙（回调晚于 200ms）就会发生。
                }
            }
        }
        finally
        {
            if (!_disposed)
            {
                BufferedAmountLowThreshold = previousThreshold;
            }
        }
    }

    public Task CloseAsync(string? reason = null)
    {
        if (Interlocked.Exchange(ref _closedFlag, 1) == 1)
        {
            return Task.CompletedTask;
        }

        lock (_stateGate)
        {
            State = DataChannelState.Closed;
        }

        if (!_disposed)
        {
            NativeRtc.Close(_id);
        }

        PulseDrain();
        _opened.TrySetException(new DataChannelClosedException(reason));
        Closed?.Invoke(reason);

        return Task.CompletedTask;
    }

    /// <summary>
    /// 清理<b>全程同步</b>，所以同时提供 <see cref="IDisposable"/> ——
    /// 原生回调里不能 await，需要一条同步的释放路径。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // 顺序很关键：
        // 1. 先清用户指针 —— 之后触发的原生回调会看到 0 并立刻返回
        // 2. 再关闭并删除原生对象
        // 3. 最后释放 GCHandle
        // 反过来做就是 use-after-free。
        NativeRtc.SetUserPointer(_id, nint.Zero);
        _disposed = true;

        // CloseAsync 本身也是同步完成的，这里不需要等
        CloseAsync("已释放").GetAwaiter().GetResult();

        NativeRtc.DeleteDataChannel(_id);

        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    // ---- 原生回调 ----

    private void HandleOpen()
    {
        lock (_stateGate)
        {
            State = DataChannelState.Open;
        }

        // 打开后把阈值同步给原生侧（构造时可能还没设）
        NativeRtc.SetBufferedAmountLowThreshold(
            _id, (int)Math.Min(BufferedAmountLowThreshold, int.MaxValue));

        _opened.TrySetResult();
    }

    private void HandleClosed()
    {
        if (Interlocked.Exchange(ref _closedFlag, 1) == 1)
        {
            return;
        }

        lock (_stateGate)
        {
            State = DataChannelState.Closed;
        }

        PulseDrain();
        _opened.TrySetException(new DataChannelClosedException("通道被对端关闭。"));
        Closed?.Invoke(null);
    }

    private void HandleError(string? error)
    {
        _opened.TrySetException(new WebRtcException($"DataChannel 错误：{error}"));
        PulseDrain();
    }

    private void HandleMessage(ReadOnlySpan<byte> data)
    {
        // 契约上「回调期间有效」，但原生缓冲区在回调返回后就不归我们了，
        // 所以这里必须复制。消费方拿到的是一份私有副本。
        var copy = data.ToArray();

        // 持锁投递：补发与新到消息必须严格保持顺序，
        // 乱序对 MessageAssembler 来说等同于数据损坏
        lock (_deliveryGate)
        {
            if (_messageReceived is { } handler)
            {
                handler.Invoke(copy);
                return;
            }

            if (_backlog.Count >= MaxBacklogMessages)
            {
                _backlogOverflowed = true;
                return;
            }

            _backlog.Enqueue(copy);
        }
    }

    private void HandleBufferedAmountLow()
    {
        PulseDrain();
        BufferedAmountLow?.Invoke();
    }

    private void PulseDrain()
    {
        var previous = Interlocked.Exchange(
            ref _drainPulse,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        previous.TrySetResult();
    }

    // ---- 静态 thunk：从用户指针取回实例 ----
    //
    // 全部异常都必须在这里吞掉。让托管异常穿回原生栈是未定义行为，
    // 表现通常是进程直接消失，且没有任何可用的错误信息。

    private static WebRtcDataChannel? Resolve(nint userPointer)
    {
        if (userPointer == nint.Zero)
        {
            return null;   // 已释放，回调来晚了
        }

        try
        {
            return GCHandle.FromIntPtr(userPointer).Target as WebRtcDataChannel;
        }
        catch (InvalidOperationException)
        {
            return null;   // 句柄已失效
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe void OnOpenThunk(int id, void* userPointer)
    {
        try
        {
            Resolve((nint)userPointer)?.HandleOpen();
        }
        catch
        {
            // 见上：绝不让异常穿回原生栈
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe void OnClosedThunk(int id, void* userPointer)
    {
        try
        {
            Resolve((nint)userPointer)?.HandleClosed();
        }
        catch
        {
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe void OnErrorThunk(int id, sbyte* error, void* userPointer)
    {
        try
        {
            Resolve((nint)userPointer)?.HandleError(Marshal.PtrToStringUTF8((nint)error));
        }
        catch
        {
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe void OnMessageThunk(int id, sbyte* message, int size, void* userPointer)
    {
        try
        {
            var channel = Resolve((nint)userPointer);
            if (channel is null)
            {
                return;
            }

            // size < 0 在 C API 里表示「以 null 结尾的文本」。
            // 我们的协议只发二进制，收到文本说明对端实现不对 —— 忽略即可。
            if (size < 0)
            {
                return;
            }

            channel.HandleMessage(new ReadOnlySpan<byte>(message, size));
        }
        catch
        {
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe void OnBufferedAmountLowThunk(int id, void* userPointer)
    {
        try
        {
            Resolve((nint)userPointer)?.HandleBufferedAmountLow();
        }
        catch
        {
        }
    }
}
