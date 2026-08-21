using System.Buffers.Binary;

namespace NexusP2P.Transfer.Protocol;

/// <summary>逻辑消息的类型。</summary>
public enum MessageType : byte
{
    /// <summary>发送方 → 接收方：传输清单。</summary>
    Manifest = 0x01,

    /// <summary>接收方 → 发送方：本地已有哪些分片（断点续传的依据）。</summary>
    Bitfield = 0x02,

    /// <summary>发送方 → 接收方：一个分片的密文。</summary>
    Piece = 0x03,

    /// <summary>接收方 → 发送方：全部分片已收齐并校验通过。</summary>
    Complete = 0x04,

    /// <summary>任一方：出错并即将关闭。</summary>
    Error = 0x05,

    /// <summary>
    /// 发送方 → 接收方：本轮请求的分片都发完了。
    ///
    /// <para>没有这条消息就会死锁：接收方拒收某个分片（校验不过）是静默的，
    /// 发送方不知道要重发，于是接收方等分片、发送方等完成通知，两边各等各的。
    /// 有了轮次边界，接收方可以在一轮结束后重发位图请求剩下的部分。</para>
    /// </summary>
    PushComplete = 0x06,

    /// <summary>
    /// 发送方 → 接收方：本次传输的 32 字节密钥材料（V3 的首条消息）。
    ///
    /// <para><b>这条消息把「文件码 + 密钥」缩成了「只要文件码」。</b>
    /// 密钥不再走 URL fragment 由用户转述，而是在 WebRTC 的 DTLS
    /// 通道建立之后由发送方直接推过来。</para>
    ///
    /// <para><b>威胁模型因此发生了变化，必须说清楚</b>：V1/V2 里密钥在
    /// fragment 中，信令服务器<b>从密码学上</b>无法解密任何字节；V3 里
    /// 信令服务器若<b>主动</b>在 SDP 交换阶段做中间人，就能拿到密钥。
    /// 也就是从「服务器无能为力」退化为「服务器不主动作恶即安全」。
    /// 被动记录流量的服务器仍然什么都拿不到 —— 载荷在 DTLS 里。</para>
    /// </summary>
    KeyOffer = 0x07,
}

/// <summary>帧头。</summary>
public readonly record struct FrameHeader(
    MessageType Type,
    int TotalLength,
    int Offset,
    int FragmentLength)
{
    public bool IsFinal => Offset + FragmentLength == TotalLength;
}

/// <summary>
/// 帧的编解码。
///
/// <para><b>为什么需要分片机制</b>：默认分片是 1 MiB，而 DataChannel 单条消息
/// 的跨浏览器安全上限只有 256 KiB。所以一条逻辑消息必须拆成多个帧传。
/// 大文件夹的清单（20 GiB 约 640 KiB 的分片根）也一样。</para>
///
/// <para><b>关键不变式</b>：一条逻辑消息的各个帧在链路上是<b>连续的</b>，
/// 不与其他逻辑消息交错。这由「通道有序可靠」+「发送方不交错投递」共同保证，
/// 使得接收侧只需要一个重组槽位而不是一张表。
/// <see cref="MessageAssembler"/> 会强制校验这一点 ——
/// 一旦对端实现打破它，立刻报错而不是悄悄拼出错误的数据。</para>
///
/// <para><b>不用 JSON</b>：20 GiB 传输下 JSON 的编解码开销与体积都不可接受。</para>
/// </summary>
public static class ProtocolFrame
{
    /// <summary>帧头字节数：类型(1) + 总长(4) + 偏移(4) + 本片长度(4)。</summary>
    public const int HeaderSize = 1 + 4 + 4 + 4;

    /// <summary>
    /// 单条逻辑消息的长度上限（8 MiB）。
    /// 上界必须存在且在分配之前校验 —— 否则对端只要声称一个天文数字的总长，
    /// 就能让我们 OOM。8 MiB 足够容纳最大的清单与一个分片的密文。
    /// </summary>
    public const int MaxLogicalMessageSize = 8 * 1024 * 1024;

    /// <summary>给定单条消息上限，一个帧最多能装多少载荷。</summary>
    public static int MaxFragmentPayload(int maxMessageSize)
    {
        if (maxMessageSize <= HeaderSize)
        {
            throw new ArgumentOutOfRangeException(nameof(maxMessageSize), maxMessageSize,
                $"单条消息上限必须大于帧头 {HeaderSize} 字节。");
        }

        return maxMessageSize - HeaderSize;
    }

    /// <summary>写一个帧，返回写入的字节数。</summary>
    public static int Write(
        Span<byte> destination,
        MessageType type,
        int totalLength,
        int offset,
        ReadOnlySpan<byte> fragment)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalLength);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(totalLength, MaxLogicalMessageSize);

        if (offset + fragment.Length > totalLength)
        {
            throw new ArgumentException(
                $"偏移 {offset} 加本片 {fragment.Length} 超过总长 {totalLength}。", nameof(fragment));
        }

        var required = HeaderSize + fragment.Length;
        if (destination.Length < required)
        {
            throw new ArgumentException(
                $"目标缓冲区需要 {required} 字节，实际只有 {destination.Length} 字节。", nameof(destination));
        }

        destination[0] = (byte)type;
        BinaryPrimitives.WriteInt32BigEndian(destination[1..], totalLength);
        BinaryPrimitives.WriteInt32BigEndian(destination[5..], offset);
        BinaryPrimitives.WriteInt32BigEndian(destination[9..], fragment.Length);
        fragment.CopyTo(destination[HeaderSize..]);

        return required;
    }

    /// <summary>
    /// 解析帧头并切出载荷。<paramref name="frame"/> <b>是不可信输入</b> ——
    /// 所有字段都校验过才返回。
    /// </summary>
    public static bool TryParse(
        ReadOnlySpan<byte> frame,
        out FrameHeader header,
        out ReadOnlySpan<byte> payload,
        out string? error)
    {
        header = default;
        payload = default;

        if (frame.Length < HeaderSize)
        {
            error = $"帧只有 {frame.Length} 字节，不足帧头 {HeaderSize} 字节。";
            return false;
        }

        var rawType = frame[0];
        if (!Enum.IsDefined(typeof(MessageType), rawType))
        {
            error = $"未知的消息类型 0x{rawType:X2}。";
            return false;
        }

        var totalLength = BinaryPrimitives.ReadInt32BigEndian(frame[1..]);
        var offset = BinaryPrimitives.ReadInt32BigEndian(frame[5..]);
        var fragmentLength = BinaryPrimitives.ReadInt32BigEndian(frame[9..]);

        if (totalLength < 0 || totalLength > MaxLogicalMessageSize)
        {
            error = $"声明的总长 {totalLength} 不在 0~{MaxLogicalMessageSize} 之间。";
            return false;
        }

        if (offset < 0 || fragmentLength < 0)
        {
            error = $"偏移 {offset} 或本片长度 {fragmentLength} 为负数。";
            return false;
        }

        if (offset > totalLength || fragmentLength > totalLength - offset)
        {
            error = $"偏移 {offset} 加本片 {fragmentLength} 超过总长 {totalLength}。";
            return false;
        }

        if (frame.Length != HeaderSize + fragmentLength)
        {
            error = $"帧实际 {frame.Length} 字节，但帧头声明载荷 {fragmentLength} 字节。";
            return false;
        }

        header = new FrameHeader((MessageType)rawType, totalLength, offset, fragmentLength);
        payload = frame[HeaderSize..];
        error = null;
        return true;
    }
}

/// <summary>协议层面的错误：帧畸形、消息内容不合法、或对端违反了协议约定。</summary>
public sealed class ProtocolException(string message) : Exception(message);
