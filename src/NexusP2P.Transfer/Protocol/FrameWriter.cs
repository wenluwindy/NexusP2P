using System.Buffers;
using NexusP2P.Transport.Abstractions;

namespace NexusP2P.Transfer.Protocol;

/// <summary>
/// 把一条逻辑消息切成帧投递出去，并在投递过程中遵守背压。
/// </summary>
public static class FrameWriter
{
    /// <summary>
    /// 投递一条逻辑消息。超过单条上限时自动切帧，帧与帧之间连续 ——
    /// 这正是 <see cref="MessageAssembler"/> 依赖的不变式。
    /// </summary>
    /// <param name="highWaterMark">
    /// 缓冲超过这个值就先等排空。0 表示不做背压（只适合小的控制消息）。
    /// </param>
    public static async Task SendAsync(
        IDataChannel channel,
        MessageType type,
        ReadOnlyMemory<byte> payload,
        long highWaterMark = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);

        if (payload.Length > ProtocolFrame.MaxLogicalMessageSize)
        {
            throw new ArgumentException(
                $"逻辑消息 {payload.Length} 字节超过上限 {ProtocolFrame.MaxLogicalMessageSize} 字节。",
                nameof(payload));
        }

        var maxFragment = ProtocolFrame.MaxFragmentPayload(channel.MaxMessageSize);
        var offset = 0;

        // 空载荷也要发一帧（Complete 消息就是空的），所以用 do-while
        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (highWaterMark > 0 && channel.BufferedAmount > highWaterMark)
            {
                await channel.WaitForDrainAsync(highWaterMark / 2, cancellationToken).ConfigureAwait(false);
            }

            var take = Math.Min(maxFragment, payload.Length - offset);
            var frameSize = ProtocolFrame.HeaderSize + take;
            var buffer = ArrayPool<byte>.Shared.Rent(frameSize);

            try
            {
                var written = ProtocolFrame.Write(
                    buffer, type, payload.Length, offset, payload.Span.Slice(offset, take));

                channel.Send(buffer.AsSpan(0, written));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            offset += take;
        }
        while (offset < payload.Length);
    }
}
