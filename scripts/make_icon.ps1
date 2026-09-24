# Convert a PNG into a multi-size (16..256) PNG-compressed .ico, for use as exe/window icon.
# Usage: powershell -ExecutionPolicy Bypass -File scripts\make_icon.ps1 -Source <png> -Dest <ico>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Dest
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$src = [System.Drawing.Image]::FromFile($Source)
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngs = New-Object System.Collections.Generic.List[byte[]]

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.DrawImage($src, 0, 0, $s, $s)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs.Add($ms.ToArray())
    $bmp.Dispose()
    $ms.Dispose()
}
$src.Dispose()

$count = $sizes.Count
$headerSize = 6
$dirSize = 16 * $count
$offset = $headerSize + $dirSize

$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([UInt16]0)  # reserved
$bw.Write([UInt16]1)  # type = icon
$bw.Write([UInt16]$count)
for ($i = 0; $i -lt $count; $i++) {
    $s = $sizes[$i]
    $b = if ($s -ge 256) { 0 } else { $s }   # 0 means 256
    $bw.Write([byte]$b)   # width
    $bw.Write([byte]$b)   # height
    $bw.Write([byte]0)    # palette
    $bw.Write([byte]0)    # reserved
    $bw.Write([UInt16]1)  # planes
    $bw.Write([UInt16]32) # bpp
    $bw.Write([UInt32]$pngs[$i].Length)
    $bw.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Flush()

$dir = Split-Path -Parent $Dest
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
[System.IO.File]::WriteAllBytes($Dest, $ms.ToArray())
$bw.Dispose()
$ms.Dispose()
Write-Host "已生成图标: $Dest"
