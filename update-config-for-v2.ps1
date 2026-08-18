# V2.0.0 配置更新脚本
# 此脚本会自动更新 appsettings.json 以修复 NAT 穿透问题

param(
    [string]$ConfigPath = "src\NexusP2P.Signaling\appsettings.json",
    [string]$PublicOrigin = "",
    [switch]$DryRun
)

Write-Host "=== NexusP2P V2.0.0 配置更新工具 ===" -ForegroundColor Cyan
Write-Host ""

# 检查配置文件是否存在
if (-not (Test-Path $ConfigPath)) {
    Write-Host "错误: 找不到配置文件 $ConfigPath" -ForegroundColor Red
    exit 1
}

Write-Host "读取配置文件: $ConfigPath" -ForegroundColor Yellow

# 读取现有配置
$config = Get-Content $ConfigPath -Raw | ConvertFrom-Json

# 显示当前配置
Write-Host "`n当前配置:" -ForegroundColor Green
Write-Host "  PublicOrigin: $($config.Signaling.PublicOrigin)"
Write-Host "  EnableJoinRateLimit: $($config.Signaling.EnableJoinRateLimit)"
Write-Host "  JoinAttemptsPerMinute: $($config.Signaling.JoinAttemptsPerMinute)"
Write-Host "  STUN/TURN URLs: $($config.Signaling.Turn.Urls.Count) 个"

# 更新速率限制配置
if (-not $config.Signaling.PSObject.Properties.Name -contains "EnableJoinRateLimit") {
    Write-Host "`n添加速率限制开关（默认关闭）" -ForegroundColor Yellow
    $config.Signaling | Add-Member -MemberType NoteProperty -Name "EnableJoinRateLimit" -Value $false
    $config.Signaling | Add-Member -MemberType NoteProperty -Name "_EnableJoinRateLimit" -Value "是否启用入房速率限制。默认关闭。生产环境建议开启以防止暴力枚举文件码。"
}

# 添加 STUN 服务器（国内可访问）
if ($config.Signaling.Turn.Urls.Count -eq 0 -or 
    $config.Signaling.Turn.Urls -contains "stun:stun.l.google.com:19302") {
    Write-Host "`n更新为国内可访问的 STUN 服务器" -ForegroundColor Yellow
    $config.Signaling.Turn.Urls = @(
        "stun:stun.miwifi.com:3478",
        "stun:stun.chat.bilibili.com:3478",
        "stun:stun.hitv.com:3478",
        "stun:stun.voipgate.com:3478"
    )
}

# 添加 MaxReceiversPerRoom 配置（如果不存在）
if (-not $config.Signaling.PSObject.Properties.Name -contains "MaxReceiversPerRoom") {
    Write-Host "`n添加 MaxReceiversPerRoom 配置" -ForegroundColor Yellow
    $config.Signaling | Add-Member -MemberType NoteProperty -Name "MaxReceiversPerRoom" -Value ([int]::MaxValue)
    $config.Signaling | Add-Member -MemberType NoteProperty -Name "_MaxReceiversPerRoom" -Value "单个房间的接收方席位上限。默认不限制（int.MaxValue）。"
}

# 更新 PublicOrigin（如果提供）
if ($PublicOrigin -ne "") {
    Write-Host "`n设置 PublicOrigin: $PublicOrigin" -ForegroundColor Yellow
    $config.Signaling.PublicOrigin = $PublicOrigin
}

# 检查是否需要配置 PublicOrigin
if ($config.Signaling.PublicOrigin -eq "" -and $PublicOrigin -eq "") {
    Write-Host "`n警告: PublicOrigin 未配置！" -ForegroundColor Red
    Write-Host "请使用 -PublicOrigin 参数指定，例如:" -ForegroundColor Yellow
    Write-Host "  .\update-config-for-v2.ps1 -PublicOrigin 'http://your-server-ip:5000'" -ForegroundColor Cyan
}

# 显示新配置
Write-Host "`n新配置:" -ForegroundColor Green
Write-Host "  PublicOrigin: $($config.Signaling.PublicOrigin)"
Write-Host "  EnableJoinRateLimit: $($config.Signaling.EnableJoinRateLimit)"
Write-Host "  JoinAttemptsPerMinute: $($config.Signaling.JoinAttemptsPerMinute)"
Write-Host "  MaxReceiversPerRoom: $($config.Signaling.MaxReceiversPerRoom)"
Write-Host "  STUN/TURN URLs: $($config.Signaling.Turn.Urls.Count) 个"
foreach ($url in $config.Signaling.Turn.Urls) {
    Write-Host "    - $url" -ForegroundColor Gray
}

# 保存配置
if (-not $DryRun) {
    Write-Host "`n保存配置..." -ForegroundColor Yellow
    
    # 备份原配置
    $backupPath = "$ConfigPath.backup-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
    Copy-Item $ConfigPath $backupPath
    Write-Host "已备份原配置到: $backupPath" -ForegroundColor Gray
    
    # 保存新配置（保持格式）
    $config | ConvertTo-Json -Depth 10 | Set-Content $ConfigPath -Encoding UTF8
    
    Write-Host "配置已更新！" -ForegroundColor Green
    Write-Host ""
    Write-Host "下一步:" -ForegroundColor Cyan
    Write-Host "  1. 如果还没配置 PublicOrigin，请运行:" -ForegroundColor White
    Write-Host "     .\update-config-for-v2.ps1 -PublicOrigin 'http://your-server-ip:5000'" -ForegroundColor Gray
    Write-Host "  2. 重新编译并启动服务" -ForegroundColor White
    Write-Host "  3. 测试多接收方传输" -ForegroundColor White
} else {
    Write-Host "`n[DryRun 模式] 不会保存任何更改" -ForegroundColor Magenta
}

Write-Host ""
