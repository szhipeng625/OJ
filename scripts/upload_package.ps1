<#
.SYNOPSIS
    将 oj-package.zip（及 .sha256）上传到服务器，供安装器下载。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\upload_package.ps1 -HostName 47.253.41.10 -UserName root -RemoteDir /var/www/html/oj/
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$HostName,
    [string]$UserName = 'root',
    [string]$RemoteDir = '/var/www/html/oj/',
    [string]$PackagePath = ''
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not $PackagePath) { $PackagePath = Join-Path $RepoRoot 'installer_out\oj-package.zip' }
if (-not (Test-Path $PackagePath)) { throw "未找到安装包: $PackagePath（先运行 make_installer.ps1）" }

Write-Host "上传: $PackagePath" -ForegroundColor Cyan
Write-Host "目标: ${UserName}@${HostName}:${RemoteDir}" -ForegroundColor Cyan

scp.exe $PackagePath "${UserName}@${HostName}:${RemoteDir}"
if ($LASTEXITCODE -ne 0) { throw 'scp 上传失败。' }

$sha = "$PackagePath.sha256"
if (Test-Path $sha) {
    scp.exe $sha "${UserName}@${HostName}:${RemoteDir}"
    if ($LASTEXITCODE -ne 0) { Write-Warning 'sha256 文件上传失败（不影响安装）。' }
}

# 静态页面 index.html（与安装包同目录，存在则一并上传）
$html = Join-Path (Split-Path $PackagePath -Parent) 'index.html'
if (Test-Path $html) {
    scp.exe $html "${UserName}@${HostName}:${RemoteDir}index.html"
    if ($LASTEXITCODE -ne 0) { Write-Warning 'index.html 上传失败。' }
}

# 安装器 oj.exe（与安装包同目录，存在则一并上传）
$exe = Join-Path (Split-Path $PackagePath -Parent) 'OJ安装器.exe'
if (Test-Path $exe) {
    scp.exe $exe "${UserName}@${HostName}:${RemoteDir}oj.exe"
    if ($LASTEXITCODE -ne 0) { Write-Warning 'oj.exe 上传失败。' }
}

Write-Host '上传完成 [完成]' -ForegroundColor Green
Write-Host "请确认浏览器可访问: http://$HostName/$(Split-Path $PackagePath -Leaf)"
