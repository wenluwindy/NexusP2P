using NexusP2P.Agent.Settings;

namespace NexusP2P.Agent.Tests.Settings;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "nexusp2p-settings", Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // 测试清理失败不该让测试失败
        }
    }

    [Fact]
    public void 文件不存在时返回默认值()
    {
        var store = new SettingsStore(SettingsPath);

        var settings = store.Load();

        Assert.Null(settings.ReceiveDirectory);
        Assert.True(settings.ShowNotifications);
        Assert.Null(store.LastLoadWarning);
    }

    [Fact]
    public void 保存后能读回()
    {
        var store = new SettingsStore(SettingsPath);
        var original = new AgentSettings
        {
            ReceiveDirectory = @"D:\我的下载",
            ShowNotifications = false,
            MinimizeToTrayOnClose = false,
        };

        store.Save(original);
        var loaded = new SettingsStore(SettingsPath).Load();

        Assert.Equal(@"D:\我的下载", loaded.ReceiveDirectory);
        Assert.False(loaded.ShowNotifications);
        Assert.False(loaded.MinimizeToTrayOnClose);
    }

    [Fact]
    public void 目录不存在时会被创建()
    {
        var nested = Path.Combine(_directory, "a", "b", "settings.json");
        var store = new SettingsStore(nested);

        store.Save(new AgentSettings());

        Assert.True(File.Exists(nested));
    }

    // ---- 设置文件坏了绝不能挡住启动 ----

    [Fact]
    public void 内容损坏时退回默认值并给出警告()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, "{ 这不是合法的 JSON");

        var store = new SettingsStore(SettingsPath);
        var settings = store.Load();

        Assert.Null(settings.ReceiveDirectory);
        Assert.NotNull(store.LastLoadWarning);
        Assert.Contains("读取失败", store.LastLoadWarning, StringComparison.Ordinal);
    }

    [Fact]
    public void 内容为空时退回默认值()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, "null");

        var store = new SettingsStore(SettingsPath);
        var settings = store.Load();

        Assert.Equal(AgentSettings.DefaultReceiveDirectory, settings.EffectiveReceiveDirectory);
        Assert.NotNull(store.LastLoadWarning);
    }

    [Fact]
    public void 缺字段时用默认值填补()
    {
        // 版本升级后新增字段，老配置文件里没有 —— 不该因此失败
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, """{"receiveDirectory":"D:\\x"}""");

        var settings = new SettingsStore(SettingsPath).Load();

        Assert.Equal(@"D:\x", settings.ReceiveDirectory);
        Assert.True(settings.ShowNotifications);   // 默认值
    }

    [Fact]
    public void 被截断时退回默认值而不是崩溃()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, """{"receiveDirectory":"D:\\x","showNot""");

        var store = new SettingsStore(SettingsPath);

        Assert.Null(store.Load().ReceiveDirectory);
        Assert.NotNull(store.LastLoadWarning);
    }

    [Fact]
    public void 损坏后重新保存能恢复正常()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, "坏掉了");

        var store = new SettingsStore(SettingsPath);
        store.Load();
        store.Save(new AgentSettings { ReceiveDirectory = @"D:\新目录" });

        var reloaded = new SettingsStore(SettingsPath);
        Assert.Equal(@"D:\新目录", reloaded.Load().ReceiveDirectory);
        Assert.Null(reloaded.LastLoadWarning);
    }

    [Fact]
    public void 保存不会留下临时文件()
    {
        var store = new SettingsStore(SettingsPath);

        store.Save(new AgentSettings());

        Assert.False(File.Exists(SettingsPath + ".tmp"), "临时文件应该已被移走");
    }

    // ---- 默认接收目录 ----

    [Fact]
    public void 未设置时用默认接收目录()
    {
        var settings = new AgentSettings();

        Assert.Equal(AgentSettings.DefaultReceiveDirectory, settings.EffectiveReceiveDirectory);
        Assert.Contains("NexusP2P", settings.EffectiveReceiveDirectory, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void 空白的接收目录视为未设置(string value)
    {
        var settings = new AgentSettings { ReceiveDirectory = value };

        Assert.Equal(AgentSettings.DefaultReceiveDirectory, settings.EffectiveReceiveDirectory);
    }

    [Fact]
    public void 设置了就用设置的()
    {
        var settings = new AgentSettings { ReceiveDirectory = @"E:\收件箱" };

        Assert.Equal(@"E:\收件箱", settings.EffectiveReceiveDirectory);
    }
}
