namespace NexusP2P.Transfer.Reconnect;

/// <summary>
/// 由异常自己声明「换一条连接重试有没有意义」。
///
/// <para>有些失败光看类型分不出来。<c>SignalingException</c> 就是典型：
/// 「连不上信令服务器」值得重试（网线可能只是拔了十秒），
/// 而「房间不可用」重试一百次也是同一个结果 —— 只会把真正的原因
/// 推迟十几秒才让用户看到。</para>
///
/// <para>放在这一层是因为 <see cref="ReconnectPolicy"/> 不能反过来依赖上层：
/// 信令是 Agent 层的概念，而重连策略必须能在没有网络的情况下单独测。</para>
/// </summary>
public interface IRetryableFailure
{
    /// <summary>重新建一条连接是否有可能得到不同结果。</summary>
    bool IsRetryable { get; }
}
