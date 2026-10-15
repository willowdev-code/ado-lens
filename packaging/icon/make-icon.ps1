<#
.SYNOPSIS
    Draws the ADO Lens icon: a lens over three work item bars on an Azure blue tile.
    Writes adolens.ico (16-256 px, PNG frames) and icon.png (512 px) next to this script.
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundedRect([float] $x, [float] $y, [float] $w, [float] $h, [float] $r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconBitmap([int] $size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = [float] $size

    # Tile: Azure blue, lighter at the top.
    $margin = [Math]::Max(0.5, $s * 0.03)
    $tile = New-RoundedRect $margin $margin ($s - 2 * $margin) ($s - 2 * $margin) ($s * 0.2)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF 0, $s),
        ([System.Drawing.Color]::FromArgb(255, 0, 137, 230)), ([System.Drawing.Color]::FromArgb(255, 0, 90, 170))
    $g.FillPath($grad, $tile)

    $white = [System.Drawing.Color]::White
    # Lens: a ring, centred a little up and left, with a handle to the bottom right.
    $cx = $s * 0.43; $cy = $s * 0.43; $r = $s * 0.24
    $ring = [Math]::Max(1.5, $s * 0.075)
    if ($size -ge 32) {
        # Three work item bars inside the lens (left out at small sizes, where they would blur).
        $bar = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(235, 255, 255, 255))
        $bh = $s * 0.045
        foreach ($i in 0..2) {
            $w = $r * (1.05 - 0.25 * $i)
            $y = $cy - $r * 0.42 + $i * $r * 0.42 - $bh / 2
            $path = New-RoundedRect ($cx - $r * 0.55) $y $w $bh ($bh / 2)
            $g.FillPath($bar, $path)
        }
    }
    $pen = New-Object System.Drawing.Pen $white, $ring
    $g.DrawEllipse($pen, $cx - $r, $cy - $r, 2 * $r, 2 * $r)
    $handle = New-Object System.Drawing.Pen $white, ([Math]::Max(2, $s * 0.11))
    $handle.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $handle.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $a = 0.7071 * ($r + $ring / 2)
    $g.DrawLine($handle, $cx + $a, $cy + $a, $s * 0.76, $s * 0.76)
    $g.Dispose()
    return $bmp
}

$dir = $PSScriptRoot
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = foreach ($n in $sizes) {
    $bmp = New-IconBitmap $n
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

# ICO: header, one directory entry per size, then the PNG frames.
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $n = $sizes[$i]
    $dim = if ($n -ge 256) { 0 } else { $n }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$frames[$i].Length); $w.Write([UInt32]$offset)
    $offset += $frames[$i].Length
}
foreach ($f in $frames) { $w.Write($f) }
$w.Flush()
[IO.File]::WriteAllBytes((Join-Path $dir 'adolens.ico'), $out.ToArray())

$big = New-IconBitmap 512
$big.Save((Join-Path $dir 'icon.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$big.Dispose()
Write-Host "written adolens.ico and icon.png in $dir"
