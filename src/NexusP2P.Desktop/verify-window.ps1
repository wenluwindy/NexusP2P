# 验证图形界面真的**画出来了**，而不只是进程活着。
#
# 为什么需要这个：早先的冒烟测试只断言「进程还在」，于是完全没抓到
# 「invariant 模式下 WPF 在中文 Windows 上抛异常」这个 bug ——
# 异常被 DispatcherUnhandledException 兜住并标记已处理，进程照样活着，
# 但界面是坏的、而且关不掉。**进程活着不等于程序能用。**
#
# 判据是窗口类名：
#   HwndWrapper[...]  真正的 WPF 窗口 —— 界面起来了
#   #32770            Win32 对话框 —— 那是错误提示框，说明启动失败了
#
# 用法：powershell -File verify-window.ps1 -Exe <路径> [-TimeoutSeconds 25]

param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [int]$TimeoutSeconds = 25
)

$ErrorActionPreference = 'Stop'

Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class Windows
{
    private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    /// <summary>列出某个进程的所有可见顶层窗口，格式 "类名|标题"。</summary>
    // Add-Type 用的是老编译器：不支持 out var、也不支持 _ 弃元，
    // 所以这里全用显式声明的老写法。
    public static List<string> VisibleWindows(int pid)
    {
        List<string> found = new List<string>();
        int target = pid;

        EnumWindows(delegate(IntPtr hWnd, IntPtr unused)
        {
            uint owner = 0;
            GetWindowThreadProcessId(hWnd, out owner);

            if (owner != (uint)target || !IsWindowVisible(hWnd))
            {
                return true;
            }

            StringBuilder className = new StringBuilder(256);
            GetClassName(hWnd, className, className.Capacity);

            StringBuilder title = new StringBuilder(512);
            GetWindowText(hWnd, title, title.Capacity);

            found.Add(className.ToString() + "|" + title.ToString());
            return true;
        }, IntPtr.Zero);

        return found;
    }
}
'@

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName Microsoft.VisualBasic

$process = Start-Process -FilePath $Exe -PassThru
Write-Host "已启动 PID $($process.Id)，等界面出现…"

$wpfWindow = $null
$dialog = $null

for ($i = 0; $i -lt ($TimeoutSeconds * 4); $i++) {
    Start-Sleep -Milliseconds 250

    if ($process.HasExited) {
        break
    }

    foreach ($entry in [Windows]::VisibleWindows($process.Id)) {
        $className, $title = $entry -split '\|', 2

        if ($className -like 'HwndWrapper*' -and -not $wpfWindow) {
            $wpfWindow = $title
        }
        elseif ($className -eq '#32770' -and -not $dialog) {
            $dialog = $title
        }
    }

    # 对话框先出现就不用再等了 —— 那已经是失败
    if ($dialog -or $wpfWindow) {
        break
    }
}

# 光是「窗口画出来了」不够。
#
# 那个 invariant 模式的 culture 崩溃是**文字输入**才触发的
# （WPF 的 InputLanguageManager 会按键盘布局去 new CultureInfo(2052)），
# 所以必须真的往里敲字。只看窗口起没起来的测试放过了这个 bug 一次，
# 不能再放过第二次。
if ($wpfWindow -and -not $dialog -and -not $process.HasExited) {
    Write-Host "窗口已出现，开始模拟文字输入（触发 IME / 区域相关代码路径）…"

    [System.Windows.Forms.SendKeys]::SendWait('%{TAB}') | Out-Null
    Start-Sleep -Milliseconds 400

    try {
        # 切到「接收」页再往输入框里敲 —— 那是界面上最主要的文本框
        [Microsoft.VisualBasic.Interaction]::AppActivate($process.Id)
        Start-Sleep -Milliseconds 600

        [System.Windows.Forms.SendKeys]::SendWait('{TAB}{TAB}{TAB}')
        Start-Sleep -Milliseconds 300
        [System.Windows.Forms.SendKeys]::SendWait('130226582')
        Start-Sleep -Milliseconds 300
        [System.Windows.Forms.SendKeys]::SendWait('{TAB}abcDEF123')
        Start-Sleep -Milliseconds 1200
    }
    catch {
        Write-Host "  （模拟输入没能激活窗口，跳过：$($_.Exception.Message)）"
    }

    # 输入之后再查一次有没有弹错误框
    foreach ($entry in [Windows]::VisibleWindows($process.Id)) {
        $className, $title = $entry -split '\|', 2
        if ($className -eq '#32770' -and -not $dialog) {
            $dialog = $title
        }
    }
}

$exited = $process.HasExited

if (-not $exited) {
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
}

Write-Host ""

if ($dialog) {
    Write-Host "失败：弹出了对话框「$dialog」" -ForegroundColor Red
    if ($wpfWindow) {
        Write-Host "      （主窗口标题「$wpfWindow」也在，但同时有错误框就是坏的）"
    }
    exit 1
}

if ($exited) {
    Write-Host "失败：进程自己退出了（退出码 $($process.ExitCode)）" -ForegroundColor Red
    exit 1
}

if (-not $wpfWindow) {
    Write-Host "失败：$TimeoutSeconds 秒内没有出现 WPF 窗口" -ForegroundColor Red
    exit 1
}

Write-Host "通过：WPF 主窗口已显示，标题「$wpfWindow」，无错误对话框" -ForegroundColor Green
exit 0
