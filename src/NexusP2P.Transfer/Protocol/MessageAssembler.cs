using System.Buffers;

namespace NexusP2P.Transfer.Protocol;

/// <summary>一条重组完成的逻辑消息。<see cref="Payload"/> 仅在回调期间有效。</summary>
public readonly record struct AssembledMessage(MessageType Type, ReadOnlyMemory<byte> Payload);

/// <summary>
/// 把帧重组成逻辑消息。
///
/// <para>依赖 <see cref="ProtocolFrame"/> 里说明的不变式：一条逻辑消息的各帧
/// 在链路上连续。所以只需要<b>一个</b>重组槽位。这不是偷懒 ——
/// 它让「对端乱序发帧」这种协议违规能被立刻发现，
/// 而一张任意键的重组表会把这类错误悄悄拼成正确形状的垃圾数据。</para>
/// </summary>
public sealed class MessageAssembler : IDisposable
{
    private byte[]? _buffer;
    private MessageType _type;
    private int _totalLength;
    private int _received;
    private bool _inProgress;
    private bool _disposed;

    /// <summary>
    /// 喂入一个帧。返回重组完成的消息，未完成则返回 null。
    /// </summary>
    /// <exception cref="ProtocolException">帧畸形或违反连续性约定。</exception>
    public AssembledMessage? Feed(ReadOnlySpan<byte> frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!ProtocolFrame.TryParse(frame, out var header, out var payload, out var error))
        {
            throw new ProtocolException($"帧不合法：{error}");
        }

        if (!_inProgress)
        {
            if (header.Offset != 0)
            {
                throw new ProtocolException(
                    $"{header.Type} 消息的首帧偏移应为 0，实际为 {header.Offset}。");
            }

            // 单帧就是完整消息时不必进重组缓冲，直接返回，省一次拷贝。
            // 这是绝大多数控制消息（Bitfield、Complete、Error）走的路径。
            if (header.IsFinal)
            {
                return new AssembledMessage(header.Type, payload.ToArray());
            }

            Begin(header);
        }
        else
        {
            if (header.Type != _type)
            {
                throw new ProtocolException(
                    $"{_type} 消息重组中却收到 {header.Type} 帧；一条逻辑消息的帧必须连续。");
            }

            if (header.TotalLength != _totalLength)
            {
                throw new ProtocolException(
                    $"同一条消息的总长前后不一致：先前 {_totalLength}，现在 {header.TotalLength}。");
            }

            if (header.Offset != _received)
            {
                throw new ProtocolException(
                    $"{_type} 消息的帧偏移应为 {_received}，实际为 {header.Offset}；不允许乱序或跳空。");
            }
        }

        payload.CopyTo(_buffer.AsSpan(_received));
        _received += payload.Length;

        if (_received < _totalLength)
        {
            return null;
        }

        var complete = new AssembledMessage(_type, _buffer.AsMemory(0, _totalLength).ToArray());
        Reset();
        return complete;
    }

    /// <summary>是否正在重组一条尚未完整的消息。连接中断时用来判断状态是否干净。</summary>
    public bool HasPartialMessage => _inProgress;

    private void Begin(FrameHeader header)
    {
        _type = header.Type;
        _totalLength = header.TotalLength;
        _received = 0;
        _inProgress = true;

        if (_buffer is null || _buffer.Length < _totalLength)
        {
            if (_buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(_buffer);
            }

            // 总长在 TryParse 里已经校验过不超过 MaxLogicalMessageSize，
            // 所以这里的分配是有界的。
            _buffer = ArrayPool<byte>.Shared.Rent(_totalLength);
        }
    }

    private void Reset()
    {
        _inProgress = false;
        _received = 0;
        _totalLength = 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = null;
        }
    }
}
