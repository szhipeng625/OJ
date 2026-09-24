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

# ---------- 2. 暂存（client/ + server/ + shared/，公共依赖只保留一份） ----------
Write-Host '========== 暂存安装内容（去重） ==========' -ForegroundColor Cyan
$stage = Join-Path $env:TEMP 'oj_installer_stage'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
$stageClient = Join-Path $stage 'client'
$stageServer = Join-Path $stage 'server'
$stageSharedNative = Join-Path $stage 'shared\native'
$stageSharedUi = Join-Path $stage 'shared\ui'
New-Item -ItemType Directory -Force -Path $stageClient, $stageServer, $stageSharedNative, $stageSharedUi | Out-Null

# 客户端独有文件：dist 根目录下除 author/、native/、ui/ 外的全部内容
Get-ChildItem $DistClient -Force | Where-Object { $_.Name -notin @('author','native','ui') } | ForEach-Object {
    Copy-Item $_.FullName -Destination $stageClient -Recurse -Force
}
# 服务端独有文件：dist\author 根目录下除 native/、ui/ 外的全部内容
Get-ChildItem $DistAuthor -Force | Where-Object { $_.Name -notin @('native','ui') } | ForEach-Object {
    Copy-Item $_.FullName -Destination $stageServer -Recurse -Force
}

# 公共依赖：native/（ojcore + OpenSSL + authorcore）与 ui/（HandyControl 等）各只保留一份
$clientNative = Join-Path $DistClient 'native'
if (Test-Path $clientNative) {
    Get-ChildItem $clientNative -Force | Copy-Item -Destination $stageSharedNative -Recurse -Force
}
$authorcore = Join-Path $DistAuthor 'native\authorcore.dll'
if (Test-Path $authorcore) {
    Copy-Item $authorcore -Destination $stageSharedNative -Force
}
$clientUi = Join-Path $DistClient 'ui'
if (Test-Path $clientUi) {
    Get-ChildItem $clientUi -Force | Copy-Item -Destination $stageSharedUi -Recurse -Force
}

# 瘦身：去掉调试符号与运行日志
Get-ChildItem $stage -Recurse -Include *.pdb, *.log | Remove-Item -Force -ErrorAction SilentlyContinue

# ---------- 3. 压缩 ----------
Write-Host '========== 压缩安装包 ==========' -ForegroundColor Cyan
$zip = Join-Path $OutDir 'oj-package.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Remove-Item $stage -Recurse -Force

# 生成 SHA256 校验文件（与 zip 一并上传）
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$zip.sha256" -Value "$hash  oj-package.zip" -Encoding ascii
Write-Host "安装包 -> $zip" -ForegroundColor Green

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
Write-Host "  1. 将 oj-package.zip（及 .sha256）上传到 BaseUrl 指向的目录；"
Write-Host "  2. 把 OJ安装器.exe 发给用户即可一键安装/卸载。"
