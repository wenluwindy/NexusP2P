using System.Threading.Channels;
using NexusP2P.Transport.Abstractions;

namespace NexusP2P.Transfer.Protocol;

/// <summary>
/// 把 <see cref="IDataChannel"/> 的事件式接口包成可 await 的收发。
///
/// <para><b>为什么需要这一层</b>：<c>MessageReceived</c> 是回调，而协议逻辑是
/// 「发清单 → 等位图 → 发分片 → 等完成」这样的顺序流程。用回调写状态机会
/// 变成一团互相设置标志位的代码；转成一个可 await 的队列之后，
/// 收发两端的逻辑都能写成直白的 async 方法。</para>
///
/// <para>回调里只做一件事：喂给重组器、把完整消息塞进队列。
/// 重活留给消费方 —— 在传输回调里做重活会拖慢整个通道。</para>
/// </summary>
public sealed class ProtocolConnection : IAsyncDisposable
{
    private readonly IDataChannel _channel;
    private readonly MessageAssembler _assembler = new();
    // 必须写全名：本类型有个叫 Channel 的属性，会把 Channel.CreateUnbounded 解析成它
    private readonly Channel<AssembledMessage> _inbound =
        System.Threading.Channels.Channel.CreateUnbounded<AssembledMessage>(
            new UnboundedChannelOptions { SingleWriter = true });

    private readonly bool _ownsChannel;
    private bool _disposed;

    public ProtocolConnection(IDataChannel channel, bool ownsChannel = false)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _ownsChannel = ownsChannel;

        _channel.MessageReceived += OnMessageReceived;
        _channel.Closed += OnClosed;
    }

    public IDataChannel Channel => _channel;

    /// <summary>投递分片时的缓冲高水位。默认 4 MiB。</summary>
    public long HighWaterMark { get; init; } = 4L * 1024 * 1024;

    /// <summary>
    /// 发错误通知这件事最多花多久。错误通知是给对端的一份好意，
    /// 不值得为它把自己挂住 —— 挂住之后连本地的错误都报不出来。
    /// </summary>
    private static readonly TimeSpan ErrorNotifyTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 收下一条完整的逻辑消息。通道关闭且队列已空时抛
    /// <see cref="DataChannelClosedException"/>。
    /// </summary>
    public async Task<AssembledMessage> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            return await _inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            // 队列被 Complete 说明底层通道已关。若关闭时带了原因，
            // 它已经被塞成 Complete 的异常，这里会作为 InnerException 出现。
            throw new DataChannelClosedException("等待消息时通道已关闭。");
        }
    }

    public Task SendAsync(
        MessageType type,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return FrameWriter.SendAsync(_channel, type, payload, HighWaterMark, cancellationToken);
    }

    /// <summary>
    /// 发一条错误通知，然后关闭。发送失败时忽略 —— 通道可能已经断了。
    ///
    /// <para><b>整件事都有时限。</b>这是错误路径：对端可能已经不读了，
    /// 而 <c>WaitForDrainAsync</c> 会一直等到真的排空为止。
    /// 在这里无限期等下去，等于把「告诉对方出了什么错」变成「自己也卡死」。</para>
    /// </summary>
    public async Task SendErrorAndCloseAsync(TransferErrorCode code, string message)
    {
        using var deadline = new CancellationTokenSource(ErrorNotifyTimeout);

        try
        {
            await SendAsync(MessageType.Error, new ErrorPayload(code, message).Serialize(), deadline.Token)
                .ConfigureAwait(false);
            await _channel.WaitForDrainAsync(0, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException
                                       or DataChannelClosedException)
        {
            // 通道已经不可用，没别的办法可想
        }

        await _channel.CloseAsync(message).ConfigureAwait(false);
    }

    private void OnMessageReceived(ReadOnlyMemory<byte> frame)
    {
        try
        {
            var message = _assembler.Feed(frame.Span);
            if (message is not null)
            {
                _inbound.Writer.TryWrite(message.Value);
            }
        }
        catch (Exception ex)
        {
            // 协议违规无法在回调里妥善处理，把它带给消费方，
            // 由它决定发 Error 还是直接断开。
            _inbound.Writer.TryComplete(ex);
        }
    }

    private void OnClosed(string? reason)
    {
        _inbound.Writer.TryComplete(
            reason is null ? null : new DataChannelClosedException(reason));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _channel.MessageReceived -= OnMessageReceived;
        _channel.Closed -= OnClosed;
        _inbound.Writer.TryComplete();
        _assembler.Dispose();

        if (_ownsChannel)
        {
            await _channel.DisposeAsync().ConfigureAwait(false);
        }
    }
}
