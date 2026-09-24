<#
.SYNOPSIS
    将 oj-client.zip / oj-server.zip（及 .sha256）上传到服务器，供安装器下载。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\upload_package.ps1 -HostName 47.253.41.10 -UserName root -RemoteDir /var/www/html/oj/
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$HostName,
    [string]$UserName = 'root',
    [string]$RemoteDir = '/usr/share/nginx/html/oj/',
    [string]$OutDir = ''
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $RepoRoot 'installer_out' }

Write-Host "目标: ${UserName}@${HostName}:${RemoteDir}" -ForegroundColor Cyan

foreach ($pkg in @('oj-client.zip', 'oj-server.zip')) {
    $p = Join-Path $OutDir $pkg
    if (-not (Test-Path $p)) { throw "未找到安装包: $p（先运行 make_installer.ps1）" }

    Write-Host "上传: $pkg" -ForegroundColor Cyan
    scp.exe $p "${UserName}@${HostName}:${RemoteDir}"
    if ($LASTEXITCODE -ne 0) { throw "scp 上传失败: $pkg" }

    $sha = "$p.sha256"
    if (Test-Path $sha) {
        scp.exe $sha "${UserName}@${HostName}:${RemoteDir}"
        if ($LASTEXITCODE -ne 0) { Write-Warning "$pkg.sha256 上传失败（不影响安装）。" }
    }
}

# 静态页面 index.html（与安装包同目录，存在则一并上传）
$html = Join-Path $OutDir 'index.html'
if (Test-Path $html) {
    scp.exe $html "${UserName}@${HostName}:${RemoteDir}index.html"
    if ($LASTEXITCODE -ne 0) { Write-Warning 'index.html 上传失败。' }
}

# 安装器 oj.exe（与安装包同目录，存在则一并上传）
$exe = Join-Path $OutDir 'OJ安装器.exe'
if (Test-Path $exe) {
    scp.exe $exe "${UserName}@${HostName}:${RemoteDir}oj.exe"
    if ($LASTEXITCODE -ne 0) { Write-Warning 'oj.exe 上传失败。' }
}

Write-Host '上传完成 [完成]' -ForegroundColor Green
Write-Host "请确认浏览器可访问: http://$HostName/oj-client.zip 与 http://$HostName/oj-server.zip"
