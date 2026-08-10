namespace NexusP2P.Transport.Abstractions;

/// <summary>
/// 内存通道的故障注入配置。
///
/// <para>这是 AD-1 的核心收益之一：<b>续传逻辑在内存管道上比接真网更好测</b>。
/// 真实网络里「传到 40% 时断开」很难精确复现，这里只要设一个字节数。</para>
/// </summary>
public sealed record FaultProfile
{
    /// <summary>不注入任何故障。</summary>
    public static FaultProfile None { get; } = new();

    /// <summary>
    /// 发送这么多字节之后强制断开。null 表示不断开。
    /// 用来精确复现「传到某个位置断线」。
    /// </summary>
    public long? DisconnectAfterBytes { get; init; }

    /// <summary>
    /// 发送这么多条消息之后强制断开。null 表示不断开。
    /// 比按字节更适合针对「第 N 条协议消息」的场景。
    /// </summary>
    public long? DisconnectAfterMessages { get; init; }

    /// <summary>
    /// 每条消息的投递延迟。用来模拟 RTT，让背压逻辑真的有机会触发 ——
    /// 零延迟的管道会让「缓冲堆积」这条路径永远走不到。
    /// </summary>
    public TimeSpan DeliveryDelay { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// 排空速率（字节/秒）。null 表示瞬间排空。
    /// 设成有限值才能测出背压是否真的在压。
    /// </summary>
    public long? DrainBytesPerSecond { get; init; }

    /// <summary>断开时给出的原因，便于测试断言。</summary>
    public string DisconnectReason { get; init; } = "故障注入：模拟连接中断";

    /// <summary>
    /// 把第几条消息（从 1 开始计）在投递前损坏掉。
    ///
    /// <para>损坏的是<b>末字节</b>：对 Piece 消息来说那正是 AES-GCM 的认证标签，
    /// 所以帧本身仍能正常解析，但分片必定校验不通过 ——
    /// 这才是用来验证「拒收之后能重传」的正确刺激，
    /// 破坏帧头只会测到帧解析而测不到重传。</para>
    ///
    /// <para>WebRTC 本身可靠有序，实际不会出现在途损坏；这个开关针对的是
    /// 「对端有 bug 或有恶意」的场景，以及避免退回死锁。</para>
    /// </summary>
    public IReadOnlySet<long>? CorruptMessageOrdinals { get; init; }

    public static FaultProfile DisconnectAfter(long bytes) =>
        new() { DisconnectAfterBytes = bytes };

    public static FaultProfile Throttled(long bytesPerSecond, TimeSpan delay) =>
        new() { DrainBytesPerSecond = bytesPerSecond, DeliveryDelay = delay };
}
