# Draws the installer wizard images (glass look) into installer/assets/. No input needed.
# Run: powershell -NoProfile -ExecutionPolicy Bypass -File src/make-installer-images.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'installer/assets'
New-Item -ItemType Directory -Force $out | Out-Null
$icoPath = Join-Path $root 'src/assets/RegionControl.ico'
# Palette from src/RegionControl/Theme.cs (dark mode defaults): accent (76,194,255), base (0,120,212).
$accent = [Drawing.Color]::FromArgb(76, 194, 255); $accentBase = [Drawing.Color]::FromArgb(0, 120, 212)
$bg1 = [Drawing.Color]::FromArgb(14, 20, 34); $bg2 = [Drawing.Color]::FromArgb(24, 40, 70)

function New-Canvas($w, $h) {
    $bmp = New-Object Drawing.Bitmap $w, $h, ([Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $bgBrush = New-Object Drawing.Drawing2D.LinearGradientBrush ([Drawing.Rectangle]::new(0, 0, $w, $h)), $bg2, $bg1, 65.0
    $g.FillRectangle($bgBrush, 0, 0, $w, $h)
    return @($bmp, $g)
}
function Round-Rect($x, $y, $w, $h, $r) {
    $p = New-Object Drawing.Drawing2D.GraphicsPath; $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90); $p.CloseFigure(); return $p
}
# Draws a few glass zone panels: the Region Control idea in one picture.
function Draw-Zones($g, $x, $y, $w, $h, $s) {
    $gap = 8 * $s; $r = 10 * $s
    $cells = @(@(0, 0, 0.58, 1.0, 0), @(0.58, 0, 0.42, 0.55, 1), @(0.58, 0.55, 0.42, 0.45, 0))
    foreach ($c in $cells) {
        $cx = $x + $c[0] * $w + $gap / 2; $cy = $y + $c[1] * $h + $gap / 2
        $cw = $c[2] * $w - $gap; $ch = $c[3] * $h - $gap
        $path = Round-Rect $cx $cy $cw $ch $r
        $alpha = if ($c[4]) { 110 } else { 38 }
        $fill = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb($alpha, $(if ($c[4]) { $accent } else { [Drawing.Color]::White })))
        $g.FillPath($fill, $path)
        $pen = New-Object Drawing.Pen ([Drawing.Color]::FromArgb($(if ($c[4]) { 230 } else { 90 }), $(if ($c[4]) { $accent } else { [Drawing.Color]::White })), (1.5 * $s))
        $g.DrawPath($pen, $path)
    }
}
function Draw-Icon($g, $x, $y, $size) {
    if (-not (Test-Path $icoPath)) { return }
    $ico = New-Object Drawing.Icon $icoPath, ([Drawing.Size]::new($size, $size))
    $g.DrawIcon($ico, [Drawing.Rectangle]::new($x, $y, $size, $size))
}
function Save($bmp, $g, $name) { $g.Dispose(); $bmp.Save((Join-Path $out $name), [Drawing.Imaging.ImageFormat]::Bmp); $bmp.Dispose() }

# Large wizard image: 164x314 base, plus 246x459 for high DPI.
foreach ($sz in @(@(164, 314, 1.0, 'wizard-164.bmp'), @(246, 459, 1.5, 'wizard-246.bmp'))) {
    $w = $sz[0]; $h = $sz[1]; $s = $sz[2]
    $bmp, $g = New-Canvas $w $h
    Draw-Zones $g (14 * $s) (96 * $s) (($w - 28 * $s)) (150 * $s) $s
    Draw-Icon $g ([int](($w - 48 * $s) / 2)) ([int](28 * $s)) ([int](48 * $s))
    $font = New-Object Drawing.Font 'Segoe UI Semibold', (11 * $s), ([Drawing.FontStyle]::Regular), ([Drawing.GraphicsUnit]::Pixel)
    $sf = New-Object Drawing.StringFormat; $sf.Alignment = 'Center'
    $g.DrawString('Region Control', $font, [Drawing.Brushes]::White, [Drawing.RectangleF]::new(0, 262 * $s, $w, 20 * $s), $sf)
    $font2 = New-Object Drawing.Font 'Segoe UI', (9 * $s), ([Drawing.FontStyle]::Regular), ([Drawing.GraphicsUnit]::Pixel)
    $g.DrawString('inNATE Tools', $font2, (New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(150, 200, 230))), [Drawing.RectangleF]::new(0, 282 * $s, $w, 16 * $s), $sf)
    Save $bmp $g $sz[3]
}
# Small image (top right of inner pages): 55x55 and 110x110.
foreach ($sz in @(@(55, 1.0, 'small-55.bmp'), @(110, 2.0, 'small-110.bmp'))) {
    $w = $sz[0]; $s = $sz[1]
    $bmp, $g = New-Canvas $w $w
    Draw-Zones $g (6 * $s) (6 * $s) (43 * $s) (43 * $s) ($s * 0.45)
    Save $bmp $g $sz[2]
}
Get-ChildItem $out | ForEach-Object { "{0}: {1} bytes" -f $_.Name, $_.Length }
