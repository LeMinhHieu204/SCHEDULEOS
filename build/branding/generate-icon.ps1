# Generates the ScheduleOS application icon (multi-resolution .ico) and logo PNG.
# Usage (Windows PowerShell 5.1): powershell -ExecutionPolicy Bypass -File build\branding\generate-icon.ps1
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$outDir = Join-Path $root 'assets\branding'
New-Item -ItemType Directory -Force $outDir | Out-Null

function New-LogoBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [single]$size
    $r = $s * 0.22
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $r * 2, $r * 2, 180, 90)
    $path.AddArc($s - $r * 2 - 1, 0, $r * 2, $r * 2, 270, 90)
    $path.AddArc($s - $r * 2 - 1, $s - $r * 2 - 1, $r * 2, $r * 2, 0, 90)
    $path.AddArc(0, $s - $r * 2 - 1, $r * 2, $r * 2, 90, 90)
    $path.CloseFigure()
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $s, $s), ([System.Drawing.Color]::FromArgb(255, 16, 37, 50)), ([System.Drawing.Color]::FromArgb(255, 12, 32, 44))
    $g.FillPath($bg, $path)

    # Leaf / flame mark: two mirrored bezier petals in teal.
    $leaf = New-Object System.Drawing.Drawing2D.GraphicsPath
    $leaf.AddBezier($s * 0.50, $s * 0.14, $s * 0.86, $s * 0.36, $s * 0.74, $s * 0.70, $s * 0.42, $s * 0.86)
    $leaf.AddBezier($s * 0.42, $s * 0.86, $s * 0.54, $s * 0.66, $s * 0.40, $s * 0.48, $s * 0.50, $s * 0.14)
    $leaf.CloseFigure()
    $teal = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF ($s * 0.4), ($s * 0.1)), (New-Object System.Drawing.PointF ($s * 0.6), ($s * 0.9)), ([System.Drawing.Color]::FromArgb(255, 0, 184, 189)), ([System.Drawing.Color]::FromArgb(255, 0, 140, 149))
    $g.FillPath($teal, $leaf)

    $leaf2 = New-Object System.Drawing.Drawing2D.GraphicsPath
    $leaf2.AddBezier($s * 0.40, $s * 0.30, $s * 0.16, $s * 0.48, $s * 0.22, $s * 0.74, $s * 0.42, $s * 0.86)
    $leaf2.AddBezier($s * 0.42, $s * 0.86, $s * 0.36, $s * 0.66, $s * 0.32, $s * 0.48, $s * 0.40, $s * 0.30)
    $leaf2.CloseFigure()
    $light = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(220, 221, 245, 242))
    $g.FillPath($light, $leaf2)

    $g.Dispose()
    return $bmp
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @()
foreach ($size in $sizes) {
    $bmp = New-LogoBitmap $size
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , @($size, $ms.ToArray())
    if ($size -eq 256) { $bmp.Save((Join-Path $outDir 'logo-256.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
}

# ICO container with PNG-compressed images.
$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ico
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $dim = if ($p[0] -ge 256) { 0 } else { $p[0] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$p[1].Length); $w.Write([uint32]$offset)
    $offset += $p[1].Length
}
foreach ($p in $pngs) { $w.Write($p[1]) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $outDir 'scheduleos.ico'), $ico.ToArray())
Write-Host "Icon written to $outDir"
