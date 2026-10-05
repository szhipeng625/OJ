<#
.SYNOPSIS
    OJ 一键构建/打包脚本（Visual Studio 与 VS Code 共用）。

.DESCRIPTION
    构建顺序：
      1. C++ 判题核心：client\ojcore（ojcore.dll，x64）
      2. C++ 出题核心：author\authorcore（authorcore.dll，x64）
      3. 客户端 WPF：client（三层架构）
      4. 服务端 WPF：author\Author（三层架构）
    -Publish 时发布到 dist：
      dist\          客户端（client.exe）
      dist\author\   服务端（Author.exe）
    原生依赖（ojcore/authorcore/OpenSSL）、启动配置 config.xml 与模板代码 templates/ 由本脚本统一补齐。

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

    # 只构建 C++ 核心（ojcore / authorcore）
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
    [string]$MysqlDb = 'oj',
    # 中间层地址（生成 config.xml 时写入 middlewareUrl，客户端/服务端统一走中间层）
    [string]$MiddlewareUrl = 'https://47.253.41.10:8899'
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
$AuthorCoreVcx  = Join-Path $RepoRoot 'author\authorcore\authorcore.vcxproj'
$ClientCsproj   = Join-Path $RepoRoot 'client\client.csproj'
$AuthorCsproj   = Join-Path $RepoRoot 'author\Author\Author.csproj'
$OjcoreOut      = Join-Path $RepoRoot "client\ojcore\x64\$Configuration"
$AuthorCoreOut  = Join-Path $RepoRoot "author\authorcore\bin\$Configuration"
$DistClient     = Join-Path $RepoRoot 'dist'
$DistAuthor     = Join-Path $RepoRoot 'dist\author'
$OpenSslBin     = 'D:\OpenSSL-Win64\bin'

# ---------- Clean ----------
if ($Clean) {
    Write-Step '清理构建产物'
    $MSBuild = Find-MSBuild
    foreach ($proj in @($OjcoreVcx, $AuthorCoreVcx)) {
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
                      (Join-Path $RepoRoot 'author\authorcore\obj'))) {
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
    foreach ($dll in @('ojcore.dll')) {
        $src = Join-Path $OjcoreOut $dll
        if (Test-Path $src) {
            Copy-Item $src (Join-Path $targetDir $dll) -Force
        } else {
            Write-Host "  [警告] 缺少 $src，请先执行 -Core 构建。" -ForegroundColor Yellow
        }
    }
    # OpenSSL 运行库（ojcore 走 HTTPS 时依赖）
    foreach ($dll in @('libssl-3-x64.dll', 'libcrypto-3-x64.dll')) {
        $src = Join-Path $OpenSslBin $dll
        if (Test-Path $src) {
            Copy-Item $src (Join-Path $targetDir $dll) -Force
        } else {
            Write-Host "  [警告] 缺少 $src（HTTPS 将不可用）" -ForegroundColor Yellow
        }
    }
    # authorcore.dll（服务端目录才需要）
    $ac = Join-Path $AuthorCoreOut 'authorcore.dll'
    if ($IsAuthor -and (Test-Path $ac)) {
        Copy-Item $ac (Join-Path $targetDir 'authorcore.dll') -Force
    }
    # config.xml：启动配置（中间件地址等），缺失时由 config.example.xml 生成并注入 middlewareUrl；
    # 已存在则保留（可能被手工改过）。
    $cfgXml = Join-Path $targetDir 'config.xml'
    if (-not (Test-Path $cfgXml)) {
        $tpl = Get-Content (Join-Path $RepoRoot 'config.example.xml') -Raw -Encoding UTF8
        $tpl = $tpl -replace '<middlewareUrl>.*?</middlewareUrl>', "<middlewareUrl>$MiddlewareUrl</middlewareUrl>"
        [System.IO.File]::WriteAllText($cfgXml, $tpl, (New-Object System.Text.UTF8Encoding($false)))
        Write-Host "  已生成 config.xml（middlewareUrl=$MiddlewareUrl）"
    }
    # templates/：模板代码目录（gen/std/itf/entry.template.cpp），随包分发；缺失时各端回退内置模板
    $tplDir = Join-Path $RepoRoot 'templates'
    if (Test-Path $tplDir) {
        $dstTpl = Join-Path $targetDir 'templates'
        if (-not (Test-Path $dstTpl)) { New-Item -ItemType Directory -Force -Path $dstTpl | Out-Null }
        Copy-Item (Join-Path $tplDir '*.template.cpp') $dstTpl -Force
    }
}

# 将 UI 程序集与原生 DLL 分层到子目录（ui/、native/），应用入口与配置留在根目录
function Organize-LayeredOutput([string]$targetDir) {
    if (-not (Test-Path $targetDir)) { return }
    $uiDir     = Join-Path $targetDir 'ui'
    $codexDir  = Join-Path $targetDir 'codex'
    $nativeDir = Join-Path $targetDir 'native'
    # ui/ 与 native/ 两个应用都要，先建；codex/ 只有出题端用得上，
    # 按需创建 —— 否则客户端那边会留一个空目录，很难看。
    New-Item -ItemType Directory -Force -Path $uiDir, $nativeDir | Out-Null

    # 按模式列出「第三方 / 非入口」程序集，用模式而非逐个文件名 ——
    # 新增依赖包不用再来改这里。两类分开：
    #   ui/    —— 通用第三方 + 共享 UI 层
    #   codex/ —— ACMDOGCodex 专用（Claude SDK / MCP / Microsoft.Extensions 一族，
    #            数量四十来个，只此一处用，混在 ui/ 里会把目录撑得很难看）
    # 注意：System.Text.Json 等框架程序集故意留在根目录 —— 静态构造函数安装程序集解析器之前
    # 就可能被加载，挪走有解析不到的风险。
    $uiPatterns = @(
        'HandyControl.*',
        'ICSharpCode.AvalonEdit.*',
        'Markdig.dll',
        'OjEditorCore.*',
        'OjUiControls.*'
    )
    $codexPatterns = @(
        'OjCodex.*',                              # ACMDOGCodex 核心
        'Anthropic.dll',                          # 官方 Claude SDK
        'ModelContextProtocol*.dll',              # MCP 服务
        'Microsoft.Extensions.*.dll',             # MCP / Hosting 依赖族
        'System.Diagnostics.DiagnosticSource.dll',
        'System.IO.Pipelines.dll',
        'System.Net.ServerSentEvents.dll'
    )
    foreach ($pair in @(@($uiPatterns, $uiDir), @($codexPatterns, $codexDir))) {
        $destDir = $pair[1]
        foreach ($pat in $pair[0]) {
            $files = @(Get-ChildItem (Join-Path $targetDir $pat) -File -ErrorAction SilentlyContinue)
            if ($files.Count -eq 0) { continue }
            # 有东西要放才建目录（客户端不会产生任何 codex 程序集）
            if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Force -Path $destDir | Out-Null }
            foreach ($f in $files) { Move-Item $f.FullName (Join-Path $destDir $f.Name) -Force }
        }
    }
    # HandyControl 卫星资源（本地化，<culture>\HandyControl.resources.dll）随主程序集移入 ui/
    Get-ChildItem $targetDir -Directory | Where-Object {
        Test-Path (Join-Path $_.FullName 'HandyControl.resources.dll')
    } | ForEach-Object {
        $dest = Join-Path $uiDir $_.Name
        if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
        Move-Item $_.FullName $dest -Force
    }
    foreach ($f in @('ojcore.dll', 'authorcore.dll', 'libssl-3-x64.dll', 'libcrypto-3-x64.dll')) {
        $src = Join-Path $targetDir $f
        if (Test-Path $src) { Move-Item $src (Join-Path $nativeDir $f) -Force }
    }
}

# WPF 输出目录（bin 下调试运行也需要原生 DLL）
$ClientBin = Join-Path $RepoRoot "client\bin\$Configuration"
$AuthorBin = Join-Path $RepoRoot "author\Author\bin\$Configuration"

if ($Publish) {
    Write-Step '补齐运行时原生依赖'
    if ($WantClient) {
        Sync-NativeAssets $DistClient
        Organize-LayeredOutput $DistClient
        Write-Host "客户端 -> $DistClient"
    }
    if ($WantAuthor) {
        Sync-NativeAssets $DistAuthor -IsAuthor
        Organize-LayeredOutput $DistAuthor
        Write-Host "服务端 -> $DistAuthor"
    }
} else {
    Write-Step '补齐 bin 目录原生依赖'
    if ($WantClient -and (Test-Path $ClientBin)) { Sync-NativeAssets $ClientBin; Organize-LayeredOutput $ClientBin }
    if ($WantAuthor -and (Test-Path $AuthorBin)) { Sync-NativeAssets $AuthorBin -IsAuthor; Organize-LayeredOutput $AuthorBin }
}

Write-Host "`n全部完成 ✔（Configuration=$Configuration, Publish=$Publish）" -ForegroundColor Green
if ($Publish) {
    Write-Host "  客户端：$DistClient\client.exe"
    Write-Host "  服务端：$DistAuthor\Author.exe"
}
