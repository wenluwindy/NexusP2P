namespace NexusP2P.Transport.Abstractions;

/// <summary>
/// 通道已关闭。
///
/// <para>继承 <see cref="InvalidOperationException"/> 是为了让
/// <see cref="IDataChannel.Send"/> 的契约（不处于 Open 时抛
/// <see cref="InvalidOperationException"/>）仍然成立，同时又能被单独捕获。</para>
///
/// <para><see cref="IDataChannel.WaitForDrainAsync"/> 在等待期间通道关闭时
/// <b>抛异常而不是静默返回</b> —— 静默返回会让发送循环空转，
/// 那种 bug 表现为「CPU 打满但没有任何进展」，比直接失败难查得多。</para>
/// </summary>
public sealed class DataChannelClosedException(string? reason = null)
    : InvalidOperationException(reason is null ? "数据通道已关闭。" : $"数据通道已关闭：{reason}")
{
    public string? Reason { get; } = reason;
}
