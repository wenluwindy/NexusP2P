using System.Buffers.Binary;
using System.Text;
using NexusP2P.Core.Crypto;

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

/// <summary>
/// 密钥要约（V3）：本次传输的 32 字节密钥材料，由发送方在通道建立后首先推送。
///
/// <para>载荷就是裸的 32 字节，没有任何头部 —— 长度是固定的，
/// 而一个可变长度字段只会给攻击者多一个可以撒谎的地方。</para>
///
/// <para><b>不做任何混淆或二次加密。</b>这条消息的机密性完全依赖
/// WebRTC 的 DTLS 层。在明文通道上自己加一层「看起来像加密」的东西，
/// 只会让人误以为它比实际更安全 —— 密钥总得有个源头，
/// 而真正的防线在 <see cref="MessageType.KeyOffer"/> 的注释里说明。</para>
/// </summary>
public readonly record struct KeyOfferPayload(TransferSecret Secret)
{
    /// <summary>载荷字节数，恒为密钥材料的长度。</summary>
    public const int Size = TransferSecret.Size;

    public byte[] Serialize() => Secret.ToArray();

    public static KeyOfferPayload Parse(ReadOnlySpan<byte> payload)
    {
        // 长度必须精确匹配。多一个字节都当协议违规处理 ——
        // 「宽容地只取前 32 字节」会让实现分歧静默地变成解密失败，
        // 而那种失败在现场看起来像是「文件码不对」，极难排查。
        if (payload.Length != Size)
        {
            throw new ProtocolException(
                $"KeyOffer 消息必须是 {Size} 字节，实际为 {payload.Length} 字节。");
        }

        return new KeyOfferPayload(new TransferSecret(payload));
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
