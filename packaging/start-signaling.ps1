# 在局域网里起一个信令服务器，供两台电脑对测。
#
# 单机自测不需要它这么麻烦，直接：
#   $env:Signaling__PublicOrigin="http://127.0.0.1:5000"
#   .\nexusp2p-signaling.exe --urls http://127.0.0.1:5000
#
# 这个脚本存在的唯一理由是：PublicOrigin 必须填对方能访问到的地址。
# 填错的话分享链接对方打不开，而这种错极难从现象倒推 —— 所以自动探测一下。

param(
    [int]$Port = 5000,

    # 自动探测选错网卡时（比如挑了虚拟网卡）用这个手动指定
    [string]$Ip
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $here 'nexusp2p-signaling.exe'

if (-not (Test-Path $exe)) {
    Write-Host "找不到 $exe" -ForegroundColor Red
    exit 1
}

function Get-OutboundIPv4 {
    # 用一个 UDP socket「连」到外网地址来问操作系统「出去的话走哪张网卡」。
    # UDP connect 不会真的发包，但会把路由定下来，于是 LocalEndPoint 就是答案。
    # 比枚举网卡可靠：枚举得自己判断哪张是虚拟网卡、哪张没插线。
    $socket = New-Object System.Net.Sockets.Socket(
        [System.Net.Sockets.AddressFamily]::InterNetwork,
        [System.Net.Sockets.SocketType]::Dgram,
        [System.Net.Sockets.ProtocolType]::Udp)
    try {
        $socket.Connect('8.8.8.8', 65530)
        return $socket.LocalEndPoint.Address.ToString()
    } finally {
        $socket.Dispose()
    }
}

if (-not $Ip) {
    try {
        $Ip = Get-OutboundIPv4
    } catch {
        Write-Host "自动探测本机 IP 失败：$($_.Exception.Message)" -ForegroundColor Red
        Write-Host "用 -Ip 手动指定，比如： .\start-signaling.ps1 -Ip 192.168.1.10"
        exit 1
    }
}

$origin = "http://${Ip}:${Port}"

Write-Host ""
Write-Host "信令服务器：$origin" -ForegroundColor Green
Write-Host ""
Write-Host "两台电脑都用这个地址（含本机）："
Write-Host "  nexusp2p.exe send  <文件或文件夹> --signaling $origin" -ForegroundColor Cyan
Write-Host "  nexusp2p.exe receive <分享链接>   --dest D:\收到 --signaling $origin" -ForegroundColor Cyan
Write-Host ""

# 本机所有 IPv4 都列出来：探测选错网卡时，正确答案通常就在这里面
try {
    $all = [System.Net.Dns]::GetHostAddresses([System.Net.Dns]::GetHostName()) |
        Where-Object { $_.AddressFamily -eq 'InterNetwork' } |
        ForEach-Object { $_.IPAddressToString }

    if ($all.Count -gt 1) {
        Write-Host "本机其他 IPv4：$($all -join '、')" -ForegroundColor DarkGray
        Write-Host "对方连不上就换一个： .\start-signaling.ps1 -Ip <另一个地址>" -ForegroundColor DarkGray
        Write-Host ""
    }
} catch {
    # 列不出来不影响主流程
}

Write-Host "首次运行 Windows 防火墙会弹窗，必须点「允许」——" -ForegroundColor Yellow
Write-Host "不允许的话对方连不上，而且不会有任何提示，只表现为「一直连不上」。" -ForegroundColor Yellow
Write-Host ""
Write-Host "Ctrl+C 停止。"
Write-Host ""

$env:Signaling__PublicOrigin = $origin

# 绑 0.0.0.0 而不是具体 IP：换网络（插网线、切 Wi-Fi）不用重启服务
& $exe --urls "http://0.0.0.0:$Port"
