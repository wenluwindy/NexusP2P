using System.Buffers.Binary;
using System.Text;

namespace NexusP2P.Transfer.Protocol;

/// <summary>
/// 一个分片的密文，附带它在传输里的位置。
///
/// <para>位置必须随密文一起传：加密的 nonce 就是由
/// <c>(文件序号, 分片序号)</c> 派生的，接收方少了位置就解不开 ——
/// 这同时也让「把密文挪到别的位置」这种攻击自动失效。</para>
/// </summary>
public readonly record struct PiecePayload(int FileIndex, long PieceIndex, ReadOnlyMemory<byte> Ciphertext)
{
    /// <summary>位置头的字节数：文件序号(4) + 分片序号(8)。</summary>
    public const int HeaderSize = 4 + 8;

    public byte[] Serialize()
    {
        var result = new byte[HeaderSize + Ciphertext.Length];
        BinaryPrimitives.WriteInt32BigEndian(result, FileIndex);
        BinaryPrimitives.WriteInt64BigEndian(result.AsSpan(4), PieceIndex);
        Ciphertext.Span.CopyTo(result.AsSpan(HeaderSize));
        return result;
    }

    public static PiecePayload Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HeaderSize)
        {
            throw new ProtocolException(
                $"Piece 消息只有 {payload.Length} 字节，不足位置头 {HeaderSize} 字节。");
        }

        var fileIndex = BinaryPrimitives.ReadInt32BigEndian(payload);
        var pieceIndex = BinaryPrimitives.ReadInt64BigEndian(payload[4..]);

        if (fileIndex < 0)
        {
            throw new ProtocolException($"Piece 消息里的文件序号为负数：{fileIndex}。");
        }

        if (pieceIndex < 0)
        {
            throw new ProtocolException($"Piece 消息里的分片序号为负数：{pieceIndex}。");
        }

        return new PiecePayload(fileIndex, pieceIndex, payload[HeaderSize..].ToArray());
    }
}

/// <summary>出错原因。取值稳定，便于两端与日志对照。</summary>
public enum TransferErrorCode : ushort
{
    Unknown = 0,

    /// <summary>清单不合法或含不安全路径。</summary>
    InvalidManifest = 1,

    /// <summary>分片校验失败次数过多。</summary>
    PieceVerificationFailed = 2,

    /// <summary>本地磁盘空间不足。</summary>
    InsufficientDiskSpace = 3,

    /// <summary>目标目录不可写。</summary>
    DestinationNotWritable = 4,

    /// <summary>对端违反了协议约定。</summary>
    ProtocolViolation = 5,

    /// <summary>用户主动取消。</summary>
    Cancelled = 6,
}

/// <summary>出错通知。发出后即关闭连接。</summary>
public readonly record struct ErrorPayload(TransferErrorCode Code, string Message)
{
    /// <summary>错误文本上限。对端给的字符串是不可信输入，不能让它无界。</summary>
    public const int MaxMessageBytes = 4096;

    public byte[] Serialize()
    {
        var text = Encoding.UTF8.GetBytes(Message);
        if (text.Length > MaxMessageBytes)
        {
            text = text.AsSpan(0, MaxMessageBytes).ToArray();
        }

        var result = new byte[sizeof(ushort) + text.Length];
        BinaryPrimitives.WriteUInt16BigEndian(result, (ushort)Code);
        text.CopyTo(result.AsSpan(sizeof(ushort)));
        return result;
    }

    public static ErrorPayload Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < sizeof(ushort))
        {
            throw new ProtocolException($"Error 消息只有 {payload.Length} 字节，不足错误码。");
        }

        var rawCode = BinaryPrimitives.ReadUInt16BigEndian(payload);
        var textBytes = payload[sizeof(ushort)..];

        if (textBytes.Length > MaxMessageBytes)
        {
            throw new ProtocolException(
                $"Error 消息文本 {textBytes.Length} 字节超过上限 {MaxMessageBytes}。");
        }

        // 未知错误码不算协议违规 —— 对端可能是更新的版本。归到 Unknown 并保留原文。
        var code = Enum.IsDefined(typeof(TransferErrorCode), rawCode)
            ? (TransferErrorCode)rawCode
            : TransferErrorCode.Unknown;

        return new ErrorPayload(code, Encoding.UTF8.GetString(textBytes));
    }
}
