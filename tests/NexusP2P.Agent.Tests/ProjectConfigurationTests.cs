using System.Xml.Linq;

namespace NexusP2P.Agent.Tests;

/// <summary>
/// 对工程配置本身的断言。
///
/// <para><b>为什么要用测试去看 csproj</b>：有些约束既不在代码里、也不在
/// 运行时能被发现，但违反它会让程序在特定用户的机器上直接不可用。
/// 这类约束只能钉在构建配置上，那就得有个东西看着构建配置。</para>
/// </summary>
public sealed class ProjectConfigurationTests
{
    private static XDocument LoadProject(string relativePath)
    {
        // 从测试程序集往上找仓库根：bin/Debug/net9.0 → 上三层是工程目录，
        // 再往上两层到仓库根。不写死绝对路径，换机器也能跑。
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NexusP2P.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        var path = Path.Combine(directory.FullName, relativePath);
        Assert.True(File.Exists(path), $"找不到工程文件：{path}");

        return XDocument.Load(path);
    }

    private static string? PropertyValue(XDocument project, string name) =>
        project.Descendants(name).FirstOrDefault()?.Value;

    /// <summary>
    /// 图形界面工程必须显式关掉 <c>InvariantGlobalization</c>。
    ///
    /// <para><b>这条测试对应一个真实发生过的故障。</b>
    /// <c>Directory.Build.props</c> 里为了少一个 ICU 依赖开了
    /// <c>InvariantGlobalization</c>，WPF 工程继承之后，在中文 Windows 上
    /// 一旦有文字输入（IME 会让 WPF 去 <c>new CultureInfo(2052)</c>）就抛：</para>
    ///
    /// <para><c>Only the invariant culture is supported in
    /// globalization-invariant mode. 2052 (0x0804) is an invalid culture
    /// identifier.</c></para>
    ///
    /// <para>它有两个特别难查的性质：一是**只在中文界面的机器上出现**
    /// （英文界面 LCID 1033 走 invariant 路径不报错）；二是**只在输入文字时触发**，
    /// 光启动程序看不出问题 —— 所以基于「进程还活着」的冒烟测试完全放过了它。</para>
    /// </summary>
    [Fact]
    public void 桌面版必须关闭_InvariantGlobalization()
    {
        var project = LoadProject(Path.Combine("src", "NexusP2P.Desktop", "NexusP2P.Desktop.csproj"));

        var value = PropertyValue(project, "InvariantGlobalization");

        Assert.False(
            value is null,
            "NexusP2P.Desktop.csproj 里没有 InvariantGlobalization。" +
            "它会从 Directory.Build.props 继承到 true，导致中文 Windows 上输入文字时崩溃。");

        Assert.True(
            string.Equals(value, "false", StringComparison.OrdinalIgnoreCase),
            $"InvariantGlobalization 必须是 false，实际是 \"{value}\"。");
    }

    /// <summary>
    /// 服务端与命令行反过来 —— 它们应该保持 invariant，少一个 ICU 依赖。
    /// 这条测试防的是「为了修桌面版的问题，顺手把全局的也关了」。
    /// </summary>
    [Fact]
    public void 全局默认仍然开启_InvariantGlobalization()
    {
        var props = LoadProject("Directory.Build.props");

        Assert.Equal("true", PropertyValue(props, "InvariantGlobalization"));
    }

    /// <summary>
    /// 桌面版的产物名不能与命令行版只差大小写。
    ///
    /// <para><b>这条也对应一个真实故障。</b>桌面版一开始叫
    /// <c>NexusP2P</c>，而命令行版叫 <c>nexusp2p</c> —— Windows 文件名
    /// 大小写不敏感，两者是同一个文件名。打包时两个 publish 输出到同一个
    /// 目录，后发布的静默覆盖前一个，**包里最终只剩一个程序**。</para>
    /// </summary>
    [Fact]
    public void 桌面版与命令行版的产物名不能只差大小写()
    {
        var desktop = PropertyValue(
            LoadProject(Path.Combine("src", "NexusP2P.Desktop", "NexusP2P.Desktop.csproj")),
            "AssemblyName");

        var cli = PropertyValue(
            LoadProject(Path.Combine("src", "NexusP2P.Cli", "NexusP2P.Cli.csproj")),
            "AssemblyName");

        Assert.NotNull(desktop);
        Assert.NotNull(cli);

        Assert.False(
            string.Equals(desktop, cli, StringComparison.OrdinalIgnoreCase),
            $"桌面版 \"{desktop}\" 与命令行版 \"{cli}\" 在 Windows 上是同一个文件名，" +
            "打包时会互相覆盖。");
    }

    /// <summary>
    /// 桌面版必须是 <c>WinExe</c>。
    ///
    /// <para>这一行就是「运行时不出现命令行窗口」的全部依据：
    /// <c>Exe</c> 会被标记成控制台子系统，Windows 启动时必然分配一个控制台窗口。</para>
    /// </summary>
    [Fact]
    public void 桌面版必须是_WinExe_才不会弹命令行窗口()
    {
        var project = LoadProject(Path.Combine("src", "NexusP2P.Desktop", "NexusP2P.Desktop.csproj"));

        Assert.Equal("WinExe", PropertyValue(project, "OutputType"));
    }
}
