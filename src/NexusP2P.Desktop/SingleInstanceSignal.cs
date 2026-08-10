using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NexusP2P.Desktop;

/// <summary>
/// 让第二次启动去激活已有窗口，而不是起一个新进程。
///
/// <para><b>为什么不能放任两个实例</b>：两个进程会同时写同一个接收目录
/// （<c>.part</c> 文件互相踩），托盘上出现两个一样的图标，
/// 而用户完全不知道自己在跟哪一个说话。</para>
///
/// <para>用「找同名进程 + SetForegroundWindow」而不是命名管道：
/// 要传的信息只有「请你到前台来」这一件事，为它铺一条 IPC 通道不值得。
/// 代价是找错窗口的可能 —— 所以要同时比对进程名与可执行文件路径。</para>
/// </summary>
internal static partial class SingleInstanceSignal
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindowAsync(nint hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint hWnd);

    /// <summary>SW_RESTORE：最小化的窗口要先还原，否则激活了也看不见。</summary>
    private const int ShowRestore = 9;

    public static void ActivateExistingWindow()
    {
        var current = Process.GetCurrentProcess();
        var executable = current.MainModule?.FileName;

        foreach (var process in Process.GetProcessesByName(current.ProcessName))
        {
            using (process)
            {
                if (process.Id == current.Id || process.MainWindowHandle == nint.Zero)
                {
                    continue;
                }

                if (!IsSameExecutable(process, executable))
                {
                    continue;
                }

                Activate(process.MainWindowHandle);
                return;
            }
        }
    }

    /// <summary>
    /// 同名进程不一定是我们自己（用户可能有另一份拷在别处的副本）。
    /// 读不到路径时按「是」处理：宁可激活错一个窗口，也不要开出第二个实例。
    /// </summary>
    private static bool IsSameExecutable(Process process, string? executable)
    {
        if (executable is null)
        {
            return true;
        }

        try
        {
            var candidate = process.MainModule?.FileName;
            return candidate is null ||
                   string.Equals(candidate, executable, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 权限不足或进程刚退出。同上，按「是」处理。
            return true;
        }
    }

    private static void Activate(nint window)
    {
        if (IsIconic(window))
        {
            ShowWindowAsync(window, ShowRestore);
        }

        SetForegroundWindow(window);
    }
}
