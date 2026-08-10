using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NexusP2P.Core.Codes;
using NexusP2P.Signaling;
using NexusP2P.Signaling.Rooms;

namespace NexusP2P.Integration.Tests.Signaling;

public sealed class RoomRegistryTests
{
    /// <summary>只记下收到的消息，够用来验证转发。</summary>
    private sealed class FakeSink : IPeerSink
    {
        private readonly List<string> _received = [];
        private readonly Lock _gate = new();

        public IReadOnlyList<string> Received
        {
            get
            {
                lock (_gate)
                {
                    return [.. _received];
                }
            }
        }

        public Task SendAsync(string json, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _received.Add(json);
            }

            return Task.CompletedTask;
        }
    }

    private static (RoomRegistry Registry, FakeTimeProvider Clock) Create(
        int gracePeriodSeconds = 60, int maxRooms = 1000)
    {
        var options = Options.Create(new SignalingOptions
        {
            PublicOrigin = "https://p2p.example.com",
            RoomGracePeriodSeconds = gracePeriodSeconds,
            MaxRooms = maxRooms,
        });

        // 宽限期是 60 秒，真等 60 秒的测试没人会跑。用假时钟。
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-07T12:00:00Z"));

        return (new RoomRegistry(options, clock, NullLogger<RoomRegistry>.Instance), clock);
    }

    [Fact]
    public void 建房后能用码进入()
    {
        var (registry, _) = Create();
        var sender = new FakeSink();

        Assert.True(registry.TryCreate(sender, out var code, out var room));
        Assert.NotNull(room);

        var receiver = new FakeSink();
        Assert.Equal(JoinOutcome.Joined, registry.TryJoin(code, PeerRole.Receiver, receiver, out var joined));
        Assert.Same(room, joined);
    }

    [Fact]
    public void 建房后房间数加一()
    {
        var (registry, _) = Create();

        Assert.Equal(0, registry.RoomCount);
        registry.TryCreate(new FakeSink(), out _, out _);
        Assert.Equal(1, registry.RoomCount);
    }

    [Fact]
    public void 发送方的位子在建房时就被占了()
    {
        var (registry, _) = Create();
        registry.TryCreate(new FakeSink(), out var code, out _);

        // 再有人想以发送方身份进来就该被拒
        Assert.Equal(
            JoinOutcome.Unavailable,
            registry.TryJoin(code, PeerRole.Sender, new FakeSink(), out _));
    }

    [Fact]
    public void 不存在的码返回_Unavailable()
    {
        var (registry, _) = Create();

        Assert.Equal(
            JoinOutcome.Unavailable,
            registry.TryJoin(TransferCode.Parse("123456789"), PeerRole.Receiver, new FakeSink(), out _));
    }

    /// <summary>
    /// 「码不存在」与「位子被占」必须完全不可区分，否则九位码就有了枚举预言机。
    /// 这里靠<b>类型</b>保证：<see cref="JoinOutcome"/> 只有两个取值，
    /// 想区分也无从下手。
    /// </summary>
    [Fact]
    public void 不存在与已占用返回完全相同的结果()
    {
        var (registry, _) = Create();
        registry.TryCreate(new FakeSink(), out var occupiedCode, out _);
        registry.TryJoin(occupiedCode, PeerRole.Receiver, new FakeSink(), out _);

        var occupied = registry.TryJoin(occupiedCode, PeerRole.Receiver, new FakeSink(), out _);
        var missing = registry.TryJoin(
            TransferCode.Parse("987654321"), PeerRole.Receiver, new FakeSink(), out _);

        Assert.Equal(JoinOutcome.Unavailable, occupied);
        Assert.Equal(JoinOutcome.Unavailable, missing);
        Assert.Equal(occupied, missing);
    }

    [Fact]
    public void 房间数达上限后拒绝新建()
    {
        var (registry, _) = Create(maxRooms: 3);

        for (var i = 0; i < 3; i++)
        {
            Assert.True(registry.TryCreate(new FakeSink(), out _, out _));
        }

        Assert.False(registry.TryCreate(new FakeSink(), out _, out _));
    }

    // ---- 宽限期：自动重连的前提（AD-7）----

    [Fact]
    public void 两端都离开后房间进入宽限期而不是立刻消失()
    {
        // 这是自动重连能成功的全部依据：网络抖动时双方的信令连接往往
        // 同时掉线，若房间立刻释放，自动重连必然扑空
        var (registry, clock) = Create(gracePeriodSeconds: 60);

        var sender = new FakeSink();
        registry.TryCreate(sender, out var code, out var room);
        var receiver = new FakeSink();
        registry.TryJoin(code, PeerRole.Receiver, receiver, out _);

        registry.Leave(room!, PeerRole.Sender, sender);
        registry.Leave(room!, PeerRole.Receiver, receiver);

        Assert.True(room!.IsEmpty);

        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(0, registry.Sweep());
        Assert.Equal(1, registry.RoomCount);
    }

    [Fact]
    public void 宽限期内用同一个码能回到原房间()
    {
        var (registry, clock) = Create(gracePeriodSeconds: 60);

        var sender = new FakeSink();
        registry.TryCreate(sender, out var code, out var room);
        registry.Leave(room!, PeerRole.Sender, sender);

        clock.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(
            JoinOutcome.Joined,
            registry.TryJoin(code, PeerRole.Sender, new FakeSink(), out var rejoined));
        Assert.Same(room, rejoined);
    }

    [Fact]
    public void 宽限期过后房间被回收()
    {
        var (registry, clock) = Create(gracePeriodSeconds: 60);

        var sender = new FakeSink();
        registry.TryCreate(sender, out var code, out var room);
        registry.Leave(room!, PeerRole.Sender, sender);

        clock.Advance(TimeSpan.FromSeconds(61));

        Assert.Equal(1, registry.Sweep());
        Assert.Equal(0, registry.RoomCount);
        Assert.Equal(
            JoinOutcome.Unavailable,
            registry.TryJoin(code, PeerRole.Receiver, new FakeSink(), out _));
    }

    [Fact]
    public void 宽限期过后即使还没被回收也不能进()
    {
        // Sweep 是定时跑的，所以「过期但还在字典里」这个窗口一定存在。
        // 入房判定不能只依赖 Sweep 已经跑过。
        var (registry, clock) = Create(gracePeriodSeconds: 60);

        var sender = new FakeSink();
        registry.TryCreate(sender, out var code, out var room);
        registry.Leave(room!, PeerRole.Sender, sender);

        clock.Advance(TimeSpan.FromSeconds(61));

        Assert.Equal(1, registry.RoomCount);   // 还没扫
        Assert.Equal(
            JoinOutcome.Unavailable,
            registry.TryJoin(code, PeerRole.Receiver, new FakeSink(), out _));
    }

    [Fact]
    public void 有人在的房间永不过期()
    {
        var (registry, clock) = Create(gracePeriodSeconds: 60);
        registry.TryCreate(new FakeSink(), out _, out var room);

        clock.Advance(TimeSpan.FromHours(10));

        Assert.False(room!.IsExpired(clock.GetUtcNow(), TimeSpan.FromSeconds(60)));
        Assert.Equal(0, registry.Sweep());
    }

    [Fact]
    public void 重新有人进来会清掉宽限期计时()
    {
        var (registry, clock) = Create(gracePeriodSeconds: 60);

        var sender = new FakeSink();
        registry.TryCreate(sender, out var code, out var room);
        registry.Leave(room!, PeerRole.Sender, sender);

        clock.Advance(TimeSpan.FromSeconds(50));
        registry.TryJoin(code, PeerRole.Sender, new FakeSink(), out _);

        // 若计时没被清掉，再过 11 秒就会被误回收
        clock.Advance(TimeSpan.FromSeconds(11));

        Assert.Equal(0, registry.Sweep());
        Assert.Null(room!.EmptySince);
    }

    // ---- 转发与位子归属 ----

    [Fact]
    public void 双方就位后互为对端()
    {
        var (registry, _) = Create();

        var sender = new FakeSink();
        registry.TryCreate(sender, out var code, out var room);
        var receiver = new FakeSink();
        registry.TryJoin(code, PeerRole.Receiver, receiver, out _);

        Assert.Same(receiver, room!.Counterpart(PeerRole.Sender));
        Assert.Same(sender, room.Counterpart(PeerRole.Receiver));
    }

    [Fact]
    public void 对端不在时_Counterpart_为空()
    {
        var (registry, _) = Create();
        registry.TryCreate(new FakeSink(), out _, out var room);

        Assert.Null(room!.Counterpart(PeerRole.Sender));
    }

    [Fact]
    public void 迟到的清理不会踢掉重连后新占位的成员()
    {
        // 真实时序：旧连接的清理逻辑可能在新连接已经占位之后才跑到。
        // 若不校验「仍然是自己占着」，就会把刚重连上来的人踢下去。
        var (registry, _) = Create();

        var oldSender = new FakeSink();
        registry.TryCreate(oldSender, out var code, out var room);
        registry.Leave(room!, PeerRole.Sender, oldSender);

        var newSender = new FakeSink();
        registry.TryJoin(code, PeerRole.Sender, newSender, out _);

        // 旧连接的清理迟到了
        registry.Leave(room!, PeerRole.Sender, oldSender);

        Assert.Same(newSender, room!.Counterpart(PeerRole.Receiver));
        Assert.False(room.IsEmpty);
    }

    [Fact]
    public void 生成的码互不重复()
    {
        var (registry, _) = Create(maxRooms: 200);
        var codes = new HashSet<int>();

        for (var i = 0; i < 200; i++)
        {
            Assert.True(registry.TryCreate(new FakeSink(), out var code, out _));
            Assert.True(codes.Add(code.Value), $"码 {code} 重复了");
        }
    }
}
