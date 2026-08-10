using NexusP2P.Agent.Settings;

namespace NexusP2P.Agent.Tests.Settings;

public sealed class DestinationCheckTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "nexusp2p-dest", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void 正常目录可用()
    {
        Directory.CreateDirectory(_root);

        var status = DestinationCheck.Check(_root);

        Assert.True(status.IsUsable, status.Problem);
        Assert.Null(status.Problem);
    }

    [Fact]
    public void 目录不存在时会被创建()
    {
        var nested = Path.Combine(_root, "还", "不", "存在");

        var status = DestinationCheck.Check(nested);

        Assert.True(status.IsUsable, status.Problem);
        Assert.True(Directory.Exists(nested));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 空目录被拒绝(string? directory)
    {
        var status = DestinationCheck.Check(directory);

        Assert.False(status.IsUsable);
        Assert.NotNull(status.Problem);
    }

    [Fact]
    public void 检查会真去写一个文件()
    {
        // 只看属性是不够的：权限、只读介质、盘符消失只有真写才暴露。
        // 这里验证探针文件用完被清掉了 —— 否则接收目录会积攒垃圾。
        Directory.CreateDirectory(_root);

        var status = DestinationCheck.Check(_root);

        Assert.True(status.IsUsable);
        Assert.Empty(Directory.GetFiles(_root, ".nexusp2p-write-test-*"));
    }

    [Fact]
    public void 空间不足时给出可读的说明()
    {
        Directory.CreateDirectory(_root);

        var status = DestinationCheck.Check(_root, requiredBytes: long.MaxValue / 2);

        Assert.False(status.IsUsable);
        Assert.NotNull(status.Problem);
        Assert.Contains("MiB", status.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void 空间充足时通过()
    {
        Directory.CreateDirectory(_root);

        var status = DestinationCheck.Check(_root, requiredBytes: 1024);

        Assert.True(status.IsUsable, status.Problem);
    }

    [Fact]
    public void 不传所需空间时只检查可写性()
    {
        Directory.CreateDirectory(_root);

        Assert.True(DestinationCheck.Check(_root, requiredBytes: 0).IsUsable);
    }

    [Fact]
    public void 路径不合法时给出说明而不是崩溃()
    {
        var status = DestinationCheck.Check("\0非法路径");

        Assert.False(status.IsUsable);
        Assert.NotNull(status.Problem);
    }
}
