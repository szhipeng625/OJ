<#
.SYNOPSIS
    打便携包：客户端 + 出题端 + .NET 8 桌面运行时，解压即用。

.DESCRIPTION
    目标机不需要预装 .NET、不需要管理员权限。运行时解包放在 runtime\，
    启动脚本设 DOTNET_ROOT 指向它，两个应用共用这一份。

    目录布局（与 OJ安装器.exe 期望的一致）：
      client\     客户端（client.exe）
      server\     出题端（Author.exe）
      runtime\    .NET 8 桌面运行时（解包）
      启动客户端.bat / 启动出题端.bat

.EXAMPLE
    # 先发布两个应用（含 C++ 核心）
    powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -All -Publish
    # 再打便携包
    powershell -ExecutionPolicy Bypass -File scripts\make_portable.ps1
#>
[CmdletBinding()]
param(
    # 已发布的两个应用所在目录（build.ps1 -Publish 的产物）
    [string]$DistDir = '',

    # .NET 8 桌面运行时的可重定位目录（含 dotnet.exe / host\fxr / shared）
    [string]$RuntimeDir = '',

    # 输出目录（便携包根 + zip 都落这里）
    [string]$OutDir = '',

    # .NET 版本（仅用于提示信息与自动下载）
    [string]$DotNetVersion = '8.0.31',

    # 运行时目录不存在时自动下载安装
    [switch]$DownloadRuntime,

    # 打包完成后压缩为 zip
    [switch]$Zip = $true
)

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not $DistDir)    { $DistDir    = Join-Path $RepoRoot 'dist' }
if (-not $RuntimeDir) { $RuntimeDir = Join-Path $RepoRoot 'dist_pack\runtime' }
if (-not $OutDir)     { $OutDir     = Join-Path $RepoRoot 'dist_pack\OJ' }

function Write-Step([string]$t) { Write-Host "`n========== $t ==========" -ForegroundColor Cyan }

# ---------- 0. 前置检查 ----------
Write-Step '检查输入'

$clientSrc = Join-Path $DistDir 'client.exe'
$serverSrc = Join-Path $DistDir 'author\Author.exe'
if (-not (Test-Path $clientSrc)) { throw "未找到客户端：$clientSrc（先跑 build.ps1 -All -Publish）" }
if (-not (Test-Path $serverSrc)) { throw "未找到出题端：$serverSrc（先跑 build.ps1 -All -Publish）" }

# 运行时目录必须齐三样，缺一不可：宿主、fxr、桌面框架
$needHost = Join-Path $RuntimeDir 'dotnet.exe'
$needFxr  = Join-Path $RuntimeDir "host\fxr\$DotNetVersion\hostfxr.dll"
$needDesk = Join-Path $RuntimeDir "shared\Microsoft.WindowsDesktop.App\$DotNetVersion\PresentationFramework.dll"
if ((Test-Path $needHost) -and (Test-Path $needFxr) -and (Test-Path $needDesk)) {
    Write-Host "运行时就绪：$RuntimeDir"
} else {
    if (-not $DownloadRuntime) {
        throw @"
运行时目录不完整：$RuntimeDir
需要 dotnet.exe + host\fxr\$DotNetVersion + shared\Microsoft.WindowsDesktop.App\$DotNetVersion。
加 -DownloadRuntime 自动下载，或手工执行：
  curl -L -o dotnet-install.ps1 https://dot.net/v1/dotnet-install.ps1
  powershell -File dotnet-install.ps1 -Runtime dotnet        -Version $DotNetVersion -InstallDir "<dir>"
  powershell -File dotnet-install.ps1 -Runtime windowsdesktop -Version $DotNetVersion -InstallDir "<dir>"
（注意必须装两次：基础运行时带 dotnet.exe 与 host\fxr，windowsdesktop 只补桌面框架）
"@
    }

    Write-Step "下载 .NET $DotNetVersion 桌面运行时"
    $installer = Join-Path $env:TEMP 'dotnet-install.ps1'
    Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer -UseBasicParsing
    New-Item -ItemType Directory -Force -Path $RuntimeDir | Out-Null
    foreach ($rt in @('dotnet', 'windowsdesktop')) {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $installer `
            -Runtime $rt -Version $DotNetVersion -InstallDir $RuntimeDir -NoPath
        if ($LASTEXITCODE -ne 0) { throw "运行时安装失败：$rt" }
    }
}

# ---------- 1. 暂存 ----------
Write-Step '暂存文件'

if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
$stageClient = Join-Path $OutDir 'client'
$stageServer = Join-Path $OutDir 'server'
New-Item -ItemType Directory -Force -Path $stageClient, $stageServer | Out-Null

# 必须剔除的「运行时状态」——它们是本机跑出来的，不是程序的一部分。
# 其中 auth\session.txt 是登录会话令牌，打包出去等于把登录态一起发出去。
# 注意：不要连 runtimes\ 一起排掉 —— 那是部分 NuGet 包的聚合程序集资源目录，程序运行需要它。
$excludeDirs  = @('auth', 'ojdata', 'logs', 'temp', 'testdata', 'contests', 'problems', 'server')
$excludeFiles = @('*.log')

function Test-Excluded($item) {
    if ($excludeDirs -contains $item.Name) { return $true }
    foreach ($p in $excludeFiles) { if ($item.Name -like $p) { return $true } }
    return $false
}

# 客户端：dist 根下的内容，排除 author 子目录（那是出题端）
Get-ChildItem $DistDir -Force | Where-Object { $_.Name -ne 'author' -and -not (Test-Excluded $_) } | ForEach-Object {
    Move-Item $_.FullName (Join-Path $stageClient $_.Name) -Force
}
# 出题端
Get-ChildItem (Join-Path $DistDir 'author') -Force | Where-Object { -not (Test-Excluded $_) } | ForEach-Object {
    Move-Item $_.FullName (Join-Path $stageServer $_.Name) -Force
}

# 被排掉的状态目录在源码树里也清一下，免得下次发布又被 dotnet publish 的“保留文件”逻辑带回来
foreach ($base in @($stageClient, $stageServer)) {
    foreach ($d in $excludeDirs) {
        $p = Join-Path $base $d
        if (Test-Path $p) { Remove-Item $p -Recurse -Force -ErrorAction SilentlyContinue }
    }
    Get-ChildItem (Join-Path $base '*.log') -ErrorAction SilentlyContinue | Remove-Item -Force
}
Write-Host "client\ + server\ 已就位（已排除运行时状态）"

Write-Step "拷贝运行时（$DotNetVersion）"
$stageRuntime = Join-Path $OutDir 'runtime'
Copy-Item $RuntimeDir $stageRuntime -Recurse -Force
Write-Host "runtime\ 已就位"

# ---------- 2. 启动脚本 ----------
Write-Step '生成启动脚本'

# DOTNET_ROOT 指向随包运行时；MULTILEVEL_LOOKUP=0 强制只用这一份，
# 避免目标机装了别的 .NET 版本时被「就近取用」而行为不一致。
$launcherTpl = @'
@echo off
chcp 65001 >nul
set "DOTNET_ROOT=%~dp0runtime"
set "DOTNET_MULTILEVEL_LOOKUP=0"
start "" "%~dp0{0}\{1}"
'@

Set-Content -Path (Join-Path $OutDir '启动客户端.bat') -Value ($launcherTpl -f 'client', 'client.exe') -Encoding UTF8
Set-Content -Path (Join-Path $OutDir '启动出题端.bat') -Value ($launcherTpl -f 'server', 'Author.exe') -Encoding UTF8

$readme = @"
ACMDOG 便携包
=============

启动方式
--------
  双击「启动客户端.bat」   —— 做题端
  双击「启动出题端.bat」   —— 出题工作台（含 ACMDOGCodex）

不要直接双击 client.exe / server\Author.exe —— 那样不会设置 DOTNET_ROOT，
目标机没装 .NET 时会提示缺少运行时。用启动脚本即可。

依赖
----
本包自带 .NET $DotNetVersion 桌面运行时（runtime\ 目录，约 160 MB）。
目标机不需要预装 .NET，也不需要管理员权限。

目录
----
  client\    客户端
  server\    出题端
  runtime\   .NET 运行时（两个应用共用）

配置
----
两个目录下各有一份 config.xml，里面是中间件地址与各类路径。
换服务器时改 middlewareUrl 即可。

注意
----
.NET $DotNetVersion 的生命周期到 2026-11-10。之后如需长期使用，
建议升级到受支持的 .NET 版本（需同步调整项目的 TargetFramework）。
"@
Set-Content -Path (Join-Path $OutDir '使用说明.txt') -Value $readme -Encoding UTF8

# ---------- 3. 压缩 ----------
if ($Zip) {
    Write-Step '压缩'
    $zipPath = Join-Path (Split-Path -Parent $OutDir) ((Split-Path -Leaf $OutDir) + '.zip')
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $OutDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
    $hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLower()
    Set-Content -Path "$zipPath.sha256" -Value "$hash  $(Split-Path -Leaf $zipPath)" -Encoding ascii
    Write-Host "已生成：$zipPath"
    Write-Host ("大小：{0:N1} MB，sha256：{1}" -f ((Get-Item $zipPath).Length / 1MB), $hash)
}

Write-Host "`n完成。便携包根目录：$OutDir" -ForegroundColor Green
