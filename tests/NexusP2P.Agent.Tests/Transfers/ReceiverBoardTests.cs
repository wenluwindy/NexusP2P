using NexusP2P.Agent.Transfers;
using NexusP2P.Transfer;

namespace NexusP2P.Agent.Tests.Transfers;

/// <summary>
/// Task 10.2 的模型侧验证：逐链路状态如何聚合成 UI 快照。
/// 界面本身不测（WPF），但驱动界面的每一条数据都在这里被断言。
/// </summary>
public sealed class ReceiverBoardTests
{
    private const long Total = 1000;

    private static TransferSnapshot Snapshot() => new()
    {
        Id = "t",
        IsSending = true,
        Phase = TransferPhase.WaitingForPeer,
        MaxReceivers = 3,
    };

    private static FanOutPeerStatus Running(string peerId, long done) => new(
        peerId, FanOutLinkState.Running, new TransferProgress(done, Total, 0, 10), null, null);

    private static FanOutPeerStatus Completed(string peerId) => new(
        peerId, FanOutLinkState.Completed, new TransferProgress(Total, Total, 10, 10), null, null);

    private static FanOutPeerStatus Failed(string peerId, string message) => new(
        peerId, FanOutLinkState.Failed, new TransferProgress(200, Total, 2, 10), null,
        new InvalidOperationException(message));

    [Fact]
    public void 每个接收方一行_整体进度是各链路之和()
    {
        var board = new ReceiverBoard(Total);
        var snapshot = Snapshot();

        snapshot = board.Apply(snapshot, Running("r1", 500));
        snapshot = board.Apply(snapshot, Running("r2", 300));

        Assert.Equal(2, snapshot.Receivers.Count);
        Assert.Equal(800, snapshot.CompletedBytes);
        Assert.Equal(Total * 2, snapshot.TotalBytes);
        Assert.Equal(TransferPhase.Transferring, snapshot.Phase);
    }

    [Fact]
    public void 列表按_peerId_稳定排序()
    {
        var board = new ReceiverBoard(Total);
        var snapshot = Snapshot();

        snapshot = board.Apply(snapshot, Running("zz", 1));
        snapshot = board.Apply(snapshot, Running("aa", 1));
        snapshot = board.Apply(snapshot, Running("mm", 1));

        Assert.Equal(["aa", "mm", "zz"], snapshot.Receivers.Select(r => r.PeerId));
    }

    [Fact]
    public void 完成的链路计满进度且不再有速率()
    {
        var board = new ReceiverBoard(Total);
        var snapshot = Snapshot();

        snapshot = board.Apply(snapshot, Running("r1", 400));
        snapshot = board.Apply(snapshot, Completed("r1"));

        var view = Assert.Single(snapshot.Receivers);
        Assert.True(view.Completed);
        Assert.Equal(Total, view.CompletedBytes);
        Assert.Equal(0, view.BytesPerSecond);
        Assert.Null(view.Error);
    }

    [Fact]
    public void 一条链路失败不影响其他行()
    {
        var board = new ReceiverBoard(Total);
        var snapshot = Snapshot();

        snapshot = board.Apply(snapshot, Running("r1", 600));
        snapshot = board.Apply(snapshot, Failed("r2", "对方断开了"));

        var failed = snapshot.Receivers.Single(r => r.PeerId == "r2");
        Assert.Equal("对方断开了", failed.Error);
        Assert.False(failed.Completed);

        var healthy = snapshot.Receivers.Single(r => r.PeerId == "r1");
        Assert.Null(healthy.Error);
        Assert.Equal(600, healthy.CompletedBytes);
    }

    [Fact]
    public void 单人接收时快照的接收方列表为空_一对一界面不变()
    {
        // MaxReceivers == 1 时 TransferManager 直接走 V1 的 StartSendAsync，
        // 根本不会建 ReceiverBoard —— 这里断言默认快照的形状
        var snapshot = new TransferSnapshot
        {
            Id = "t",
            IsSending = true,
            Phase = TransferPhase.Transferring,
        };

        Assert.Empty(snapshot.Receivers);
        Assert.Equal(1, snapshot.MaxReceivers);
    }
}
