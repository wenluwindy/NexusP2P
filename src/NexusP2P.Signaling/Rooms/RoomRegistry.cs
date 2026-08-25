using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using NexusP2P.Core.Codes;

namespace NexusP2P.Signaling.Rooms;

/// <summary>
/// 入房结果。
///
/// <para><b>刻意只有两个取值。</b>「码不存在」与「位子已被占」必须对外
/// 完全不可区分，否则九位码就有了枚举预言机 —— 攻击者能靠错误信息的差异
/// 判断哪些码是活的。</para>
///
/// <para>把这件事做成<b>类型上不可能出错</b>，而不是靠每个调用点都记得
/// 返回同样的措辞。区分信息只写进服务端日志。</para>
/// </summary>
public enum JoinOutcome
{
    Joined,
    Unavailable,
}

/// <summary>
/// 内存里的房间表。<b>完全无状态</b>（AD-4）：不落盘、不依赖外部存储，
/// 进程重启全丢也不影响正确性。
/// </summary>
public sealed class RoomRegistry(
    IOptions<SignalingOptions> options,
    TimeProvider timeProvider,
    ILogger<RoomRegistry> logger)
{
    /// <summary>生成文件码时最多试几次。10^9 的空间里撞车概率极低，但不能不设上限。</summary>
    private const int MaxCodeAllocationAttempts = 32;

    private readonly ConcurrentDictionary<int, Room> _rooms = new();

    private SignalingOptions Options => options.Value;

    private TimeSpan GracePeriod => TimeSpan.FromSeconds(Options.RoomGracePeriodSeconds);

    public int RoomCount => _rooms.Count;

    /// <summary>
    /// 建一个新房间并占住发送方的位子。
    /// 房间数达到上限时返回 false —— 宁可拒绝新建，也不让内存被吃光。
    ///
    /// <para><paramref name="maxReceivers"/> 是接收方席位数（AD-15）：
    /// 默认 1 时行为与 V1 完全一致。调用方负责先把它夹到合法区间。</para>
    ///
    /// <para><paramref name="password"/> 是可选的进房口令：null（默认）时
    /// 房间不设口令，行为与从前完全一致。</para>
    /// </summary>
    public bool TryCreate(
        IPeerSink sender, out TransferCode code, out Room? room, int maxReceivers = 1, RoomPassword? password = null)
    {
        code = default;
        room = null;

        if (_rooms.Count >= Options.MaxRooms)
        {
            logger.LogWarning("房间数已达上限 {MaxRooms}，拒绝新建。", Options.MaxRooms);
            return false;
        }

        var now = timeProvider.GetUtcNow();

        for (var attempt = 0; attempt < MaxCodeAllocationAttempts; attempt++)
        {
            var candidate = TransferCode.Generate();
            var created = new Room(candidate, now, maxReceivers, password);

            if (!_rooms.TryAdd(candidate.Value, created))
            {
                continue;   // 撞上了一个活着的房间，换一个码
            }

            if (!created.TryOccupy(PeerRole.Sender, sender))
            {
                // 刚建出来的房间不可能被占，除非有并发 bug
                _rooms.TryRemove(candidate.Value, out _);
                continue;
            }

            code = candidate;
            room = created;
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("房间 {Code} 已建立。", candidate);
            }
            return true;
        }

        logger.LogError("连续 {Attempts} 次都没能分配到空闲的文件码。", MaxCodeAllocationAttempts);
        return false;
    }

    /// <summary>
    /// 用文件码进入房间并占住指定角色的位子。
    ///
    /// <para>宽限期内的空房间仍然可以进 —— 这正是自动重连的依据（AD-7）。</para>
    ///
    /// <para>以接收方身份进入时 <paramref name="peerId"/> 是服务器分配的会话内
    /// 标识（AD-12）；发送方没有 peerId，返回 null。</para>
    ///
    /// <para><paramref name="password"/>：房间设置了口令时必须凭口令进房。
    /// 口令缺失或错误与「码不存在」返回同一个 <see cref="JoinOutcome.Unavailable"/>
    /// —— 口令不能给九位码引入新的枚举预言机。</para>
    /// </summary>
    public JoinOutcome TryJoin(
        TransferCode code, PeerRole role, IPeerSink sink, string? password, out Room? room, out string? peerId)
    {
        room = null;
        peerId = null;

        if (!_rooms.TryGetValue(code.Value, out var existing))
        {
            // 只写日志，不告诉客户端 —— 见 JoinOutcome 的说明
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("入房失败：房间 {Code} 不存在。", code);
            }
            return JoinOutcome.Unavailable;
        }

        if (existing.IsExpired(timeProvider.GetUtcNow(), GracePeriod))
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("入房失败：房间 {Code} 的宽限期已过。", code);
            }
            return JoinOutcome.Unavailable;
        }

        if (existing.Password is { } verifier && !verifier.Matches(password))
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("入房失败：房间 {Code} 的口令缺失或不正确。", code);
            }
            return JoinOutcome.Unavailable;
        }

        var occupied = role == PeerRole.Sender
            ? existing.TryOccupySender(sink)
            : existing.TryAddReceiver(sink, out peerId);

        if (!occupied)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("入房失败：房间 {Code} 的 {Role} 位子已被占。", code, role);
            }
            return JoinOutcome.Unavailable;
        }

        room = existing;
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("{Role} 已进入房间 {Code}。", role, code);
        }
        return JoinOutcome.Joined;
    }

    /// <summary>不带口令的完整入口（V2 及更早的全部调用方）。</summary>
    public JoinOutcome TryJoin(TransferCode code, PeerRole role, IPeerSink sink, out Room? room, out string? peerId) =>
        TryJoin(code, role, sink, null, out room, out peerId);

    /// <summary>V1 兼容入口：不需要 peerId 的调用方继续用它。</summary>
    public JoinOutcome TryJoin(TransferCode code, PeerRole role, IPeerSink sink, out Room? room) =>
        TryJoin(code, role, sink, null, out room, out _);

    /// <summary>成员离开。房间变空后进入宽限期，由 <see cref="Sweep"/> 回收。</summary>
    public void Leave(Room room, PeerRole role, IPeerSink sink)
    {
        ArgumentNullException.ThrowIfNull(room);

        room.Vacate(role, sink, timeProvider.GetUtcNow());
        LogIfEmpty(room);
    }

    /// <summary>接收方按 peerId 离开（AD-12 的精确版本；引用双重校验在 Room 里做）。</summary>
    public void LeaveReceiver(Room room, string peerId, IPeerSink sink)
    {
        ArgumentNullException.ThrowIfNull(room);

        room.VacateReceiver(peerId, sink, timeProvider.GetUtcNow());
        LogIfEmpty(room);
    }

    private void LogIfEmpty(Room room)
    {
        if (room.IsEmpty)
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "房间 {Code} 已空，进入 {Grace} 秒宽限期。", room.Code, Options.RoomGracePeriodSeconds);
            }
        }
    }

    /// <summary>回收宽限期已过的空房间。返回回收数量。</summary>
    public int Sweep()
    {
        var now = timeProvider.GetUtcNow();
        var removed = 0;

        foreach (var (key, room) in _rooms)
        {
            if (!room.IsExpired(now, GracePeriod))
            {
                continue;
            }

            if (_rooms.TryRemove(key, out _))
            {
                removed++;
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("房间 {Code} 宽限期已过，已回收。", room.Code);
                }
            }
        }

        return removed;
    }

    /// <summary>仅测试与诊断用。</summary>
    internal bool TryPeek(TransferCode code, out Room? room) => _rooms.TryGetValue(code.Value, out room);
}
