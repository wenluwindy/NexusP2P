using System.Buffers.Binary;
using NexusP2P.Transport.Abstractions;

namespace NexusP2P.InteropHarness;

/// <summary>
/// 把 stdin/stdout 当成一条 <see cref="IDataChannel"/>。
///
/// <para><b>为什么需要它</b>：网页端是这套协议的第二个实现，而
/// 「网页发、exe 收」这条路从来没被真的跑过一次。单元向量能证明哈希与密文
/// 逐字节一致，但证明不了<b>消息序列</b>兼容 —— 而那正是最容易出问题的地方
/// （C# 侧已经栽过两次「两端一起干等、谁都不报错」）。</para>
///
/// <para>线上格式：每条消息前面加一个 4 字节大端长度。stdio 是字节流，
/// 而 DataChannel 的语义是消息式的 —— 少了长度前缀就没法还原消息边界。</para>
/// </summary>
internal sealed class StdioDataChannel : IDataChannel
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly Lock _writeGate = new();
    private readonly CancellationTokenSource _shutdown = new();

    private Task? _readPump;
    private int _closed;

    public StdioDataChannel(Stream input, Stream output)
    {
        _input = input;
        _output = output;
        State = DataChannelState.Open;
    }

    public DataChannelState State { get; private set; }

    /// <summary>与 WebRtcDataChannel.SafeMaxMessageSize 一致。</summary>
    public int MaxMessageSize => 64 * 1024;

    /// <summary>stdout 是同步写的，写完就算发出去了 —— 永远没有积压。</summary>
    public long BufferedAmount => 0;

    public long BufferedAmountLowThreshold { get; set; }

    public event Action? BufferedAmountLow;

    public event Action<ReadOnlyMemory<byte>>? MessageReceived;

    public event Action<string?>? Closed;

    /// <summary>启动读取泵。订阅者挂好之后才调，否则头几条消息会丢。</summary>
    public void Start() => _readPump = Task.Run(() => ReadPumpAsync(_shutdown.Token));

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

        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, message.Length);

        // 整条消息（头 + 体）必须原子地写出去，否则并发发送会让两条消息
        // 的字节交错，对端拿到的就是垃圾
        lock (_writeGate)
        {
            _output.Write(header);
            _output.Write(message);
            _output.Flush();
        }
    }

    public Task WaitForOpenAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task WaitForDrainAsync(long threshold, CancellationToken cancellationToken = default)
    {
        BufferedAmountLow?.Invoke();
        return Task.CompletedTask;
    }

    public Task CloseAsync(string? reason = null)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1)
        {
            return Task.CompletedTask;
        }

        State = DataChannelState.Closed;
        Closed?.Invoke(reason);
        return Task.CompletedTask;
    }

    private async Task ReadPumpAsync(CancellationToken cancellationToken)
    {
        var header = new byte[4];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!await ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false))
                {
                    break;   // 对端关闭了 stdout
                }

                var length = BinaryPrimitives.ReadInt32BigEndian(header);
                if (length < 0 || length > MaxMessageSize)
                {
                    await CloseAsync($"收到非法的消息长度 {length}。").ConfigureAwait(false);
                    return;
                }

                var payload = new byte[length];
                if (!await ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false))
                {
                    break;
                }

                MessageReceived?.Invoke(payload);
            }

            await CloseAsync(null).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            await CloseAsync(ex.Message).ConfigureAwait(false);
        }
    }

    /// <summary>读满整个缓冲区。返回 false 表示流已结束。</summary>
    private async Task<bool> ReadExactlyAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;

        while (offset < buffer.Length)
        {
            var read = await _input
                .ReadAsync(buffer.AsMemory(offset), cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await CloseAsync("已释放").ConfigureAwait(false);

        if (_readPump is not null)
        {
            try
            {
                await _readPump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _shutdown.Dispose();
    }
}
