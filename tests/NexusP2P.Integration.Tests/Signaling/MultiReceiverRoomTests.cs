using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NexusP2P.Signaling;
using NexusP2P.Signaling.Rooms;

namespace NexusP2P.Integration.Tests.Signaling;

/// <summary>
/// V2 多接收方房间（AD-12/15/16）。
/// V1 行为（maxReceivers=1）的回归保证在 <see cref="RoomRegistryTests"/> ——
/// 那些测试原样未动，全部走兼容入口。
/// </summary>
public sealed class MultiReceiverRoomTests
{
    private sealed class FakeSink : IPeerSink
    {
        public Task SendAsync(string json, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static (RoomRegistry Registry, FakeTimeProvider Clock) Create(int gracePeriodSeconds = 60)
    {
        var options = Options.Create(new SignalingOptions
        {
            PublicOrigin = "https://p2p.example.com",
            RoomGracePeriodSeconds = gracePeriodSeconds,
            MaxRooms = 1000,
        });

        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-07T12:00:00Z"));
        return (new RoomRegistry(options, clock, NullLogger<RoomRegistry>.Instance), clock);
    }

    // ---- 席位（AD-15）----

    [Fact]
    public void 默认席位是_1_第二个接收方被拒()
    {
        var (registry, _) = Create();
        registry.TryCreate(new FakeSink(), out var code, out _);

        Assert.Equal(JoinOutcome.Joined, registry.TryJoin(code, PeerRole.Receiver, new FakeSink(), out _, out var id1));
        Assert.NotNull(id1);

        // 与「码不存在」同一个 Unavailable —— 预言机规则不破
        Assert.Equal(
            JoinOutcome.Unavailable,
            registry.TryJoin(code, PeerRole.Receiver, new FakeSink(), out _, out var id2));
        Assert.Null(id2);
    }

    [Fact]
    public void 声明_N_个席位后前_N_个都能进第_N加1_个被拒()
    {
        var (registry, _) = Create();
        registry.TryCreate(new FakeSink(), out var code, out var room, maxReceivers: 3);

        var ids = new HashSet<string>();
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(
                JoinOutcome.Joined,
                registry.TryJoin(code, PeerRole.Receiver, new FakeSink(), out _, out var peerId));
            Assert.NotNull(peerId);
            Assert.True(ids.Add(peerId!), $"peerId {peerId} 重复了");
        }

        Assert.Equal(
            JoinOutcome.Unavailable,
            registry.TryJoin(code, PeerRole.Receiver, new FakeSink(), out _, out _));
        Assert.Equal(3, room!.ReceiverIds.Count);
    }

    [Fact]
    public void 席位满员被拒与码不存在完全不可区分()
    {
        var (registry, _) = Create();
        registry.TryCreate(new FakeSink(), out var code, out _, maxReceivers: 2);
        registry.TryJoin(code, PeerRole.Receiver, new FakeSink(), out _, out _);
        registry.TryJoin(code, PeerRole.Receiver, new FakeSink(), out _, out _);

        var full = registry.TryJoin(code, PeerRole.Receiver, new FakeSink(), out _, out _);
        var missing = registry.TryJoin(
            NexusP2P.Core.Codes.TransferCode.Parse("987654321"), PeerRole.Receiver, new FakeSink(), out _, out _);

        Assert.Equal(full, missing);
    }

    // ---- peerId 寻址（AD-12）----

    [Fact]
    public void 按_peerId_能找到对应的接收方()
    {
        var (registry, _) = Create();
        registry.TryCreate(new FakeSink(), out var code, out var room, maxReceivers: 2);

        var r1 = new FakeSink();
        var r2 = new FakeSink();
        registry.TryJoin(code, PeerRole.Receiver, r1, out _, out var id1);
        registry.TryJoin(code, PeerRole.Receiver, r2, out _, out var id2);

        Assert.Same(r1, room!.Receiver(id1!));
        Assert.Same(r2, room.Receiver(id2!));
        Assert.Null(room.Receiver("deadbeef"));   // 不存在的 peerId：null，不抛
    }

    [Fact]
    public void 多接收方时_SoleReceiver_为空_单接收方时就是那一个()
    {
        var (registry, _) = Create();
        registry.TryCreate(new FakeSink(), out var code, out var room, maxReceivers: 2);

        Assert.Null(room!.SoleReceiver);   // 0 个

        var r1 = new FakeSink();
        registry.TryJoin(code, PeerRole.Receiver, r1, out _, out _);
        Assert.Same(r1, room.SoleReceiver);   // 恰好 1 个：V1 客户端可路由

        registry.TryJoin(code, PeerRole.Receiver, new FakeSink(), out _, out _);
        Assert.Null(room.SoleReceiver);   // 2 个：不带 to 的消息没有明确目标
    }

    // ---- 离开与生命周期（AD-16）----

    [Fact]
    public void 接收方离开只腾自己的位子()
    {
        var (registry, _) = Create();
        registry.TryCreate(new FakeSink(), out var code, out var room, maxReceivers: 3);

        var r1 = new FakeSink();
        var r2 = new FakeSink();
        registry.TryJoin(code, PeerRole.Receiver, r1, out _, out var id1);
        registry.TryJoin(code, PeerRole.Receiver, r2, out _, out var id2);

        registry.LeaveReceiver(room!, id1!, r1);

        Assert.Null(room!.Receiver(id1!));
        Assert.Same(r2, room.Receiver(id2!));
        Assert.False(room.IsEmpty);
    }

    [Fact]
    public void 迟到的清理不会踢掉占着同一_peerId_之外的新人()
    {
        // 接收方断线重连拿的是新 peerId，但防御照样要做：
        // 按 peerId + 引用双重校验，拿错谁的位子都不可能
        var (registry, _) = Create();
        registry.TryCreate(new FakeSink(), out var code, out var room, maxReceivers: 2);

        var old = new FakeSink();
        registry.TryJoin(code, PeerRole.Receiver, old, out _, out var oldId);
        registry.LeaveReceiver(room!, oldId!, old);

        var fresh = new FakeSink();
        registry.TryJoin(code, PeerRole.Receiver, fresh, out _, out var freshId);

        // 旧连接的清理迟到了：peerId 相同的概率极低，但引用校验兜底
        registry.LeaveReceiver(room!, freshId!, old);

        Assert.Same(fresh, room!.Receiver(freshId!));
    }

    [Fact]
    public void 发送方在时房间永不过期_接收方全走也一样()
    {
        var (registry, clock) = Create(gracePeriodSeconds: 60);
        registry.TryCreate(new FakeSink(), out var code, out var room, maxReceivers: 2);

        var r1 = new FakeSink();
        registry.TryJoin(code, PeerRole.Receiver, r1, out _, out var id1);
        registry.LeaveReceiver(room!, id1!, r1);

        clock.Advance(TimeSpan.FromHours(10));

        Assert.False(room!.IsExpired(clock.GetUtcNow(), TimeSpan.FromSeconds(60)));
        Assert.Equal(0, registry.Sweep());
    }

    [Fact]
    public void 接收方在而发送方不在时房间保留()
    {
        var (registry, clock) = Create(gracePeriodSeconds: 60);
        var sender = new FakeSink();
        registry.TryCreate(sender, out var code, out var room, maxReceivers: 2);
        registry.TryJoin(code, PeerRole.Receiver, new FakeSink(), out _, out _);

        registry.Leave(room!, PeerRole.Sender, sender);
        clock.Advance(TimeSpan.FromHours(1));

        Assert.False(room!.IsExpired(clock.GetUtcNow(), TimeSpan.FromSeconds(60)));

        // 发送方宽限期内（这里房间根本没空过）以 sender 回位
        Assert.Equal(
            JoinOutcome.Joined,
            registry.TryJoin(code, PeerRole.Sender, new FakeSink(), out var rejoined, out _));
        Assert.Same(room, rejoined);
    }

    [Fact]
    public void 所有成员都离开后才起算宽限期()
    {
        var (registry, clock) = Create(gracePeriodSeconds: 60);
        var sender = new FakeSink();
        registry.TryCreate(sender, out var code, out var room, maxReceivers: 2);

        var r1 = new FakeSink();
        registry.TryJoin(code, PeerRole.Receiver, r1, out _, out var id1);

        registry.Leave(room!, PeerRole.Sender, sender);
        clock.Advance(TimeSpan.FromSeconds(50));

        // 接收方还在：没起算
        Assert.Equal(0, registry.Sweep());

        registry.LeaveReceiver(room!, id1!, r1);
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(0, registry.Sweep());   // 从最后一人离开起算

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(1, registry.Sweep());
    }

    // ---- 并发（席位数守恒）----

    [Fact]
    public async Task 并发进房不超席且_peerId_不重复()
    {
        var (registry, _) = Create();
        registry.TryCreate(new FakeSink(), out var code, out var room, maxReceivers: 4);

        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() =>
        {
            var joined = registry.TryJoin(code, PeerRole.Receiver, new FakeSink(), out _, out var peerId);
            return (joined, peerId);
        })));

        var admitted = results.Where(r => r.joined == JoinOutcome.Joined).ToList();
        Assert.Equal(4, admitted.Count);
        Assert.Equal(4, admitted.Select(r => r.peerId).Distinct().Count());
        Assert.Equal(4, room!.ReceiverIds.Count);
    }

    [Fact]
    public async Task 并发进出后席位数守恒()
    {
        var (registry, _) = Create();
        registry.TryCreate(new FakeSink(), out var code, out var room, maxReceivers: 4);

        await Task.WhenAll(Enumerable.Range(0, 64).Select(i => Task.Run(() =>
        {
            var sink = new FakeSink();
            if (registry.TryJoin(code, PeerRole.Receiver, sink, out _, out var peerId) == JoinOutcome.Joined)
            {
                registry.LeaveReceiver(room!, peerId!, sink);
            }
        })));

        Assert.Empty(room!.ReceiverIds);

        // 全部退干净后席位应当完整可用
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(
                JoinOutcome.Joined,
                registry.TryJoin(code, PeerRole.Receiver, new FakeSink(), out _, out _));
        }
    }
}
