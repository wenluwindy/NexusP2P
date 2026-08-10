namespace NexusP2P.Transport.Abstractions;

/// <summary>通道状态。取值刻意与 WebRTC 的 <c>RTCDataChannelState</c> 对齐。</summary>
public enum DataChannelState
{
    Connecting,
    Open,
    Closing,
    Closed,
}

/// <summary>
/// 消息式、有序、带背压的双向通道。
///
/// <para><b>这个抽象刻意贴合 WebRTC DataChannel 的语义</b>，而不是设计成
/// 更方便的流式接口。原因是它最终要由 WebRTC 实现，如果抽象比底层更宽松，
/// 上层就会依赖底层给不了的保证 —— 到接真网时才发现，代价高得多。</para>
///
/// <para>具体地说：</para>
/// <list type="bullet">
/// <item><b>消息式而非流式</b>：发出去的一条消息，对端原样收到一条，
/// 不会被合并或拆分。所以上层不需要自己做分帧 —— 但也不能指望
/// 发送半条消息。</item>
/// <item><b>有序且可靠</b>：对应 WebRTC 的 ordered + reliable 模式。</item>
/// <item><b>有 <see cref="BufferedAmount"/> 背压</b>：发送是同步入队的，
/// 真正的发送速率由底层决定。上层必须盯着这个值调节投递速度，
/// 否则内存会无界增长。SIPSorcery 的 spike 已经证明这不是理论风险。</item>
/// <item><b>有 <see cref="MaxMessageSize"/> 上限</b>：浏览器跨实现的安全值
/// 是 256 KiB，所以分片必须自己切成多条消息。</item>
/// </list>
/// </summary>
public interface IDataChannel : IAsyncDisposable
{
    DataChannelState State { get; }

    /// <summary>单条消息的字节数上限。</summary>
    int MaxMessageSize { get; }

    /// <summary>已入队但尚未发出的字节数。上层据此做背压。</summary>
    long BufferedAmount { get; }

    /// <summary>
    /// <see cref="BufferedAmount"/> 从高位回落到这个阈值以下时触发
    /// <see cref="BufferedAmountLow"/>。设为 0 表示不触发。
    ///
    /// <para>有事件就别轮询 —— SIPSorcery 没有这个事件，只能轮询，
    /// 而 <c>Task.Delay(1)</c> 在 Windows 上实际睡 15.6ms，
    /// 光是轮询本身就能吃掉大半吞吐。</para>
    /// </summary>
    long BufferedAmountLowThreshold { get; set; }

    /// <summary>缓冲回落到阈值以下。</summary>
    event Action? BufferedAmountLow;

    /// <summary>收到一条消息。回调里<b>不要做重活</b>，尽快转交给消费方。</summary>
    event Action<ReadOnlyMemory<byte>>? MessageReceived;

    /// <summary>通道关闭（正常或异常）。<paramref name="reason"/> 为 null 表示正常关闭。</summary>
    event Action<string?>? Closed;

    /// <summary>
    /// 投递一条消息。<b>同步入队</b>，不等待真正发出 ——
    /// 想知道发出去多少要看 <see cref="BufferedAmount"/>。
    /// </summary>
    /// <exception cref="ArgumentException">消息超过 <see cref="MaxMessageSize"/>。</exception>
    /// <exception cref="InvalidOperationException">通道不处于 <see cref="DataChannelState.Open"/>。</exception>
    void Send(ReadOnlySpan<byte> message);

    /// <summary>等到通道进入 <see cref="DataChannelState.Open"/>。</summary>
    Task WaitForOpenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 等到 <see cref="BufferedAmount"/> 降到 <paramref name="threshold"/> 以下。
    /// 有事件就用事件，没有才退化为轮询 —— 这是背压的统一入口，
    /// 免得每个调用方各写一套轮询。
    /// </summary>
    Task WaitForDrainAsync(long threshold, CancellationToken cancellationToken = default);

    /// <summary>主动关闭。</summary>
    Task CloseAsync(string? reason = null);
}
