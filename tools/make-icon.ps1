<#
.SYNOPSIS
  Draws the ScreenCast Presenter icon at every Windows icon size and packs it into app.ico.
.DESCRIPTION
  A blue rounded tile with a white screen; inside it, the yellow outline SCP draws around
  whatever is being presented. Each size is drawn on its own (not scaled down) so small sizes
  stay crisp: below 32 px the screen loses its stand and the outline becomes a solid block.
  Output: src\ScreenCastPresenter\Assets\app.ico (+ PNG previews in artifacts\icon-preview).
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$assets = Join-Path $root 'src\ScreenCastPresenter\Assets'
$preview = Join-Path $root 'artifacts\icon-preview' # previews only; not part of the source
New-Item -ItemType Directory -Force $assets, $preview | Out-Null

function New-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Min($r * 2, [Math]::Min($w, $h))
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# Tiny sizes are laid out on whole pixels (white screen, dark glass, yellow window), so the
# taskbar and tray icons are sharp instead of blurred by anti-aliasing.
$PixelLayouts = @{
    # size = tile radius, screen x, y, w, h, bezel, window inset
    16 = @(3, 2, 3, 12, 10, 1, 1)
    20 = @(4, 2, 4, 16, 12, 1, 1)
    24 = @(5, 3, 5, 18, 14, 2, 1)
}

function Draw-PixelIcon([int]$size) {
    $L = $PixelLayouts[$size]
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.SmoothingMode = 'AntiAlias'
    $tile = New-RoundedPath 0 0 $size $size $L[0]
    $rect = New-Object System.Drawing.RectangleF 0, 0, $size, $size
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 0x1D, 0x4E, 0xD8)), ([System.Drawing.Color]::FromArgb(255, 0x4C, 0x8D, 0xFF)), 45.0
    $g.FillPath($grad, $tile)
    $g.SmoothingMode = 'None'
    $x = $L[1]; $y = $L[2]; $w = $L[3]; $h = $L[4]; $b = $L[5]; $i = $L[6]
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0xFA, 0xFA, 0xFB))), $x, $y, $w, $h)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x0B, 0x0B, 0x0C))), $x + $b, $y + $b, $w - 2 * $b, $h - 2 * $b)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0xFF, 0xC8, 0x3D))), $x + $b + $i, $y + $b + $i, $w - 2 * ($b + $i), $h - 2 * ($b + $i))
    $g.Dispose()
    return $bmp
}

function Draw-Icon([int]$size) {
    if ($PixelLayouts.ContainsKey($size)) { return Draw-PixelIcon $size }
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0
    $small = $size -lt 32

    # Tile: deep blue to bright blue, top-left to bottom-right.
    $inset = [single]([Math]::Max(0.5, 8 * $s))
    $tileSize = [single]($size - 2 * $inset)
    $tile = New-RoundedPath $inset $inset $tileSize $tileSize ([single](56 * $s))
    $rect = New-Object System.Drawing.RectangleF $inset, $inset, $tileSize, $tileSize
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 0x1D, 0x4E, 0xD8)), ([System.Drawing.Color]::FromArgb(255, 0x4C, 0x8D, 0xFF)), 45.0
    $g.FillPath($grad, $tile)
    # Soft top highlight for depth (skipped when tiny).
    if (-not $small) {
        $hl = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(60, 255, 255, 255)), ([System.Drawing.Color]::FromArgb(0, 255, 255, 255)), 90.0
        $g.FillPath($hl, $tile)
    }

    # Screen: white rounded rectangle, dark glass inside.
    $white = [System.Drawing.Color]::FromArgb(255, 0xFA, 0xFA, 0xFB)
    if ($small) { $sx = 40; $sy = 56; $sw = 176; $sh = 124 } else { $sx = 44; $sy = 56; $sw = 168; $sh = 116 }
    $screen = New-RoundedPath ([single]($sx * $s)) ([single]($sy * $s)) ([single]($sw * $s)) ([single]($sh * $s)) ([single](16 * $s))
    $g.FillPath((New-Object System.Drawing.SolidBrush $white), $screen)
    $b = [Math]::Max(1.0, 12 * $s)  # bezel
    $glass = New-RoundedPath ([single]($sx * $s + $b)) ([single]($sy * $s + $b)) ([single]($sw * $s - 2 * $b)) ([single]($sh * $s - 2 * $b)) ([single]([Math]::Max(1, 8 * $s)))
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x0B, 0x0B, 0x0C))), $glass)

    # The presented window: SCP's yellow outline around a lighter pane.
    $yellow = [System.Drawing.Color]::FromArgb(255, 0xFF, 0xC8, 0x3D)
    $wx = ($sx + 30) * $s; $wy = ($sy + 24) * $s; $ww = ($sw - 60) * $s; $wh = ($sh - 48) * $s
    if ($small) {
        $g.FillRectangle((New-Object System.Drawing.SolidBrush $yellow), [single]$wx, [single]$wy, [single]$ww, [single]$wh)
    } else {
        $pane = New-RoundedPath ([single]$wx) ([single]$wy) ([single]$ww) ([single]$wh) ([single](6 * $s))
        $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x2A, 0x3A, 0x5C))), $pane)
        $pen = New-Object System.Drawing.Pen $yellow, ([single]([Math]::Max(1.5, 8 * $s)))
        $pen.LineJoin = 'Round'
        $g.DrawPath($pen, $pane)
        # A title bar line inside the window, so it reads as a window.
        $bar = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(200, 0xFA, 0xFA, 0xFB)), ([single]([Math]::Max(1, 6 * $s)))
        $g.DrawLine($bar, [single]($wx + 14 * $s), [single]($wy + 16 * $s), [single]($wx + $ww * 0.45), [single]($wy + 16 * $s))
    }

    # Stand (not at tiny sizes, where it turns into mush).
    if (-not $small) {
        $stand = New-RoundedPath ([single](104 * $s)) ([single](($sy + $sh + 12) * $s)) ([single](48 * $s)) ([single](14 * $s)) ([single](7 * $s))
        $g.FillPath((New-Object System.Drawing.SolidBrush $white), $stand)
        $neck = New-Object System.Drawing.RectangleF ([single](120 * $s)), ([single](($sy + $sh) * $s)), ([single](16 * $s)), ([single](14 * $s))
        $g.FillRectangle((New-Object System.Drawing.SolidBrush $white), $neck)
    }
    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @()
foreach ($size in $sizes) {
    $bmp = Draw-Icon $size
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Save((Join-Path $preview "icon-$size.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $pngs += , @($size, $ms.ToArray())
}

# ICO container with PNG-compressed images (supported since Windows Vista).
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($entry in $pngs) {
    $size = $entry[0]; $data = $entry[1]
    $dim = if ($size -ge 256) { 0 } else { $size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]$data.Length); $w.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($entry in $pngs) { $w.Write([byte[]]$entry[1]) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets 'app.ico'), $out.ToArray())
"Wrote $(Join-Path $assets 'app.ico') ($($out.Length) bytes, sizes $($sizes -join ', '))"
