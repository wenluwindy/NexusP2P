using NexusP2P.Agent;

namespace NexusP2P.Agent.Tests;

/// <summary>
/// 部署配置文件（<c>nexusp2p.json</c>）。
///
/// <para>重点不是「能读 JSON」，而是<b>读不了的时候会不会说话</b>。
/// 一个写坏的配置文件被静默忽略，用户明明配好了地址，
/// 却对着「必须指定信令服务器」完全不知道问题在哪。</para>
/// </summary>
public sealed class AgentConfigFileTests : IDisposable
{
    private readonly List<string> _temporary = [];

    public void Dispose()
    {
        foreach (var path in _temporary)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    private string WriteConfig(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nexusp2p-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        _temporary.Add(path);
        return path;
    }

    [Fact]
    public void 读得出信令地址()
    {
        var path = WriteConfig("""{ "signaling": "https://p2p.example.com" }""");

        var config = AgentConfigFile.TryLoadFrom(path, out var warning);

        Assert.NotNull(config);
        Assert.Equal("https://p2p.example.com", config.Signaling);
        Assert.Null(warning);
    }

    [Fact]
    public void 文件不存在不算错也不警告()
    {
        // 配置文件是可选的：命令行参数和环境变量都能替代它
        var config = AgentConfigFile.TryLoadFrom(
            Path.Combine(Path.GetTempPath(), $"没有这个文件-{Guid.NewGuid():N}.json"), out var warning);

        Assert.Null(config);
        Assert.Null(warning);
    }

    [Fact]
    public void 允许注释和尾逗号()
    {
        // 这是给人手改的文件。因为一个尾逗号就打不开程序说不过去。
        var path = WriteConfig(
            """
            {
              // 换服务器就改这一行
              "signaling": "https://p2p.example.com",
            }
            """);

        var config = AgentConfigFile.TryLoadFrom(path, out var warning);

        Assert.NotNull(config);
        Assert.Equal("https://p2p.example.com", config.Signaling);
        Assert.Null(warning);
    }

    [Fact]
    public void JSON_写坏了会给出带路径的警告()
    {
        var path = WriteConfig("""{ "signaling": "https://p2p.example.com" """);   // 少个右括号

        var config = AgentConfigFile.TryLoadFrom(path, out var warning);

        Assert.Null(config);
        Assert.NotNull(warning);

        // 警告里必须带上文件路径 —— 否则用户不知道该去改哪个文件
        Assert.Contains(path, warning, StringComparison.Ordinal);
    }

    [Fact]
    public void 空文件会给出警告而不是当成没配()
    {
        var path = WriteConfig("   ");

        var config = AgentConfigFile.TryLoadFrom(path, out var warning);

        Assert.Null(config);
        Assert.NotNull(warning);
    }

    [Fact]
    public void 缺字段时其余字段仍然可用()
    {
        var path = WriteConfig("""{ "signaling": "http://192.168.1.10:5000" }""");

        var config = AgentConfigFile.TryLoadFrom(path, out _);

        Assert.NotNull(config);
        Assert.Equal("http://192.168.1.10:5000", config.Signaling);
        Assert.Null(config.IceServers);
    }

    [Fact]
    public void 读得出兜底的_ICE_服务器()
    {
        var path = WriteConfig(
            """
            {
              "signaling": "https://p2p.example.com",
              "iceServers": ["stun:stun.example.com:3478", "turn:u:p@turn.example.com:3478"]
            }
            """);

        var config = AgentConfigFile.TryLoadFrom(path, out _);

        Assert.NotNull(config);
        Assert.NotNull(config.IceServers);
        Assert.Equal(
            ["stun:stun.example.com:3478", "turn:u:p@turn.example.com:3478"],
            config.IceServers);
    }

    [Fact]
    public void 约定的位置在可执行文件旁边()
    {
        // 不是用户目录：配合「打个包拷到两台电脑」——
        // 改一次配置，整个目录拷过去就都对了
        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, AgentConfigFile.FileName),
            AgentConfigFile.DefaultPath);
    }
}
