using NexusP2P.Transfer.Protocol;

namespace NexusP2P.Transfer.Tests.Protocol;

public sealed class MessagesTests
{
    // ---- PiecePayload ----

    [Fact]
    public void PiecePayload_往返无损()
    {
        var ciphertext = new byte[] { 9, 8, 7, 6, 5 };
        var original = new PiecePayload(3, 1234567890123L, ciphertext);

        var parsed = PiecePayload.Parse(original.Serialize());

        Assert.Equal(3, parsed.FileIndex);
        Assert.Equal(1234567890123L, parsed.PieceIndex);
        Assert.Equal(ciphertext, parsed.Ciphertext.ToArray());
    }

    [Fact]
    public void PiecePayload_空密文也能往返()
    {
        var parsed = PiecePayload.Parse(new PiecePayload(0, 0, ReadOnlyMemory<byte>.Empty).Serialize());

        Assert.Empty(parsed.Ciphertext.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(11)]
    public void PiecePayload_短于位置头被拒绝(int length)
    {
        Assert.Throws<ProtocolException>(() => PiecePayload.Parse(new byte[length]));
    }

    [Fact]
    public void PiecePayload_负的文件序号被拒绝()
    {
        var payload = new PiecePayload(0, 0, new byte[4]).Serialize();
        payload[0] = 0xFF;   // 文件序号最高位置 1 -> 负数

        Assert.Throws<ProtocolException>(() => PiecePayload.Parse(payload));
    }

    [Fact]
    public void PiecePayload_负的分片序号被拒绝()
    {
        var payload = new PiecePayload(0, 0, new byte[4]).Serialize();
        payload[4] = 0xFF;

        Assert.Throws<ProtocolException>(() => PiecePayload.Parse(payload));
    }

    [Fact]
    public void PiecePayload_位置头是大端且文件序号在前()
    {
        var serialized = new PiecePayload(
            0x01020304, 0x0A0B0C0D0E0F1011L, ReadOnlyMemory<byte>.Empty).Serialize();

        Assert.Equal(
            new byte[] { 0x01, 0x02, 0x03, 0x04, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10, 0x11 },
            serialized);
    }

    // ---- ErrorPayload ----

    [Fact]
    public void ErrorPayload_往返无损()
    {
        var original = new ErrorPayload(TransferErrorCode.InsufficientDiskSpace, "磁盘只剩 200 MB");

        var parsed = ErrorPayload.Parse(original.Serialize());

        Assert.Equal(TransferErrorCode.InsufficientDiskSpace, parsed.Code);
        Assert.Equal("磁盘只剩 200 MB", parsed.Message);
    }

    [Fact]
    public void ErrorPayload_空文本可以()
    {
        var parsed = ErrorPayload.Parse(new ErrorPayload(TransferErrorCode.Cancelled, "").Serialize());

        Assert.Equal(TransferErrorCode.Cancelled, parsed.Code);
        Assert.Equal("", parsed.Message);
    }

    [Fact]
    public void ErrorPayload_未知错误码归到_Unknown_但保留原文()
    {
        // 对端可能是更新的版本，用了我们还不认识的错误码。
        // 那不是协议违规 —— 硬要报错反而会掩盖对端真正想说的事。
        var payload = new ErrorPayload(TransferErrorCode.Unknown, "未来的错误").Serialize();
        payload[0] = 0xFF;
        payload[1] = 0xFF;

        var parsed = ErrorPayload.Parse(payload);

        Assert.Equal(TransferErrorCode.Unknown, parsed.Code);
        Assert.Equal("未来的错误", parsed.Message);
    }

    [Fact]
    public void ErrorPayload_序列化时截断超长文本()
    {
        var huge = new string('x', ErrorPayload.MaxMessageBytes * 2);

        var serialized = new ErrorPayload(TransferErrorCode.Unknown, huge).Serialize();

        Assert.Equal(sizeof(ushort) + ErrorPayload.MaxMessageBytes, serialized.Length);
    }

    [Fact]
    public void ErrorPayload_解析时拒绝超长文本()
    {
        // 对端给的字符串是不可信输入，不能让它无界
        var payload = new byte[sizeof(ushort) + ErrorPayload.MaxMessageBytes + 1];

        Assert.Throws<ProtocolException>(() => ErrorPayload.Parse(payload));
    }

    [Fact]
    public void ErrorPayload_短于错误码被拒绝()
    {
        Assert.Throws<ProtocolException>(() => ErrorPayload.Parse([0x01]));
    }
}
