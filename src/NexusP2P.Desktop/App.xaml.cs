using System.Windows;
using System.Windows.Threading;
using NexusP2P.Agent;
using NexusP2P.Agent.Settings;

namespace NexusP2P.Desktop;

/// <summary>
/// 应用入口。
///
/// <para><b>没有 Main 方法</b> —— WPF 的 SDK 会生成一个带
/// <c>[STAThread]</c> 的入口。配合 csproj 里的 <c>WinExe</c>，
/// 这就是「运行时不出现命令行窗口」的完整实现：进程被标记为 Windows 子系统，
/// 操作系统不为它分配控制台。</para>
/// </summary>
public partial class App : Application, IDisposable
{
    /// <summary>单实例互斥体。用 Local\ 前缀：按登录会话隔离，不跨用户。</summary>
    private const string InstanceMutexName = @"Local\NexusP2P.Desktop.SingleInstance";

    private Mutex? _instanceMutex;
    private MainWindow? _window;

    internal AgentOptions Options { get; private set; } = new();

    internal SettingsStore Settings { get; } = new();

    /// <summary>配置缺失或不合法时的说明。界面启动后要显示出来。</summary>
    internal string? ConfigurationProblem { get; private set; }

    private void OnStartup(object sender, StartupEventArgs e)
    {
        if (!TryClaimSingleInstance())
        {
            // 第二次启动：激活已有窗口而不是起新进程。
            // 两个实例同时跑会让同一个接收目录被两边写，也会让托盘出现两个图标。
            SingleInstanceSignal.ActivateExistingWindow();
            Shutdown();
            return;
        }

        LoadConfiguration();

        // 未处理异常不能让程序直接消失。WinExe 没有控制台，
        // 崩溃时用户什么都看不到，只会觉得「点了没反应」。
        DispatcherUnhandledException += OnUnhandledException;

        // 启动必须单独护住。
        //
        // ShutdownMode 是 OnExplicitShutdown（关窗不退出，为了让传输继续），
        // 于是「窗口没建出来」是个死局：没有窗口、没有托盘图标、没人调
        // Shutdown()，进程就在后台永远挂着 —— 用户看到一个错误框，
        // 然后发现程序**关不掉**，只能去任务管理器杀。
        //
        // 所以这里的 catch 必须真的退出，而不是像 OnUnhandledException
        // 那样标记已处理然后继续。
        try
        {
            _window = new MainWindow();
            _window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"程序启动失败：\n\n{ex.Message}\n\n" +
                "这通常是运行环境的问题。已收到的传输进度保留在磁盘上，不会丢。",
                "NexusP2P 无法启动",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown(1);
        }
    }

    private bool TryClaimSingleInstance()
    {
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirst);

        if (!isFirst)
        {
            _instanceMutex.Dispose();
            _instanceMutex = null;
        }

        return isFirst;
    }

    /// <summary>
    /// 读部署配置（AD-8）：可执行文件旁的 <c>nexusp2p.json</c>，
    /// 环境变量可覆盖。
    ///
    /// <para><b>配置有问题不阻止启动。</b>界面要能打开并把问题讲清楚 ——
    /// 一个打不开的程序没法告诉用户「去改哪个文件的哪一行」。</para>
    /// </summary>
    private void LoadConfiguration()
    {
        var config = AgentConfigFile.TryLoad(out var warning);

        var origin = Environment.GetEnvironmentVariable("NEXUSP2P_SIGNALING")
                     ?? config?.Signaling;

        if (string.IsNullOrWhiteSpace(origin))
        {
            ConfigurationProblem = warning ??
                $"还没有配置信令服务器地址。请在设置页填写，或编辑 {AgentConfigFile.DefaultPath}：" +
                "{ \"signaling\": \"https://你的域名\" }";
            return;
        }

        Options = new AgentOptions
        {
            SignalingOrigin = origin,
            IceServers = config?.IceServers ?? [],
        };

        var problems = Options.Validate();
        ConfigurationProblem = problems.Count > 0
            ? string.Join("；", problems)
            : warning;
    }

    /// <summary>用户在设置页改了信令地址之后调这个。</summary>
    internal void UpdateSignalingOrigin(string origin)
    {
        Options = Options with { SignalingOrigin = origin };
        var problems = Options.Validate();
        ConfigurationProblem = problems.Count > 0 ? string.Join("；", problems) : null;
    }

    /// <summary>
    /// 兜住未处理异常。
    ///
    /// <para><b>能不能继续，取决于界面还在不在。</b>早先这里无条件
    /// <c>Handled = true</c>，结果是启动阶段出错时程序变成一个没有窗口、
    /// 没有托盘、也没法退出的后台进程 —— 用户点掉错误框之后发现
    /// 程序关不掉，只能去任务管理器。</para>
    /// </summary>
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // 界面已经建起来了：一个后台任务出错不该让整个程序消失，
        // 尤其是可能还有另一个传输正在进行。用户随时能自己关。
        if (_window is not null)
        {
            MessageBox.Show(
                $"程序遇到了一个未预料的问题：\n\n{e.Exception.Message}\n\n" +
                "已经收到的传输进度保留在磁盘上，重开程序可以接着传。",
                "NexusP2P",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            e.Handled = true;
            return;
        }

        // 还没有界面 —— 继续下去只会留一个关不掉的隐形进程
        MessageBox.Show(
            $"程序启动过程中出错：\n\n{e.Exception.Message}\n\n" +
            "已收到的传输进度保留在磁盘上，不会丢。",
            "NexusP2P 无法启动",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
        Shutdown(1);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// 释放单实例互斥体。
    ///
    /// <para>进程退出时操作系统本来也会回收它，但显式释放让「第二个实例
    /// 能否立刻启动」不依赖于内核回收的时机。</para>
    /// </summary>
    public void Dispose()
    {
        _instanceMutex?.Dispose();
        _instanceMutex = null;
        GC.SuppressFinalize(this);
    }
}
