using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using NexusP2P.Core.Codes;

namespace NexusP2P.Signaling.Rooms;

/// <summary>房间里的两类角色。</summary>
public enum PeerRole
{
    Sender,
    Receiver,
}

/// <summary>把一条信令消息发给某个成员。</summary>
public interface IPeerSink
{
    Task SendAsync(string json, CancellationToken cancellationToken);
}

/// <summary>
/// 一个房间：1 个发送方 + 至多 <see cref="MaxReceivers"/> 个接收方，
/// 只负责把消息在成员之间转发（AD-12）。
///
/// <para><b>房间只存在于内存里，不落盘。</b>进程重启全丢也不影响正确性 ——
/// 续传的锚点是内容（清单哈希）而不是房间号（AD-4）。</para>
///
/// <para>服务器<b>不解析也不存储</b>转发的内容。SDP 与 ICE 候选对它是不透明的
/// 字符串，这样「服务器看不到文件内容」这个承诺不依赖于我们记得不去看。</para>
///
/// <para><see cref="MaxReceivers"/> 默认 1，此时行为与 V1 完全一致（AD-15）：
/// 第二个接收方进房得到与「码不存在」相同的失败，不产生新的枚举预言机。</para>
/// </summary>
public sealed class Room(TransferCode code, DateTimeOffset createdAt, int maxReceivers = 1)
{
    private readonly Lock _gate = new();
    private IPeerSink? _sender;

    /// <summary>peerId → 接收方。peerId 是会话内标识，不持久（AD-16）。</summary>
    private readonly Dictionary<string, IPeerSink> _receivers = [];

    private DateTimeOffset? _emptySince = createdAt;

    public TransferCode Code { get; } = code;

    public DateTimeOffset CreatedAt { get; } = createdAt;

    /// <summary>接收方席位数。建房时声明（AD-15），生命周期内不变。</summary>
    public int MaxReceivers { get; } =
        maxReceivers >= 1
            ? maxReceivers
            : throw new ArgumentOutOfRangeException(nameof(maxReceivers), "至少要有一个接收方席位。");

    /// <summary>所有成员都不在时的时刻。用来算宽限期（AD-16）。</summary>
    public DateTimeOffset? EmptySince
    {
        get
        {
            lock (_gate)
            {
                return _emptySince;
            }
        }
    }

    public bool IsEmpty
    {
        get
        {
            lock (_gate)
            {
                return _sender is null && _receivers.Count == 0;
            }
        }
    }

    /// <summary>发送方。不在则为 null。</summary>
    public IPeerSink? Sender
    {
        get
        {
            lock (_gate)
            {
                return _sender;
            }
        }
    }

    /// <summary>当前在房接收方的 peerId 快照。</summary>
    public IReadOnlyList<string> ReceiverIds
    {
        get
        {
            lock (_gate)
            {
                return [.. _receivers.Keys];
            }
        }
    }

    /// <summary>当前在房接收方的 (peerId, sink) 快照。给「通知所有接收方」用。</summary>
    public IReadOnlyList<KeyValuePair<string, IPeerSink>> Receivers
    {
        get
        {
            lock (_gate)
            {
                return [.. _receivers];
            }
        }
    }

    /// <summary>占住发送方位子。已被占则返回 false。</summary>
    public bool TryOccupySender(IPeerSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        lock (_gate)
        {
            if (_sender is not null)
            {
                return false;
            }

            _sender = sink;
            _emptySince = null;
            return true;
        }
    }

    /// <summary>
    /// 加入一个接收方并分配 peerId。席位已满则返回 false。
    ///
    /// <para>peerId 由服务器分配而不是客户端自报 —— 自报的标识既可能撞车
    /// 也可能被恶意仿冒成别人。</para>
    /// </summary>
    public bool TryAddReceiver(IPeerSink sink, [NotNullWhen(true)] out string? peerId)
    {
        ArgumentNullException.ThrowIfNull(sink);
        peerId = null;

        lock (_gate)
        {
            if (_receivers.Count >= MaxReceivers)
            {
                return false;
            }

            string candidate;
            do
            {
                candidate = GeneratePeerId();
            }
            while (_receivers.ContainsKey(candidate));

            _receivers.Add(candidate, sink);
            _emptySince = null;
            peerId = candidate;
            return true;
        }
    }

    /// <summary>
    /// 腾出发送方位子。只有仍然是自己占着才腾 —— 否则一个迟到的清理
    /// 会把重连后新占位的成员踢掉。
    /// </summary>
    public void VacateSender(IPeerSink sink, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_sender, sink))
            {
                return;
            }

            _sender = null;
            StampIfEmpty(now);
        }
    }

    /// <summary>
    /// 腾出一个接收方位子。按 peerId + 引用<b>双重</b>校验：peerId 防拿错位子，
    /// 引用防迟到的清理踢掉重连后拿到同一 peerId 的新人（理论上 peerId 不复用，
    /// 双重校验让这一点不依赖生成器的实现细节）。
    /// </summary>
    public void VacateReceiver(string peerId, IPeerSink sink, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(peerId);

        lock (_gate)
        {
            if (!_receivers.TryGetValue(peerId, out var occupant) || !ReferenceEquals(occupant, sink))
            {
                return;
            }

            _receivers.Remove(peerId);
            StampIfEmpty(now);
        }
    }

    /// <summary>按 peerId 找接收方。不在则为 null（正常时序：刚断线，不是协议违规）。</summary>
    public IPeerSink? Receiver(string peerId)
    {
        lock (_gate)
        {
            return _receivers.GetValueOrDefault(peerId);
        }
    }

    /// <summary>
    /// 房里<b>唯一</b>的接收方；没有或不止一个都返回 null。
    ///
    /// <para>给不带 <c>to</c> 的旧客户端路由用（AD-15）：旧客户端只可能在
    /// <see cref="MaxReceivers"/> = 1 的房间里，此时「唯一的接收方」就是 V1 的对端。
    /// 多接收方房间里不带 <c>to</c> 的消息没有明确目标，静默丢弃。</para>
    /// </summary>
    public IPeerSink? SoleReceiver
    {
        get
        {
            lock (_gate)
            {
                return _receivers.Count == 1 ? _receivers.Values.First() : null;
            }
        }
    }

    // ---- V1 兼容入口：默认容量下行为与 V1 完全一致，既有调用与测试不动 ----

    /// <summary>占住一个角色的位子（V1 兼容入口）。位子已被占/席位已满则返回 false。</summary>
    public bool TryOccupy(PeerRole role, IPeerSink sink) =>
        role == PeerRole.Sender ? TryOccupySender(sink) : TryAddReceiver(sink, out _);

    /// <summary>腾出一个角色的位子（V1 兼容入口）。接收方按引用反查 peerId。</summary>
    public void Vacate(PeerRole role, IPeerSink sink, DateTimeOffset now)
    {
        if (role == PeerRole.Sender)
        {
            VacateSender(sink, now);
            return;
        }

        lock (_gate)
        {
            // N ≤ 8，线性扫没有性能问题
            foreach (var (peerId, occupant) in _receivers)
            {
                if (ReferenceEquals(occupant, sink))
                {
                    _receivers.Remove(peerId);
                    StampIfEmpty(now);
                    return;
                }
            }
        }
    }

    /// <summary>
    /// 对端的发送口（V1 兼容入口）。发送方的对端是「唯一的接收方」，
    /// 接收方的对端是发送方。对端不在则为 null。
    /// </summary>
    public IPeerSink? Counterpart(PeerRole role) =>
        role == PeerRole.Sender ? SoleReceiver : Sender;

    /// <summary>宽限期是否已过。房间非空时永不过期。</summary>
    public bool IsExpired(DateTimeOffset now, TimeSpan gracePeriod)
    {
        lock (_gate)
        {
            if (_sender is not null || _receivers.Count > 0)
            {
                return false;
            }

            return _emptySince is { } since && now - since >= gracePeriod;
        }
    }

    /// <summary>调用方必须已持有 <see cref="_gate"/>。</summary>
    private void StampIfEmpty(DateTimeOffset now)
    {
        if (_sender is null && _receivers.Count == 0)
        {
            _emptySince = now;
        }
    }

    /// <summary>8 个小写十六进制字符。会话内标识，不需要全局唯一，只需房间内唯一。</summary>
    private static string GeneratePeerId()
    {
        Span<byte> bytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexStringLower(bytes);
    }
}
