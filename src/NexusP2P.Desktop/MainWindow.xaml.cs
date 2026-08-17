using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using NexusP2P.Agent;
using NexusP2P.Agent.Settings;
using NexusP2P.Agent.Transfers;
using NexusP2P.Desktop.Updates;

namespace NexusP2P.Desktop;

/// <summary>
/// 主窗口。
///
/// <para>这里只做三件事：把用户操作转成 <see cref="TransferManager"/> 的调用、
/// 把快照画到界面上、以及处理托盘与关窗。<b>没有任何协议逻辑</b> ——
/// 那些都在 Agent 与 Transfer 里，与界面无关，也因此能被无界面地测试。</para>
/// </summary>
public partial class MainWindow : Window, IDisposable
{
    private readonly App _app = (App)Application.Current;
    private readonly TransferManager _manager;
    private readonly TrayPresence _tray;
    private readonly UpdateService _updateService = new();
    private readonly CancellationTokenSource _updateCts = new();

    private string? _selectedPath;
    private UpdateRelease? _availableUpdate;
    private string? _downloadedInstallerPath;
    private AgentSettings _settings;
    private bool _updateBusy;
    private bool _disposed;

    /// <summary>「程序缩到托盘了」这句提示只说一次，不必每次关窗都弹。</summary>
    private bool _minimizeExplained;

    public MainWindow()
    {
        InitializeComponent();

        _settings = _app.Settings.Load();
        _manager = new TransferManager(_app.Options, _app.Settings);

        // 事件在后台线程上触发，必须切回界面线程才能碰控件
        _manager.SnapshotChanged += snapshot =>
            Dispatcher.BeginInvoke(() => Render(snapshot));

        _tray = new TrayPresence(this);

        LoadSettingsIntoUi();
        ShowConfigurationProblem();
    }

    private void LoadSettingsIntoUi()
    {
        ReceiveFolder.Text = _settings.EffectiveReceiveDirectory;
        NotificationsCheck.IsChecked = _settings.ShowNotifications;
        TrayCheck.IsChecked = _settings.MinimizeToTrayOnClose;

        // 托盘建不起来时，把这个开关灰掉并说明原因。
        // 留着一个勾了却不生效的开关，比直接告诉用户「这里用不了」更糟。
        if (!_tray.IsAvailable)
        {
            TrayCheck.IsEnabled = false;
            TrayNote.Text = $"托盘图标不可用，关闭窗口将直接退出程序。（{_tray.Problem}）";
            TrayNote.Visibility = Visibility.Visible;
        }
        SignalingInput.Text = _app.Options.SignalingOrigin;
        ConfigPathText.Text = $"配置文件：{AgentConfigFile.DefaultPath}";
        UpdateVersionText.Text = $"当前版本 {UpdateService.CurrentVersion.ToString(3)}";

        // 设置文件损坏时如实说明，而不是默默用了默认值
        if (_app.Settings.LastLoadWarning is { } warning)
        {
            ShowWarning(warning);
        }
    }

    private void ShowConfigurationProblem()
    {
        if (_app.ConfigurationProblem is not { } problem)
        {
            return;
        }

        ShowWarning(problem);

        // 没有信令地址就什么都做不了 —— 直接把用户领到设置页，
        // 而不是让他点了发送才发现不行
        if (string.IsNullOrWhiteSpace(_app.Options.SignalingOrigin))
        {
            Tabs.SelectedIndex = 2;
        }
    }

    private void ShowWarning(string text)
    {
        ConfigWarningText.Text = text;
        ConfigWarning.Visibility = Visibility.Visible;
    }

    // ---------------- 发送 ----------------

    private void OnPickFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择要发送的文件", Multiselect = false };
        if (dialog.ShowDialog(this) == true)
        {
            SelectPath(dialog.FileName);
        }
    }

    private void OnPickFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择要发送的文件夹" };
        if (dialog.ShowDialog(this) == true)
        {
            SelectPath(dialog.FolderName);
        }
    }

    /// <summary>
    /// 拖放。
    ///
    /// <para>一次只接一个 —— 清单的顶层名字来自这一个路径，
    /// 混合多个来源会让接收端的目录结构变得没法预期。</para>
    /// </summary>
    private void OnDrop(object sender, DragEventArgs e)
    {
        OnDragLeave(sender, e);

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths)
        {
            return;
        }

        if (paths.Length > 1)
        {
            ShowWarning("一次只能发送一个文件或文件夹。已取用第一个：" + Path.GetFileName(paths[0]));
        }

        SelectPath(paths[0]);
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        var isFile = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = isFile ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;

        if (isFile)
        {
            DropZone.BorderBrush = (Brush)FindResource("Primary");
        }
    }

    private void OnDragLeave(object sender, DragEventArgs e) =>
        DropZone.BorderBrush = (Brush)FindResource("BorderStrong");

    private void SelectPath(string path)
    {
        _selectedPath = path;

        var isDirectory = Directory.Exists(path);
        SelectedPath.Text = isDirectory
            ? $"已选择文件夹：{path}"
            : $"已选择文件：{path}（{FormatSize(new FileInfo(path).Length)}）";

        SelectedPath.Visibility = Visibility.Visible;
        SendButton.IsEnabled = !string.IsNullOrWhiteSpace(_app.Options.SignalingOrigin);

        if (!SendButton.IsEnabled)
        {
            ShowWarning("还没有配置信令服务器地址，请先在设置页填写。");
        }
    }

    private void OnStartSend(object sender, RoutedEventArgs e)
    {
        if (_selectedPath is null)
        {
            return;
        }

        SendButton.IsEnabled = false;
        CodePanel.Visibility = Visibility.Visible;
        CodeText.Text = "正在计算校验和…";
        ShareUrlText.Text = string.Empty;

        // 刻意不 await：界面要立刻回到可响应状态，进度靠 SnapshotChanged 推。
        // 异常已经在 TransferManager 里被翻译成快照上的 Error 字段。
        _ = _manager.StartSendManyAsync(_selectedPath, ReadMaxPeers());
    }

    /// <summary>
    /// 读「允许接收人数」。默认 1 = 一对一（V1 行为）。
    ///
    /// <para>只保证下界（至少 1 个人）—— 上界不在这里定：信令服务器会把请求
    /// 夹到它自己配置的席位上限，并在建房应答里回显生效值。</para>
    /// </summary>
    private int ReadMaxPeers()
    {
        if (!int.TryParse(MaxPeersInput.Text.Trim(), out var value) || value < 1)
        {
            MaxPeersInput.Text = "1";
            return 1;
        }

        return value;
    }

    /// <summary>接收人数框只收数字。粘贴进来的非数字由 ReadMaxPeers 兜底。</summary>
    private void OnMaxPeersTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = !e.Text.All(char.IsAsciiDigit);

    private void OnCopyCode(object sender, RoutedEventArgs e) => CopyToClipboard(CodeText.Text, "文件码");

    private void OnCopyLink(object sender, RoutedEventArgs e) =>
        CopyToClipboard(ShareUrlText.Text, "分享链接");

    // ---------------- 接收 ----------------

    private void OnReceiveInputChanged(object sender, TextChangedEventArgs e)
    {
        // 粘贴完整分享链接时密钥已经在里面了，把密钥框灰掉省得用户以为漏填了
        var hasLink = ReceiveInput.Text.Contains('#', StringComparison.Ordinal);
        ReceiveKey.IsEnabled = !hasLink;

        if (hasLink)
        {
            ReceiveKey.Text = string.Empty;
        }
    }

    private void OnPickFolderForReceive(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择接收目录",
            InitialDirectory = _settings.EffectiveReceiveDirectory,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        // 立刻持久化（AD-9：选了之后要记住）
        _settings = _settings with { ReceiveDirectory = dialog.FolderName };
        _app.Settings.Save(_settings);
        ReceiveFolder.Text = dialog.FolderName;
    }

    private void OnStartReceive(object sender, RoutedEventArgs e)
    {
        var target = ReceiveInput.Text.Trim();
        if (target.Length == 0)
        {
            ShowWarning("请先填入分享链接或九位文件码。");
            return;
        }

        if (string.IsNullOrWhiteSpace(_app.Options.SignalingOrigin))
        {
            ShowWarning("还没有配置信令服务器地址，请先在设置页填写。");
            Tabs.SelectedIndex = 2;
            return;
        }

        ReceiveButton.IsEnabled = false;
        LandedPanel.Visibility = Visibility.Collapsed;
        ReceiveProgressPanel.Visibility = Visibility.Visible;

        _ = _manager.StartReceiveAsync(
            target,
            ReceiveKey.IsEnabled ? ReceiveKey.Text : null,
            _settings.EffectiveReceiveDirectory);
    }

    private void OnOpenReceiveFolder(object sender, RoutedEventArgs e)
    {
        var folder = _settings.EffectiveReceiveDirectory;

        if (!Directory.Exists(folder))
        {
            ShowWarning($"目录不存在：{folder}");
            return;
        }

        // UseShellExecute：这是让资源管理器打开一个目录，不是执行程序
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _manager.Cancel();

    /// <summary>设置页里的「退出程序」。不依赖托盘图标能被找到。</summary>
    private void OnQuit(object sender, RoutedEventArgs e) => RequestQuit();

    // ---------------- 设置 ----------------

    private void OnSaveSignaling(object sender, RoutedEventArgs e)
    {
        var origin = SignalingInput.Text.Trim();
        _app.UpdateSignalingOrigin(origin);

        if (_app.ConfigurationProblem is { } problem)
        {
            ShowWarning(problem);
            return;
        }

        ConfigWarning.Visibility = Visibility.Collapsed;

        MessageBox.Show(
            this,
            "已保存到本次会话。\n\n" +
            $"要让它在下次启动时也生效，请把它写进 {AgentConfigFile.DefaultPath}：\n" +
            $"{{ \"signaling\": \"{origin}\" }}",
            "NexusP2P",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OnSettingsToggled(object sender, RoutedEventArgs e)
    {
        _settings = _settings with
        {
            ShowNotifications = NotificationsCheck.IsChecked == true,
            MinimizeToTrayOnClose = TrayCheck.IsChecked == true,
        };

        _app.Settings.Save(_settings);
    }

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        if (_updateBusy)
        {
            return;
        }

        try
        {
            if (_downloadedInstallerPath is { } installer && File.Exists(installer))
            {
                StartInstaller(installer);
                return;
            }

            if (_availableUpdate is { } release)
            {
                await DownloadAndInstallAsync(release);
                return;
            }

            await CheckForUpdateAsync();
        }
        catch (OperationCanceledException) when (_updateCts.IsCancellationRequested)
        {
            // 窗口退出时取消网络操作，不再更新已经销毁的界面。
        }
        catch (Exception ex) when (
            ex is HttpRequestException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            UpdateStatusText.Text = $"更新失败：{DescribeUpdateError(ex)}";
            UpdateButton.Content = _availableUpdate is null ? "重新检查" : "重新下载";
            UpdateProgressBar.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _updateBusy = false;
            if (!_disposed)
            {
                UpdateButton.IsEnabled = true;
            }
        }
    }

    private async Task CheckForUpdateAsync()
    {
        _updateBusy = true;
        UpdateButton.IsEnabled = false;
        UpdateButton.Content = "正在检查";
        UpdateStatusText.Text = "正在连接 GitHub Releases…";
        UpdateProgressBar.Visibility = Visibility.Collapsed;

        var release = await _updateService.CheckAsync(
            UpdateService.CurrentVersion,
            _updateCts.Token);

        if (release is null)
        {
            UpdateStatusText.Text = "当前已是最新正式版本。";
            UpdateButton.Content = "再次检查";
            return;
        }

        _availableUpdate = release;
        var size = release.Size > 0 ? $"，{FormatSize(release.Size)}" : string.Empty;
        UpdateStatusText.Text = $"发现新版本 {release.Tag}{size}。";
        UpdateButton.Content = "下载并安装";
    }

    private async Task DownloadAndInstallAsync(UpdateRelease release)
    {
        _updateBusy = true;
        UpdateButton.IsEnabled = false;
        UpdateButton.Content = "正在下载";
        UpdateStatusText.Text = $"正在下载 {release.Tag}…";
        UpdateProgressBar.Value = 0;
        UpdateProgressBar.Visibility = Visibility.Visible;

        var progress = new Progress<UpdateDownloadProgress>(value =>
        {
            UpdateProgressBar.Value = value.Fraction;
            UpdateStatusText.Text = value.TotalBytes > 0
                ? $"正在下载 {release.Tag}：{value.Percentage}  " +
                  $"{FormatSize(value.DownloadedBytes)} / {FormatSize(value.TotalBytes)}"
                : $"正在下载 {release.Tag}：{FormatSize(value.DownloadedBytes)}";
        });

        _downloadedInstallerPath = await _updateService.DownloadAsync(
            release,
            progress,
            _updateCts.Token);

        UpdateProgressBar.Value = 1;
        UpdateStatusText.Text = "下载完成，正在打开安装程序…";
        UpdateButton.Content = "打开安装程序";
        StartInstaller(_downloadedInstallerPath);
    }

    private void StartInstaller(string installerPath)
    {
        if (_manager.IsBusy)
        {
            UpdateStatusText.Text = "安装程序已下载。请在当前传输结束后点击“打开安装程序”。";
            UpdateButton.Content = "打开安装程序";
            return;
        }

        using var process = Process.Start(new ProcessStartInfo(installerPath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(installerPath),
        });

        if (process is null)
        {
            throw new IOException("Windows 未能启动安装程序。");
        }

        Shutdown();
    }

    private static string DescribeUpdateError(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden } =>
            "GitHub 暂时限制了请求，请稍后再试。",
        HttpRequestException => "无法连接 GitHub，请检查网络后重试。",
        UnauthorizedAccessException => "没有权限写入本地更新目录。",
        _ => exception.Message,
    };

    // ---------------- 渲染 ----------------

    private void Render(TransferSnapshot snapshot)
    {
        if (snapshot.IsSending)
        {
            RenderSend(snapshot);
        }
        else
        {
            RenderReceive(snapshot);
        }

        _tray.Update(snapshot);
        NotifyIfFinished(snapshot);
    }

    private void RenderSend(TransferSnapshot snapshot)
    {
        SendProgressPanel.Visibility = Visibility.Visible;
        SendStatus.Text = DescribePhase(snapshot);
        SendConnection.Text = DescribeConnection(snapshot);
        SendBar.Value = snapshot.Fraction;
        SendNumbers.Text = DescribeNumbers(snapshot);
        SendEta.Text = DescribeEta(snapshot);
        SendBottleneck.Text = DescribeBottleneck(snapshot);

        if (snapshot.Code is { } code)
        {
            CodeText.Text = code;
        }

        if (snapshot.ShareUrl is { } url)
        {
            ShareUrlText.Text = url;
        }

        RenderReceivers(snapshot);

        var busy = IsBusyPhase(snapshot.Phase);
        SendCancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        SendButton.IsEnabled = !busy && _selectedPath is not null;
    }

    /// <summary>
    /// 接收方列表（V2）。一人接收（Receivers 为空）时保持折叠 ——
    /// 一对一的界面与 V1 视觉等价，不为多路把单路搞复杂。
    /// </summary>
    private void RenderReceivers(TransferSnapshot snapshot)
    {
        if (snapshot.Receivers.Count == 0)
        {
            ReceiverList.Visibility = Visibility.Collapsed;
            ReceiverList.ItemsSource = null;
            return;
        }

        ReceiverList.Visibility = Visibility.Visible;
        ReceiverList.ItemsSource = snapshot.Receivers
            .Select(r => new ReceiverRow(r))
            .ToArray();
    }

    private void RenderReceive(TransferSnapshot snapshot)
    {
        ReceiveProgressPanel.Visibility = Visibility.Visible;
        ReceiveStatus.Text = DescribePhase(snapshot);
        ReceiveConnection.Text = DescribeConnection(snapshot);
        ReceiveBar.Value = snapshot.Fraction;
        ReceiveNumbers.Text = DescribeNumbers(snapshot);
        ReceiveEta.Text = DescribeEta(snapshot);
        ReceiveBottleneck.Text = DescribeBottleneck(snapshot);

        var busy = IsBusyPhase(snapshot.Phase);
        ReceiveCancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ReceiveButton.IsEnabled = !busy;

        if (snapshot.Phase == TransferPhase.Completed && snapshot.LandedFiles.Count > 0)
        {
            LandedTitle.Text = $"已接收 {snapshot.LandedFiles.Count} 个文件：";
            LandedFiles.ItemsSource = snapshot.LandedFiles;
            LandedPanel.Visibility = Visibility.Visible;
        }
    }

    private static bool IsBusyPhase(TransferPhase phase) =>
        phase is TransferPhase.Preparing or TransferPhase.WaitingForPeer
            or TransferPhase.Connecting or TransferPhase.Transferring or TransferPhase.Verifying;

    private static string DescribePhase(TransferSnapshot snapshot) => snapshot.Phase switch
    {
        TransferPhase.Preparing => "正在计算校验和…",

        TransferPhase.WaitingForPeer when snapshot.MaxReceivers > 1 =>
            $"等待接收（最多 {snapshot.MaxReceivers} 人）…",

        TransferPhase.WaitingForPeer => "等待对方接收…",

        // 重连中要显示第几次 —— 悄悄重试只会让「网络确实不通」这件事
        // 被推迟十几秒，而用户对着一个不动的进度条不知道发生了什么
        TransferPhase.Connecting when snapshot.ReconnectAttempt > 0 =>
            $"连接断开，正在重连（第 {snapshot.ReconnectAttempt}/{snapshot.ReconnectMaxAttempts} 次）…",

        TransferPhase.Connecting => "正在建立连接…",

        TransferPhase.Transferring when snapshot.Receivers.Count > 0 =>
            $"正在传输（{snapshot.Receivers.Count(r => !r.Completed && r.Error is null)} 人接收中，" +
            $"整体 {snapshot.Fraction * 100:N0}%）",

        TransferPhase.Transferring => "正在传输",
        TransferPhase.Verifying => "正在校验已有进度…",
        TransferPhase.Completed => "完成",
        TransferPhase.Cancelled => "已取消（进度已保留，可以接着传）",
        TransferPhase.Failed => snapshot.Error ?? "传输失败",
        _ => string.Empty,
    };

    private static string DescribeConnection(TransferSnapshot snapshot) => snapshot.Bottleneck switch
    {
        Bottleneck.Relay => "经服务器中继",
        Bottleneck.DirectLink => "直连",
        _ => string.Empty,
    };

    private static string DescribeNumbers(TransferSnapshot snapshot)
    {
        if (snapshot.TotalBytes == 0)
        {
            return string.Empty;
        }

        return $"{snapshot.Fraction * 100:N1}%  " +
               $"{FormatSize(snapshot.CompletedBytes)} / {FormatSize(snapshot.TotalBytes)}  " +
               $"{FormatSize((long)snapshot.BytesPerSecond)}/s";
    }

    private static string DescribeEta(TransferSnapshot snapshot) =>
        snapshot.Remaining is { } remaining ? $"剩余 {FormatDuration(remaining)}" : string.Empty;

    /// <summary>
    /// 瓶颈说明。用户看到 3 MB/s 时第一反应是「是不是坏了」，
    /// 应该直接告诉他为什么。
    /// </summary>
    private static string DescribeBottleneck(TransferSnapshot snapshot) => snapshot.Bottleneck switch
    {
        Bottleneck.Hashing => "正在算校验和，这一步只用本机 CPU，还没开始传",
        Bottleneck.Relay => "走中继中 —— 速度受中继服务器上行带宽限制，不是你的网络问题",
        Bottleneck.DirectLink => "直连中 —— 速度取决于双方的物理带宽，这就是最快的路径",
        Bottleneck.PeerBackpressure => "对方处理不过来（下行带宽或磁盘写入已满），正在等它消费",
        Bottleneck.Reconnecting => "正在重连。已收到的进度不会丢，会接着传",
        _ => string.Empty,
    };

    private void NotifyIfFinished(TransferSnapshot snapshot)
    {
        if (!_settings.ShowNotifications)
        {
            return;
        }

        switch (snapshot.Phase)
        {
            case TransferPhase.Completed when snapshot.TotalBytes > 0:
                _tray.Notify("传输完成", snapshot.IsSending
                    ? "对方已确认收齐并通过校验。"
                    : $"已接收 {snapshot.LandedFiles.Count} 个文件。");
                break;

            case TransferPhase.Failed:
                _tray.Notify("传输失败", snapshot.Error ?? "未知原因。");
                break;
        }
    }

    // ---------------- 关窗与托盘 ----------------

    /// <summary>
    /// 关窗不中断传输：最小化到托盘。
    ///
    /// <para>这是明确要求的行为 —— 20 GB 传到一半时误点关闭按钮，
    /// 不该让四十分钟的进度作废（虽然进度按内容记录能续传，
    /// 但重新握手与重扫仍要花时间）。</para>
    /// </summary>
    private void OnClosing(object sender, CancelEventArgs e)
    {
        // 托盘不可用时**绝不能**最小化：那会让程序变成一个没有任何
        // 可见入口、也没法退出的后台进程，只能去任务管理器杀。
        if (_settings.MinimizeToTrayOnClose && _tray.IsAvailable)
        {
            e.Cancel = true;
            Hide();

            // **每次缩到托盘都要说一声**（至少第一次）。
            //
            // 早先只在有传输时才提示，于是空闲时关窗是完全静默的 ——
            // 用户以为程序退了，实际它还在托盘里，而托盘图标很容易被
            // 折叠进「显示隐藏的图标」里看不见。结果就是「这软件关不掉」。
            if (_manager.IsBusy)
            {
                _tray.Notify("传输继续进行中",
                    "程序已最小化到托盘，传输没有中断。双击托盘图标可以打开窗口。");
            }
            else if (!_minimizeExplained)
            {
                _minimizeExplained = true;
                _tray.Notify("程序仍在托盘中运行",
                    "双击托盘图标可以打开窗口；要完全退出请用托盘右键菜单的「退出」，" +
                    "或在设置页关掉「关闭窗口时最小化到托盘」。");
            }

            return;
        }

        if (_manager.IsBusy && !ConfirmQuitWhileBusy())
        {
            e.Cancel = true;
            return;
        }

        Shutdown();
    }

    private bool ConfirmQuitWhileBusy()
    {
        // 一对多时把「还有几个人在收」说清楚 —— 中断影响的不止一个人
        var receiving = _manager.Snapshot.Receivers.Count(r => !r.Completed && r.Error is null);
        var headline = receiving > 1
            ? $"还有 {receiving} 人正在接收。现在退出会同时中断他们。"
            : "还有传输正在进行。现在退出会中断它。";

        return MessageBox.Show(
            this,
            headline + "\n\n已收到的部分会保留在磁盘上，之后用同一个文件码可以接着传。",
            "确认退出",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning) == MessageBoxResult.OK;
    }

    /// <summary>托盘菜单的「退出」走这里。</summary>
    internal void RequestQuit()
    {
        if (_manager.IsBusy && !ConfirmQuitWhileBusy())
        {
            return;
        }

        Shutdown();
    }

    private void Shutdown()
    {
        Dispose();
        Application.Current.Shutdown();
    }

    /// <summary>
    /// 释放托盘图标与传输管理器。
    ///
    /// <para>托盘图标必须显式释放 —— 不释放的话它会一直留在托盘上直到
    /// 用户把鼠标移过去，那是个经典的「僵尸托盘图标」。</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _manager.Cancel();
        _updateCts.Cancel();
        _tray.Dispose();
        _updateService.Dispose();
        _updateCts.Dispose();

        // TransferManager 的释放是同步完成的（只是取消 + 释放 CTS），
        // 所以这里同步等待不会阻塞界面线程。
        _manager.DisposeAsync().AsTask().GetAwaiter().GetResult();

        GC.SuppressFinalize(this);
    }

    internal void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):N2} GiB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):N1} MiB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):N0} KiB",
        _ => $"{bytes} B",
    };

    private static string FormatDuration(TimeSpan span) => span switch
    {
        { TotalHours: >= 1 } => $"{(int)span.TotalHours} 小时 {span.Minutes} 分",
        { TotalMinutes: >= 1 } => $"{span.Minutes} 分 {span.Seconds} 秒",
        _ => $"{Math.Max(1, span.Seconds)} 秒",
    };

    private void CopyToClipboard(string text, string what)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
            _tray.Notify("已复制", $"{what}已复制到剪贴板。");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // 剪贴板被别的进程占着。这不值得弹错误框打断用户 ——
            // 文本就在界面上，选中手动复制即可。
            ShowWarning($"剪贴板暂时不可用，请手动选中{what}复制。");
        }
    }
}

/// <summary>
/// 接收方列表里的一行（V2）。把 <see cref="ReceiverView"/> 翻译成
/// 界面直接绑定的字符串 —— 模板里不放逻辑。
/// </summary>
internal sealed record ReceiverRow(ReceiverView View)
{
    public string Title => View switch
    {
        { Completed: true } => $"接收方 {View.PeerId} — 已收齐并通过校验",
        { Error: not null } => $"接收方 {View.PeerId} — 失败",
        _ => $"接收方 {View.PeerId}",
    };

    public string Detail => View switch
    {
        { Completed: true } => MainWindow.FormatSize(View.TotalBytes),
        { Error: not null } => View.Error!,
        _ => $"{View.Fraction * 100:N1}%  {MainWindow.FormatSize(View.CompletedBytes)}" +
             $" / {MainWindow.FormatSize(View.TotalBytes)}" +
             $"  {MainWindow.FormatSize((long)View.BytesPerSecond)}/s" +
             View.Bottleneck switch
             {
                 Bottleneck.Relay => "（中继）",
                 Bottleneck.DirectLink => "（直连）",
                 _ => string.Empty,
             },
    };

    public double Fraction => View.Fraction;
}
