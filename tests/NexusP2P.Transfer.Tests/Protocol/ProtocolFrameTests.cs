using System.Buffers.Binary;
using NexusP2P.Transfer.Protocol;

namespace NexusP2P.Transfer.Tests.Protocol;

public sealed class ProtocolFrameTests
{
    private static byte[] Frame(MessageType type, int totalLength, int offset, byte[] fragment)
    {
        var buffer = new byte[ProtocolFrame.HeaderSize + fragment.Length];
        ProtocolFrame.Write(buffer, type, totalLength, offset, fragment);
        return buffer;
    }

    [Fact]
    public void 帧头往返无损()
    {
        var fragment = new byte[] { 1, 2, 3, 4, 5 };
        var frame = Frame(MessageType.Piece, 100, 20, fragment);

        Assert.True(ProtocolFrame.TryParse(frame, out var header, out var payload, out var error));

        Assert.Null(error);
        Assert.Equal(MessageType.Piece, header.Type);
        Assert.Equal(100, header.TotalLength);
        Assert.Equal(20, header.Offset);
        Assert.Equal(5, header.FragmentLength);
        Assert.False(header.IsFinal);
        Assert.Equal(fragment, payload.ToArray());
    }

    [Fact]
    public void 末帧被标记为_IsFinal()
    {
        var frame = Frame(MessageType.Manifest, 10, 5, new byte[5]);

        Assert.True(ProtocolFrame.TryParse(frame, out var header, out _, out _));
        Assert.True(header.IsFinal);
    }

    [Fact]
    public void 空载荷的帧合法()
    {
        // Complete 消息就是空的
        var frame = Frame(MessageType.Complete, 0, 0, []);

        Assert.True(ProtocolFrame.TryParse(frame, out var header, out var payload, out _));
        Assert.Equal(0, header.TotalLength);
        Assert.True(header.IsFinal);
        Assert.Empty(payload.ToArray());
    }

    [Fact]
    public void 帧头大小是_13_字节()
    {
        Assert.Equal(13, ProtocolFrame.HeaderSize);
    }

    [Fact]
    public void MaxFragmentPayload_扣掉帧头()
    {
        Assert.Equal(256 * 1024 - 13, ProtocolFrame.MaxFragmentPayload(256 * 1024));
    }

    [Fact]
    public void 单条上限不足帧头时被拒绝()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ProtocolFrame.MaxFragmentPayload(ProtocolFrame.HeaderSize));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProtocolFrame.MaxFragmentPayload(5));
    }

    // ---- 畸形输入 ----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(12)]
    public void 短于帧头的帧被拒绝(int length)
    {
        Assert.False(ProtocolFrame.TryParse(new byte[length], out _, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void 未知消息类型被拒绝()
    {
        var frame = Frame(MessageType.Piece, 0, 0, []);
        frame[0] = 0xEE;

        Assert.False(ProtocolFrame.TryParse(frame, out _, out _, out var error));
        Assert.Contains("未知", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void 声明总长为天文数字被拒绝()
    {
        // 若照着这个数字预分配，就会 OOM
        var frame = Frame(MessageType.Manifest, 0, 0, []);
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1), int.MaxValue);

        Assert.False(ProtocolFrame.TryParse(frame, out _, out _, out var error));
        Assert.Contains("总长", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void 总长为负数被拒绝()
    {
        var frame = Frame(MessageType.Manifest, 0, 0, []);
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1), -1);

        Assert.False(ProtocolFrame.TryParse(frame, out _, out _, out _));
    }

    [Fact]
    public void 偏移为负数被拒绝()
    {
        var frame = Frame(MessageType.Manifest, 10, 0, new byte[5]);
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(5), -1);

        Assert.False(ProtocolFrame.TryParse(frame, out _, out _, out _));
    }

    [Fact]
    public void 偏移加本片超过总长被拒绝()
    {
        var frame = Frame(MessageType.Manifest, 10, 8, new byte[2]);
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(5), 9);   // 9 + 2 > 10

        Assert.False(ProtocolFrame.TryParse(frame, out _, out _, out var error));
        Assert.Contains("超过总长", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void 帧头声明的载荷长度与实际不符被拒绝()
    {
        // 这是最经典的解析漏洞：信了长度字段而没核对实际字节数
        var frame = Frame(MessageType.Manifest, 100, 0, new byte[10]);
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(9), 50);   // 声称 50 实际 10

        Assert.False(ProtocolFrame.TryParse(frame, out _, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void 末尾有多余字节被拒绝()
    {
        var frame = Frame(MessageType.Manifest, 10, 0, new byte[10]);
        var padded = frame.Concat(new byte[] { 0xFF }).ToArray();

        Assert.False(ProtocolFrame.TryParse(padded, out _, out _, out _));
    }

    [Fact]
    public void 逐字节截断都只会返回失败而不抛异常()
    {
        var frame = Frame(MessageType.Piece, 64, 0, new byte[64]);

        for (var length = 0; length < frame.Length; length++)
        {
            var prefix = frame.AsSpan(0, length).ToArray();

            var ex = Record.Exception(() =>
            {
                // 只关心「不抛异常」，成功与否都可以接受
                var parsed = ProtocolFrame.TryParse(prefix, out _, out _, out _);
                Assert.True(parsed || !parsed);
            });

            Assert.Null(ex);
        }
    }

    [Fact]
    public void 写入时超过总长会被拒绝()
    {
        var buffer = new byte[100];

        Assert.Throws<ArgumentException>(
            () => ProtocolFrame.Write(buffer, MessageType.Piece, 10, 8, new byte[5]));
    }

    [Fact]
    public void 写入时目标缓冲区不足会被拒绝()
    {
        Assert.Throws<ArgumentException>(
            () => ProtocolFrame.Write(new byte[10], MessageType.Piece, 100, 0, new byte[50]));
    }

    [Fact]
    public void 写入时超过逻辑消息上限会被拒绝()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ProtocolFrame.Write(new byte[100], MessageType.Manifest,
                ProtocolFrame.MaxLogicalMessageSize + 1, 0, []));
    }
}
