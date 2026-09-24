<#
.SYNOPSIS
    OJ 一键打包：生成安装包 oj-package.zip + 自包含单文件安装器 OJ安装器.exe。

.DESCRIPTION
    1. （可选）调用 build.ps1 -All -Publish 构建客户端与服务端到 dist。
    2. 把 dist（client）与 dist\author（server）分目录暂存并压缩为 oj-package.zip。
    3. 发布 installer\installer.csproj 为自包含单文件 OJInstaller.exe，
       并将 BaseUrl 烧录进程序集元数据，重命名为 OJ安装器.exe。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\make_installer.ps1 -BaseUrl "http://你的服务器/oj/"
    powershell -ExecutionPolicy Bypass -File scripts\make_installer.ps1 -NoBuild
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    # 安装包下载地址前缀（以 / 结尾），烧录进安装器；可被 installer_url.txt 或 /url= 覆盖
    [string]$BaseUrl = 'http://YOUR-SERVER/oj/',

    # 产物输出目录
    [string]$OutDir = '',

    # 跳过构建（dist 已是最新时）
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

$DistClient   = Join-Path $RepoRoot 'dist'
$DistAuthor   = Join-Path $RepoRoot 'dist\author'
$InstallerProj = Join-Path $RepoRoot 'installer\installer.csproj'

if (-not $OutDir) { $OutDir = Join-Path $RepoRoot 'installer_out' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# ---------- 1. 构建全部 ----------
if (-not $NoBuild) {
    Write-Host '========== 构建客户端 + 服务端 ==========' -ForegroundColor Cyan
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $RepoRoot 'scripts\build.ps1') `
        -All -Publish -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw '构建失败，终止打包。' }
}

if (-not (Test-Path $DistClient)) { throw "缺少客户端目录: $DistClient" }
if (-not (Test-Path $DistAuthor)) { throw "缺少服务端目录: $DistAuthor" }

# ---------- 2. 打包：oj-client.zip（仅客户端）+ oj-server.zip（出题端，含共享依赖） ----------
Write-Host '========== 打包安装内容 ==========' -ForegroundColor Cyan
Add-Type -AssemblyName System.IO.Compression.FileSystem

function New-Package([string]$innerName, [string]$sourceDir, [string]$zipPath, [switch]$ExcludeAuthor) {
    $stage = Join-Path $env:TEMP ("oj_pkg_" + $innerName)
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    $inner = Join-Path $stage $innerName
    New-Item -ItemType Directory -Force -Path $inner | Out-Null
    Get-ChildItem $sourceDir -Force | Where-Object { -not ($ExcludeAuthor -and $_.Name -eq 'author') } | ForEach-Object {
        Copy-Item $_.FullName -Destination $inner -Recurse -Force
    }
    # 瘦身：去掉调试符号与运行日志
    Get-ChildItem $stage -Recurse -Include *.pdb, *.log | Remove-Item -Force -ErrorAction SilentlyContinue

    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    Remove-Item $stage -Recurse -Force

    $hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -Path "$zipPath.sha256" -Value "$hash  $(Split-Path $zipPath -Leaf)" -Encoding ascii
    Write-Host "  安装包 -> $zipPath" -ForegroundColor Green
}

# 客户端包：dist 下除 author 之外的全部内容（不含出题端 exe/dll 与死依赖）
New-Package 'client' $DistClient (Join-Path $OutDir 'oj-client.zip') -ExcludeAuthor
# 出题端包：dist\author 全部内容（自包含：含 ojcore/OpenSSL 等共享依赖）
New-Package 'server' $DistAuthor (Join-Path $OutDir 'oj-server.zip')

# ---------- 4. 发布安装器 ----------
Write-Host '========== 发布安装器（自包含单文件） ==========' -ForegroundColor Cyan
$instOut = Join-Path $env:TEMP 'oj_installer_pub'
if (Test-Path $instOut) { Remove-Item $instOut -Recurse -Force }

dotnet publish $InstallerProj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:InstallerBaseUrl="$BaseUrl" `
    -o $instOut --nologo
if ($LASTEXITCODE -ne 0) { throw '安装器发布失败。' }

Copy-Item (Join-Path $instOut 'OJInstaller.exe') (Join-Path $OutDir 'OJ安装器.exe') -Force
Remove-Item $instOut -Recurse -Force
Write-Host "安装器 -> $(Join-Path $OutDir 'OJ安装器.exe')" -ForegroundColor Green

# 静态页面 index.html（源文件在 installer\www\）
$wwwIndex = Join-Path $RepoRoot 'installer\www\index.html'
if (Test-Path $wwwIndex) {
    Copy-Item $wwwIndex (Join-Path $OutDir 'index.html') -Force
    Write-Host "页面 -> $(Join-Path $OutDir 'index.html')" -ForegroundColor Green
}

Write-Host ''
Write-Host '全部完成 [完成]' -ForegroundColor Green
Write-Host "  1. 将 oj-client.zip / oj-server.zip（及 .sha256）上传到 BaseUrl 指向的目录；"
Write-Host "  2. 把 OJ安装器.exe 发给用户：/client 装客户端，/server 装出题端，默认两者都装。"
