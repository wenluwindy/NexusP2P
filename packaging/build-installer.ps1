param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root 'dist\nexusp2p-win-x64\NexusP2P-Desktop.exe'

if (-not (Test-Path -LiteralPath $source)) {
    throw "找不到 Windows 发布目录。请先运行 packaging/package.sh win。"
}

# Inno Setup 6.7 的安装器在没有管理员权限时默认装到用户目录，而不是
# Program Files —— winget 安装就是这种情况。只找 Program Files 会在开发机上
# 报「找不到 Inno Setup」，尽管它明明装好了。
$compiler = @(
    (Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1

if (-not $compiler) {
    throw '找不到 Inno Setup 6（ISCC.exe）。请先安装 Inno Setup。'
}

$previousVersion = $env:NEXUSP2P_VERSION
try {
    $env:NEXUSP2P_VERSION = $Version
    & $compiler (Join-Path $PSScriptRoot 'windows\NexusP2P.iss')
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup 编译失败，退出码：$LASTEXITCODE"
    }
}
finally {
    $env:NEXUSP2P_VERSION = $previousVersion
}

$output = Join-Path $root "dist\NexusP2P-Setup-$Version-win-x64.exe"
if (-not (Test-Path -LiteralPath $output)) {
    throw "安装器编译完成，但没有找到预期产物：$output"
}

Write-Host "安装器：$output"
