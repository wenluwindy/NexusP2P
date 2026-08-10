using NexusP2P.Core.Codes;

namespace NexusP2P.Signaling.Rooms;

/// <summary>房间里的两个角色。</summary>
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
/// 一个房间：至多两个成员，只负责把消息从一端转发到另一端。
///
/// <para><b>房间只存在于内存里，不落盘。</b>进程重启全丢也不影响正确性 ——
/// 续传的锚点是内容（清单哈希）而不是房间号（AD-4）。</para>
///
/// <para>服务器<b>不解析也不存储</b>转发的内容。SDP 与 ICE 候选对它是不透明的
/// 字符串，这样「服务器看不到文件内容」这个承诺不依赖于我们记得不去看。</para>
/// </summary>
public sealed class Room(TransferCode code, DateTimeOffset createdAt)
{
    private readonly Lock _gate = new();
    private IPeerSink? _sender;
    private IPeerSink? _receiver;
    private DateTimeOffset? _emptySince = createdAt;

    public TransferCode Code { get; } = code;

    public DateTimeOffset CreatedAt { get; } = createdAt;

    /// <summary>两端都不在时的时刻。用来算宽限期。</summary>
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
                return _sender is null && _receiver is null;
            }
        }
    }

    /// <summary>占住一个角色的位子。位子已被占用则返回 false。</summary>
    public bool TryOccupy(PeerRole role, IPeerSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        lock (_gate)
        {
            ref var slot = ref role == PeerRole.Sender ? ref _sender : ref _receiver;
            if (slot is not null)
            {
                return false;
            }

            slot = sink;
            _emptySince = null;
            return true;
        }
    }

    /// <summary>
    /// 腾出一个角色的位子。<paramref name="now"/> 用来在房间变空时打时间戳。
    /// </summary>
    public void Vacate(PeerRole role, IPeerSink sink, DateTimeOffset now)
    {
        lock (_gate)
        {
            ref var slot = ref role == PeerRole.Sender ? ref _sender : ref _receiver;

            // 只有仍然是自己占着才腾 —— 否则一个迟到的清理会把
            // 重连后新占位的成员踢掉
            if (!ReferenceEquals(slot, sink))
            {
                return;
            }

            slot = null;

            if (_sender is null && _receiver is null)
            {
                _emptySince = now;
            }
        }
    }

    /// <summary>对端的发送口。对端不在则为 null。</summary>
    public IPeerSink? Counterpart(PeerRole role)
    {
        lock (_gate)
        {
            return role == PeerRole.Sender ? _receiver : _sender;
        }
    }

    /// <summary>宽限期是否已过。房间非空时永不过期。</summary>
    public bool IsExpired(DateTimeOffset now, TimeSpan gracePeriod)
    {
        lock (_gate)
        {
            if (_sender is not null || _receiver is not null)
            {
                return false;
            }

            return _emptySince is { } since && now - since >= gracePeriod;
        }
    }
}
