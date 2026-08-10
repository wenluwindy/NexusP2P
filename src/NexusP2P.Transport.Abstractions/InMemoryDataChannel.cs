using System.Threading.Channels;

namespace NexusP2P.Transport.Abstractions;

/// <summary>
/// 互联的一对内存通道，用来在<b>完全不碰网络</b>的前提下开发和测试传输协议。
///
/// <para>见 AD-1：协议的正确性先在这里证明，真实 WebRTC 只是换掉
/// <see cref="IDataChannel"/> 的实现。这样接真网时只需面对一类 bug（网络），
/// 而不是网络与协议两类叠加。</para>
/// </summary>
public sealed class InMemoryDataChannelPair : IAsyncDisposable
{
    private readonly InMemoryDataChannel _left;
    private readonly InMemoryDataChannel _right;

    private InMemoryDataChannelPair(InMemoryDataChannel left, InMemoryDataChannel right)
    {
        _left = left;
        _right = right;
    }

    public IDataChannel Left => _left;

    public IDataChannel Right => _right;

    /// <summary>浏览器跨实现的安全上限，用作默认值。</summary>
    public const int DefaultMaxMessageSize = 256 * 1024;

    /// <summary>
    /// 建一对互联的通道。
    ///
    /// <para><b>故障只注入到 <paramref name="faults"/> 指定的方向（左 → 右）</b>，
    /// 右 → 左默认无故障。这个不对称是刻意的：测试里左端是发送方，
    /// 想坏的是它发出的分片。若两个方向都注入，连接收方回发的位图都会被改坏，
    /// 测到的就不是「分片被拒收后能重传」而是「位图畸形」。</para>
    /// </summary>
    public static InMemoryDataChannelPair Create(
        FaultProfile? faults = null,
        int maxMessageSize = DefaultMaxMessageSize,
        FaultProfile? reverseFaults = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxMessageSize, 1);

        var left = new InMemoryDataChannel("left", maxMessageSize, faults ?? FaultProfile.None);
        var right = new InMemoryDataChannel("right", maxMessageSize, reverseFaults ?? FaultProfile.None);

        left.Connect(right);
        right.Connect(left);

        return new InMemoryDataChannelPair(left, right);
    }

    public async ValueTask DisposeAsync()
    {
        await _left.DisposeAsync().ConfigureAwait(false);
        await _right.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>内存通道的一端。由 <see cref="InMemoryDataChannelPair.Create"/> 成对创建。</summary>
internal sealed class InMemoryDataChannel : IDataChannel
{
    private readonly Channel<byte[]> _outbound = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly CancellationTokenSource _shutdown = new();
    private readonly FaultProfile _faults;
    private readonly string _name;
    private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private InMemoryDataChannel _peer = null!;
    private Task _pump = Task.CompletedTask;

    private long _bufferedAmount;
    private long _deliveredBytes;
    private long _deliveredMessages;
    private bool _wasAboveThreshold;
    private int _closedFlag;

    private TaskCompletionSource _drainPulse = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public InMemoryDataChannel(string name, int maxMessageSize, FaultProfile faults)
    {
        _name = name;
        MaxMessageSize = maxMessageSize;
        _faults = faults;
    }

    public DataChannelState State { get; private set; } = DataChannelState.Connecting;

    public int MaxMessageSize { get; }

    public long BufferedAmount => Interlocked.Read(ref _bufferedAmount);

    public long BufferedAmountLowThreshold { get; set; }

    /// <summary>已成功投递给对端的字节数。测试用来断言故障注入的触发点。</summary>
    public long DeliveredBytes => Interlocked.Read(ref _deliveredBytes);

    public event Action? BufferedAmountLow;

    public event Action<ReadOnlyMemory<byte>>? MessageReceived;

    public event Action<string?>? Closed;

    internal void Connect(InMemoryDataChannel peer)
    {
        _peer = peer;
        State = DataChannelState.Open;
        _opened.TrySetResult();
        _pump = Task.Run(PumpAsync);
    }

    public void Send(ReadOnlySpan<byte> message)
    {
        if (State != DataChannelState.Open)
        {
            throw new DataChannelClosedException($"通道 {_name} 当前状态为 {State}。");
        }

        if (message.Length > MaxMessageSize)
        {
            throw new ArgumentException(
                $"消息 {message.Length} 字节超过上限 {MaxMessageSize} 字节。", nameof(message));
        }

        // 复制一份：契约上 Send 是同步入队，调用方在返回后就可以复用它的缓冲区。
        var copy = message.ToArray();

        Interlocked.Add(ref _bufferedAmount, copy.Length);
        if (BufferedAmountLowThreshold > 0 && BufferedAmount > BufferedAmountLowThreshold)
        {
            _wasAboveThreshold = true;
        }

        if (!_outbound.Writer.TryWrite(copy))
        {
            Interlocked.Add(ref _bufferedAmount, -copy.Length);
            throw new DataChannelClosedException($"通道 {_name} 的发送队列已关闭。");
        }
    }

    public Task WaitForOpenAsync(CancellationToken cancellationToken = default) =>
        _opened.Task.WaitAsync(cancellationToken);

    public async Task WaitForDrainAsync(long threshold, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(threshold);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 先抓住脉冲任务再判断，否则可能错过判断与等待之间发生的那次投递
            var pulse = Volatile.Read(ref _drainPulse).Task;

            if (BufferedAmount <= threshold)
            {
                return;
            }

            if (State is DataChannelState.Closed or DataChannelState.Closing)
            {
                throw new DataChannelClosedException($"通道 {_name} 在等待排空期间关闭。");
            }

            await pulse.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var message in _outbound.Reader
                               .ReadAllAsync(_shutdown.Token)
                               .ConfigureAwait(false))
            {
                if (_faults.DeliveryDelay > TimeSpan.Zero)
                {
                    await Task.Delay(_faults.DeliveryDelay, _shutdown.Token).ConfigureAwait(false);
                }

                if (_faults.DrainBytesPerSecond is { } rate and > 0)
                {
                    var seconds = (double)message.Length / rate;
                    if (seconds > 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(seconds), _shutdown.Token).ConfigureAwait(false);
                    }
                }

                Interlocked.Add(ref _bufferedAmount, -message.Length);
                var delivered = Interlocked.Add(ref _deliveredBytes, message.Length);
                var count = Interlocked.Increment(ref _deliveredMessages);

                RaiseBufferedAmountLowIfNeeded();
                PulseDrain();

                if (_faults.CorruptMessageOrdinals?.Contains(count) == true && message.Length > 0)
                {
                    // 只翻末字节。对 Piece 消息那是 GCM 认证标签，帧仍可解析但必定校验失败。
                    message[^1] ^= 0xFF;
                }

                try
                {
                    _peer.MessageReceived?.Invoke(message);
                }
                catch (Exception ex)
                {
                    // 消费方回调抛异常等于对端处理不了数据 —— 断开是诚实的反应。
                    // 若在这里静默吞掉，症状会变成「传输卡住但没有任何错误」。
                    await CloseBothAsync($"接收回调抛出异常：{ex.Message}").ConfigureAwait(false);
                    return;
                }

                if (ShouldDisconnect(delivered, count))
                {
                    await CloseBothAsync(_faults.DisconnectReason).ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常关闭
        }
        catch (ChannelClosedException)
        {
            // 正常关闭
        }
    }

    /// <summary>
    /// 阈值按「已投递字节/消息数」判定。跨过阈值的那条消息会<b>完整投递后</b>
    /// 才断开 —— 半条消息不是 WebRTC 会发生的事，模拟它只会造出真实网络里
    /// 不存在的失败模式。
    /// </summary>
    private bool ShouldDisconnect(long deliveredBytes, long deliveredMessages) =>
        (_faults.DisconnectAfterBytes is { } byteLimit && deliveredBytes >= byteLimit) ||
        (_faults.DisconnectAfterMessages is { } messageLimit && deliveredMessages >= messageLimit);

    private void RaiseBufferedAmountLowIfNeeded()
    {
        if (BufferedAmountLowThreshold <= 0 || !_wasAboveThreshold)
        {
            return;
        }

        if (BufferedAmount <= BufferedAmountLowThreshold)
        {
            _wasAboveThreshold = false;
            BufferedAmountLow?.Invoke();
        }
    }

    private void PulseDrain()
    {
        var previous = Interlocked.Exchange(
            ref _drainPulse,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        previous.TrySetResult();
    }

    private async Task CloseBothAsync(string? reason)
    {
        await CloseAsync(reason).ConfigureAwait(false);
        await _peer.CloseAsync(reason).ConfigureAwait(false);
    }

    public Task CloseAsync(string? reason = null)
    {
        if (Interlocked.Exchange(ref _closedFlag, 1) == 1)
        {
            return Task.CompletedTask;
        }

        State = DataChannelState.Closed;
        _outbound.Writer.TryComplete();
        _shutdown.Cancel();

        // 唤醒所有等待排空的调用方，让它们看到关闭状态并抛出
        PulseDrain();
        _opened.TrySetCanceled();

        Closed?.Invoke(reason);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);

        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 关闭时取消是预期行为
        }

        _shutdown.Dispose();
    }
}
