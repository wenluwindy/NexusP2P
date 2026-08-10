using NexusP2P.Transfer.Protocol;
using NexusP2P.Transport.Abstractions;

namespace NexusP2P.Transfer.Tests.Protocol;

public sealed class MessageAssemblerTests
{
    private static byte[] Frame(MessageType type, int totalLength, int offset, byte[] fragment)
    {
        var buffer = new byte[ProtocolFrame.HeaderSize + fragment.Length];
        ProtocolFrame.Write(buffer, type, totalLength, offset, fragment);
        return buffer;
    }

    private static byte[] Payload(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)((i * 13) & 0xFF);
        }

        return bytes;
    }

    [Fact]
    public void 单帧消息立即完成()
    {
        using var assembler = new MessageAssembler();
        var payload = Payload(50);

        var result = assembler.Feed(Frame(MessageType.Bitfield, 50, 0, payload));

        Assert.NotNull(result);
        Assert.Equal(MessageType.Bitfield, result.Value.Type);
        Assert.Equal(payload, result.Value.Payload.ToArray());
        Assert.False(assembler.HasPartialMessage);
    }

    [Fact]
    public void 空消息立即完成()
    {
        using var assembler = new MessageAssembler();

        var result = assembler.Feed(Frame(MessageType.Complete, 0, 0, []));

        Assert.NotNull(result);
        Assert.Equal(MessageType.Complete, result.Value.Type);
        Assert.Empty(result.Value.Payload.ToArray());
    }

    [Fact]
    public void 多帧消息按顺序重组()
    {
        using var assembler = new MessageAssembler();
        var payload = Payload(300);

        Assert.Null(assembler.Feed(Frame(MessageType.Piece, 300, 0, payload[..100])));
        Assert.True(assembler.HasPartialMessage);

        Assert.Null(assembler.Feed(Frame(MessageType.Piece, 300, 100, payload[100..200])));

        var result = assembler.Feed(Frame(MessageType.Piece, 300, 200, payload[200..]));

        Assert.NotNull(result);
        Assert.Equal(payload, result.Value.Payload.ToArray());
        Assert.False(assembler.HasPartialMessage);
    }

    [Fact]
    public void 连续多条消息互不干扰()
    {
        using var assembler = new MessageAssembler();
        var first = Payload(200);
        var second = Payload(150);

        Assert.Null(assembler.Feed(Frame(MessageType.Manifest, 200, 0, first[..100])));
        var a = assembler.Feed(Frame(MessageType.Manifest, 200, 100, first[100..]));

        Assert.Null(assembler.Feed(Frame(MessageType.Piece, 150, 0, second[..50])));
        var b = assembler.Feed(Frame(MessageType.Piece, 150, 50, second[50..]));

        Assert.Equal(first, a!.Value.Payload.ToArray());
        Assert.Equal(second, b!.Value.Payload.ToArray());
    }

    [Fact]
    public void 缓冲区复用不会污染后续消息()
    {
        // 内部缓冲区是复用的。若返回的是缓冲区的视图而不是副本，
        // 下一条消息就会把上一条的内容改掉 —— 表现为「偶尔收到错乱的数据」。
        using var assembler = new MessageAssembler();

        var big = Payload(400);
        Assert.Null(assembler.Feed(Frame(MessageType.Piece, 400, 0, big[..200])));
        var firstResult = assembler.Feed(Frame(MessageType.Piece, 400, 200, big[200..]));
        var firstCopy = firstResult!.Value.Payload.ToArray();

        var other = new byte[400];
        Array.Fill(other, (byte)0xEE);
        Assert.Null(assembler.Feed(Frame(MessageType.Piece, 400, 0, other[..200])));
        assembler.Feed(Frame(MessageType.Piece, 400, 200, other[200..]));

        Assert.Equal(firstCopy, firstResult.Value.Payload.ToArray());
        Assert.Equal(big, firstCopy);
    }

    // ---- 协议违规 ----

    [Fact]
    public void 首帧偏移不为零被拒绝()
    {
        using var assembler = new MessageAssembler();

        var ex = Assert.Throws<ProtocolException>(
            () => assembler.Feed(Frame(MessageType.Piece, 100, 10, Payload(10))));

        Assert.Contains("首帧偏移", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 重组中途换类型被拒绝()
    {
        // 「一条逻辑消息的帧连续」这个不变式必须强制，
        // 否则乱序的帧会被悄悄拼成形状正确的垃圾数据
        using var assembler = new MessageAssembler();
        assembler.Feed(Frame(MessageType.Piece, 200, 0, Payload(100)));

        var ex = Assert.Throws<ProtocolException>(
            () => assembler.Feed(Frame(MessageType.Manifest, 200, 100, Payload(100))));

        Assert.Contains("必须连续", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 重组中途改变总长被拒绝()
    {
        using var assembler = new MessageAssembler();
        assembler.Feed(Frame(MessageType.Piece, 200, 0, Payload(100)));

        var ex = Assert.Throws<ProtocolException>(
            () => assembler.Feed(Frame(MessageType.Piece, 300, 100, Payload(100))));

        Assert.Contains("总长", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 帧偏移跳空被拒绝()
    {
        using var assembler = new MessageAssembler();
        assembler.Feed(Frame(MessageType.Piece, 300, 0, Payload(100)));

        var ex = Assert.Throws<ProtocolException>(
            () => assembler.Feed(Frame(MessageType.Piece, 300, 200, Payload(100))));

        Assert.Contains("偏移", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 帧偏移倒退被拒绝()
    {
        using var assembler = new MessageAssembler();
        assembler.Feed(Frame(MessageType.Piece, 300, 0, Payload(100)));

        Assert.Throws<ProtocolException>(
            () => assembler.Feed(Frame(MessageType.Piece, 300, 0, Payload(100))));
    }

    [Fact]
    public void 畸形帧被拒绝()
    {
        using var assembler = new MessageAssembler();

        Assert.Throws<ProtocolException>(() => assembler.Feed(new byte[3]));
    }

    [Fact]
    public void 释放后再用会抛异常()
    {
        var assembler = new MessageAssembler();
        assembler.Dispose();

        Assert.Throws<ObjectDisposedException>(
            () => assembler.Feed(Frame(MessageType.Complete, 0, 0, [])));
    }

    // ---- 与 FrameWriter 端到端配合 ----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(1023)]
    [InlineData(1024)]
    [InlineData(1025)]
    [InlineData(100_000)]
    [InlineData(1024 * 1024)]
    public async Task FrameWriter_与_MessageAssembler_往返一致(int length)
    {
        // 分片是 1 MiB 而单条消息上限 256 KiB，所以切帧/重组这条路径
        // 在真实参数下必须走通 —— 这是整个协议层最基本的可用性前提。
        await using var pair = InMemoryDataChannelPair.Create();
        using var assembler = new MessageAssembler();

        var completed = new TaskCompletionSource<AssembledMessage>();
        pair.Right.MessageReceived += frame =>
        {
            var result = assembler.Feed(frame.Span);
            if (result is not null)
            {
                completed.TrySetResult(result.Value);
            }
        };

        var payload = Payload(length);
        await FrameWriter.SendAsync(pair.Left, MessageType.Piece, payload);

        var message = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(MessageType.Piece, message.Type);
        Assert.Equal(payload, message.Payload.ToArray());
    }

    [Fact]
    public async Task 超过逻辑消息上限的载荷被拒绝()
    {
        await using var pair = InMemoryDataChannelPair.Create();

        await Assert.ThrowsAsync<ArgumentException>(
            () => FrameWriter.SendAsync(
                pair.Left, MessageType.Manifest,
                new byte[ProtocolFrame.MaxLogicalMessageSize + 1]));
    }

    [Fact]
    public async Task 大消息在小上限通道上也能切帧()
    {
        // 上限设成 1 KiB，一条 100 KiB 的消息要切成 100 多帧
        await using var pair = InMemoryDataChannelPair.Create(maxMessageSize: 1024);
        using var assembler = new MessageAssembler();

        var frameCount = 0;
        var completed = new TaskCompletionSource<AssembledMessage>();
        pair.Right.MessageReceived += frame =>
        {
            frameCount++;
            var result = assembler.Feed(frame.Span);
            if (result is not null)
            {
                completed.TrySetResult(result.Value);
            }
        };

        var payload = Payload(100_000);
        await FrameWriter.SendAsync(pair.Left, MessageType.Manifest, payload);

        var message = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(payload, message.Payload.ToArray());
        Assert.True(frameCount > 90, $"应切成上百帧，实际 {frameCount} 帧");
    }
}
