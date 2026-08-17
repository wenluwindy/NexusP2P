using System.Drawing;
using System.IO;
using System.Windows.Forms;
using NexusP2P.Agent.Transfers;

namespace NexusP2P.Desktop;

/// <summary>
/// 托盘图标：关窗后程序仍在跑，托盘是它唯一的可见入口。
///
/// <para><b>为什么用 WinForms 的 NotifyIcon</b>：WPF 至今没有托盘 API。
/// 可选项是引入一个第三方库（比如 Hardcodet.NotifyIcon.Wpf）或者启用
/// WinForms 互操作。选后者 —— NotifyIcon 就是对 Shell_NotifyIcon 的一层
/// 薄封装，而多一个 NuGet 依赖要多一份供应链与升级负担。</para>
///
/// <para>气泡通知也走它。Windows 10 之后系统会把气泡转成通知中心的
/// toast，所以行为与原生通知一致，不需要额外做 WinRT 那一套。</para>
/// </summary>
internal sealed class TrayPresence : IDisposable
{
    private readonly MainWindow _window;

    /// <summary>托盘图标。为 null 表示建失败了，见 <see cref="IsAvailable"/>。</summary>
    private readonly NotifyIcon? _icon;

    private bool _disposed;

    public TrayPresence(MainWindow window)
    {
        _window = window;

        // 托盘建不起来不能让程序起不来。
        //
        // 但**必须记下来**（IsAvailable）：托盘是关窗之后唯一的入口，
        // 没有它还照着「关窗最小化到托盘」走，程序就变成一个看不见、
        // 也关不掉的后台进程。
        try
        {
            _icon = new NotifyIcon
            {
                Icon = LoadIcon(),
                Visible = true,
                Text = "NexusP2P",
                ContextMenuStrip = BuildMenu(),
            };

            _icon.DoubleClick += (_, _) => _window.RestoreFromTray();
            IsAvailable = true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _icon = null;
            IsAvailable = false;
            Problem = ex.Message;
        }
    }

    /// <summary>
    /// 托盘图标是否真的在。
    ///
    /// <para>为 false 时关窗必须直接退出，不能最小化 ——
    /// 否则程序会变成一个没有任何可见入口的进程。</para>
    /// </summary>
    public bool IsAvailable { get; }

    /// <summary>托盘不可用的原因，可以显示给用户。</summary>
    public string? Problem { get; }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add("打开窗口", null, (_, _) => _window.RestoreFromTray());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => _window.RequestQuit());

        return menu;
    }

    /// <summary>
    /// 从可执行文件自身取图标。
    ///
    /// <para>比嵌一份资源再读出来简单，而且永远与窗口图标一致 ——
    /// 两份图标不同步是个小而持久的膈应。</para>
    /// </summary>
    private static Icon LoadIcon()
    {
        try
        {
            var executable = Environment.ProcessPath;
            if (executable is not null && Icon.ExtractAssociatedIcon(executable) is { } icon)
            {
                return icon;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            // 单文件发布等情形下可能取不到
        }

        // 兜底：没有图标也要有托盘入口，否则关窗之后程序就彻底看不见了
        return SystemIcons.Application;
    }

    /// <summary>
    /// 把进度写进悬浮提示。
    ///
    /// <para>关窗之后这是用户唯一能看到的进度 —— 鼠标悬到托盘上就能知道
    /// 还剩多少，不必为了看一眼进度把窗口翻出来。</para>
    /// </summary>
    public void Update(TransferSnapshot snapshot)
    {
        if (_disposed || _icon is null)
        {
            return;
        }

        var text = snapshot.Phase switch
        {
            TransferPhase.Preparing => "NexusP2P — 正在计算校验和",
            TransferPhase.WaitingForPeer => "NexusP2P — 等待对方接收",
            TransferPhase.Connecting => "NexusP2P — 正在连接",

            // 一对多（V2）：把「几个人在收」说出来
            TransferPhase.Transferring when snapshot.Receivers.Count > 0 =>
                $"NexusP2P — {snapshot.Receivers.Count(r => !r.Completed && r.Error is null)} 人接收中，" +
                $"整体 {snapshot.Fraction * 100:N0}%",

            TransferPhase.Transferring =>
                $"NexusP2P — {snapshot.Fraction * 100:N0}%，" +
                $"{MainWindow.FormatSize((long)snapshot.BytesPerSecond)}/s",
            TransferPhase.Verifying => "NexusP2P — 正在校验",
            TransferPhase.Failed => "NexusP2P — 传输失败",
            _ => "NexusP2P",
        };

        // Text 上限 63 个字符，超了会抛 ArgumentException。
        // 中文提示不至于超，但速度数字的位数是变化的，截一下更稳。
        _icon.Text = text.Length <= 63 ? text : text[..63];
    }

    public void Notify(string title, string message)
    {
        if (_disposed || _icon is null)
        {
            return;
        }

        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = message;
        _icon.ShowBalloonTip(5000);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_icon is null)
        {
            return;
        }

        // 必须显式设 Visible = false：只 Dispose 的话图标会留在托盘上
        // 直到用户把鼠标移过去，那是个经典的「僵尸托盘图标」
        _icon.Visible = false;
        _icon.Dispose();
    }
}
