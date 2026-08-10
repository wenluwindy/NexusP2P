using System.Reflection;
using SIPSorcery.Net;

namespace SipSorceryThroughput;

/// <summary>
/// 用反射窥探（并可改写）SIPSorcery 内部 SCTP 发送器的状态。
///
/// 这么做是为了确诊：SIPSorcery 的 SctpDataSender 用一个硬编码的节拍循环发包
/// （BURST_PERIOD_MILLISECONDS = 50），每个节拍最多发一个拥塞窗口的数据。
/// 于是吞吐上限 ≈ cwnd / 50ms，而 cwnd 的增长又以这 50ms 为「一个 RTT」，
/// 导致在低延迟链路上速度被死死钉住，与实际网络能力无关。
///
/// 注意：这是诊断手段，不是可以交付的方案。改私有字段不能进生产代码。
/// </summary>
internal sealed class SctpProbe
{
    private readonly object _sender;
    private readonly FieldInfo _burstPeriod;
    private readonly FieldInfo _congestionWindow;
    private readonly FieldInfo _slowStartThreshold;
    private readonly FieldInfo _receiverWindow;
    private readonly PropertyInfo? _outstandingBytes;

    private SctpProbe(object sender)
    {
        _sender = sender;
        var t = sender.GetType();
        _burstPeriod = Field(t, "_burstPeriodMilliseconds");
        _congestionWindow = Field(t, "_congestionWindow");
        _slowStartThreshold = Field(t, "_slowStartThreshold");
        _receiverWindow = Field(t, "_receiverWindow");
        _outstandingBytes = t.GetProperty("_outstandingBytes",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
    }

    public static SctpProbe? TryCreate(RTCPeerConnection pc)
    {
        try
        {
            var association = pc.sctp?.RTCSctpAssociation;
            if (association is null) return null;

            var senderField = Field(association.GetType(), "_dataSender");
            var sender = senderField.GetValue(association);
            return sender is null ? null : new SctpProbe(sender);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  (SCTP 探针不可用：{ex.Message})");
            return null;
        }
    }

    public int BurstPeriodMs
    {
        get => (int)_burstPeriod.GetValue(_sender)!;
        set => _burstPeriod.SetValue(_sender, value);
    }

    public uint CongestionWindow => (uint)_congestionWindow.GetValue(_sender)!;
    public uint SlowStartThreshold => (uint)_slowStartThreshold.GetValue(_sender)!;
    public uint ReceiverWindow => (uint)_receiverWindow.GetValue(_sender)!;
    public uint OutstandingBytes => (uint)(_outstandingBytes?.GetValue(_sender) ?? 0u);

    /// <summary>cwnd / 节拍周期 —— 这个库结构上能达到的理论吞吐上限。</summary>
    public double TheoreticalMiBps => CongestionWindow / 1024.0 / 1024 / (BurstPeriodMs / 1000.0);

    public string Snapshot() =>
        $"cwnd {CongestionWindow / 1024.0,6:N1} KiB | " +
        $"ssthresh {SlowStartThreshold / 1024.0,7:N0} KiB | " +
        $"rwnd {ReceiverWindow / 1024.0,6:N0} KiB | " +
        $"在途 {OutstandingBytes / 1024.0,6:N1} KiB | " +
        $"节拍 {BurstPeriodMs,3} ms | 理论上限 {TheoreticalMiBps,6:N2} MiB/s";

    /// <summary>沿继承链往上找私有字段。</summary>
    private static FieldInfo Field(Type type, string name)
    {
        for (var t = type; t is not null; t = t.BaseType!)
        {
            var f = t.GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (f is not null) return f;
        }

        throw new MissingFieldException(type.FullName, name);
    }
}
