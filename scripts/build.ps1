<#
.SYNOPSIS
    OJ 一键构建/打包脚本（Visual Studio 与 VS Code 共用）。

.DESCRIPTION
    构建顺序：
      1. C++ 判题核心：client\ojcore（ojcore.dll + lsm_shared.dll，x64）
      2. C++ 出题核心：author\authorcore（authorcore.dll，x64）
      3. 客户端 WPF：client（三层架构）
      4. 服务端 WPF：author\Author（三层架构）
    -Publish 时发布到 dist：
      dist\          客户端（client.exe）
      dist\author\   服务端（Author.exe）
    原生依赖（ojcore/lsm_shared/libmysql/mysql_config）由本脚本统一补齐。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -All -Publish
    powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Core
    powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Client
    powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Author
    powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Clean
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    # 只构建 C++ 核心（ojcore / lsm_shared / authorcore）
    [switch]$Core,
    # 只构建客户端 WPF
    [switch]$Client,
    # 只构建服务端 WPF（自动带上 authorcore）
    [switch]$Author,
    # 全部构建
    [switch]$All,
    # 发布到 dist（否则只做构建，产物在各工程 bin 目录，便于调试）
    [switch]$Publish,
    # 清理
    [switch]$Clean,
    # 生成 mysql_config.json 时使用的数据库连接参数（该文件不入库；仅在缺失时生成）
    [string]$MysqlHost = '127.0.0.1',
    [int]$MysqlPort = 3306,
    [string]$MysqlUser = 'root',
    [string]$MysqlPass = '12345678',
    [string]$MysqlDb = 'oj'
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

function Write-Step([string]$msg) { Write-Host "`n========== $msg ==========" -ForegroundColor Cyan }
function Die([string]$msg) { Write-Host "`n[错误] $msg" -ForegroundColor Red; exit 1 }

# ---------- 定位 MSBuild（vswhere，失败则回退常见安装路径） ----------
function Find-MSBuild {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $found = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
        if ($found -and (Test-Path $found)) { return $found }
    }
    $fallback = 'D:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe'
    if (Test-Path $fallback) { return $fallback }
    Die '未找到 MSBuild，请安装 Visual Studio 2022（含 C++ 桌面开发工作负载），或修改脚本中的回退路径。'
}

# ---------- 路径约定 ----------
$OjcoreSln      = Join-Path $RepoRoot 'client\ojcore\ojcore.sln'
$OjcoreVcx      = Join-Path $RepoRoot 'client\ojcore\ojcore.vcxproj'
$LsmVcx         = Join-Path $RepoRoot 'client\ojcore\lsm\lsm_shared.vcxproj'
$AuthorCoreVcx  = Join-Path $RepoRoot 'author\authorcore\authorcore.vcxproj'
$ClientCsproj   = Join-Path $RepoRoot 'client\client.csproj'
$AuthorCsproj   = Join-Path $RepoRoot 'author\Author\Author.csproj'
$OjcoreOut      = Join-Path $RepoRoot "client\ojcore\x64\$Configuration"
$AuthorCoreOut  = Join-Path $RepoRoot "author\authorcore\bin\$Configuration"
$DistClient     = Join-Path $RepoRoot 'dist'
$DistAuthor     = Join-Path $RepoRoot 'dist\author'

# ---------- Clean ----------
if ($Clean) {
    Write-Step '清理构建产物'
    $MSBuild = Find-MSBuild
    foreach ($proj in @($OjcoreVcx, $LsmVcx, $AuthorCoreVcx)) {
        if (Test-Path $proj) {
            & $MSBuild $proj /t:Clean /p:Configuration=$Configuration /p:Platform=x64 /v:minimal /nologo
        }
    }
    foreach ($csproj in @($ClientCsproj, $AuthorCsproj)) {
        dotnet clean $csproj -c $Configuration --nologo -v minimal
    }
    foreach ($d in @((Join-Path $RepoRoot 'dist'),
                      (Join-Path $RepoRoot 'client\ojcore\x64'),
                      (Join-Path $RepoRoot 'author\authorcore\bin'),
                      (Join-Path $RepoRoot 'author\authorcore\obj'),
                      (Join-Path $RepoRoot 'client\ojcore\lsm\x64'))) {
        if (Test-Path $d) { Remove-Item $d -Recurse -Force; Write-Host "删除 $d" }
    }
    Write-Host '清理完成。' -ForegroundColor Green
    exit 0
}

# 未指定任何组件时，默认 -All
if (-not ($Core -or $Client -or $Author -or $All)) { $All = $true }
# WPF 工程运行时依赖 C++ 核心，构建任一前端都先确保核心是最新的
$WantCore   = $Core -or $All -or $Author -or $Client
$WantClient = $Client -or $All
$WantAuthor = $Author -or $All

# ---------- 1. C++ 核心（必须显式 x64） ----------
if ($WantCore) {
    $MSBuild = Find-MSBuild
    Write-Step "构建 C++ 核心（$Configuration|x64）"
    & $MSBuild $OjcoreVcx /p:Configuration=$Configuration /p:Platform=x64 /m /v:minimal /nologo
    if ($LASTEXITCODE -ne 0) { Die 'ojcore 构建失败。' }
    & $MSBuild $LsmVcx /p:Configuration=$Configuration /p:Platform=x64 /m /v:minimal /nologo
    if ($LASTEXITCODE -ne 0) { Die 'lsm_shared 构建失败。' }
    & $MSBuild $AuthorCoreVcx /p:Configuration=$Configuration /p:Platform=x64 /m /v:minimal /nologo
    if ($LASTEXITCODE -ne 0) { Die 'authorcore 构建失败。' }
}

# ---------- 2. 客户端 WPF ----------
if ($WantClient) {
    Write-Step "构建客户端 client（$Configuration）"
    if ($Publish) {
        if (Test-Path $DistClient) {
            # 只清客户端文件，保留 dist\author
            Get-ChildItem $DistClient -Force | Where-Object { $_.Name -ne 'author' } | Remove-Item -Recurse -Force
        }
        dotnet publish $ClientCsproj -c $Configuration -o $DistClient --nologo -v minimal
        if ($LASTEXITCODE -ne 0) { Die '客户端发布失败。' }
    } else {
        dotnet build $ClientCsproj -c $Configuration --nologo -v minimal
        if ($LASTEXITCODE -ne 0) { Die '客户端构建失败。' }
    }
}

# ---------- 3. 服务端 WPF ----------
if ($WantAuthor) {
    Write-Step "构建服务端 author（$Configuration）"
    if ($Publish) {
        if (Test-Path $DistAuthor) { Remove-Item $DistAuthor -Recurse -Force }
        dotnet publish $AuthorCsproj -c $Configuration -o $DistAuthor --nologo -v minimal
        if ($LASTEXITCODE -ne 0) { Die '服务端发布失败。' }
    } else {
        dotnet build $AuthorCsproj -c $Configuration --nologo -v minimal
        if ($LASTEXITCODE -ne 0) { Die '服务端构建失败。' }
    }
}

# ---------- 4. 补齐运行时原生依赖 ----------
function Sync-NativeAssets([string]$targetDir, [switch]$IsAuthor) {
    if (-not (Test-Path $targetDir)) { Die "目标目录不存在：$targetDir" }
    foreach ($dll in @('ojcore.dll', 'lsm_shared.dll', 'libmysql.dll')) {
        $src = Join-Path $OjcoreOut $dll
        if (Test-Path $src) {
            Copy-Item $src (Join-Path $targetDir $dll) -Force
        } else {
            Write-Host "  [警告] 缺少 $src，请先执行 -Core 构建。" -ForegroundColor Yellow
        }
    }
    # authorcore.dll（服务端目录才需要）
    $ac = Join-Path $AuthorCoreOut 'authorcore.dll'
    if ($IsAuthor -and (Test-Path $ac)) {
        Copy-Item $ac (Join-Path $targetDir 'authorcore.dll') -Force
    }
    # mysql_config.json：已存在则保留（可能被手工改过）；
    # 缺失时生成本地开发配置（不入库；模板见 docs\mysql_config.example.json）
    $cfg = Join-Path $targetDir 'mysql_config.json'
    if (-not (Test-Path $cfg)) {
        $cfgObj = [ordered]@{
            host = $MysqlHost
            port = $MysqlPort
            user = $MysqlUser
            pass = $MysqlPass
            db   = $MysqlDb
        }
        ($cfgObj | ConvertTo-Json) | Set-Content -Path $cfg -Encoding UTF8
        Write-Host "  已生成本地 mysql_config.json（账号 $MysqlUser，库 $MysqlDb）"
    }
}

# WPF 输出目录（bin 下调试运行也需要原生 DLL）
$ClientBin = Join-Path $RepoRoot "client\bin\$Configuration\net8.0-windows"
$AuthorBin = Join-Path $RepoRoot "author\Author\bin\$Configuration\net8.0-windows"

if ($Publish) {
    Write-Step '补齐运行时原生依赖'
    if ($WantClient) {
        Sync-NativeAssets $DistClient
        Write-Host "客户端 -> $DistClient"
    }
    if ($WantAuthor) {
        Sync-NativeAssets $DistAuthor -IsAuthor
        Write-Host "服务端 -> $DistAuthor"
    }
} else {
    Write-Step '补齐 bin 目录原生依赖'
    if ($WantClient -and (Test-Path $ClientBin)) { Sync-NativeAssets $ClientBin }
    if ($WantAuthor -and (Test-Path $AuthorBin)) { Sync-NativeAssets $AuthorBin -IsAuthor }
}

Write-Host "`n全部完成 ✔（Configuration=$Configuration, Publish=$Publish）" -ForegroundColor Green
if ($Publish) {
    Write-Host "  客户端：$DistClient\client.exe"
    Write-Host "  服务端：$DistAuthor\Author.exe"
}
