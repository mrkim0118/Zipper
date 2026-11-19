# Draws the Zipper icon and writes Assets/icon.ico (multi-size, PNG entries) and Assets/icon.png (128px).
Add-Type -AssemblyName System.Drawing
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

function New-IconBitmap([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $f = $s / 256.0
    # rounded square body
    $r = 56 * $f; $m = 12 * $f; $w = $s - 2 * $m
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($m, $m, $r, $r, 180, 90); $path.AddArc($m + $w - $r, $m, $r, $r, 270, 90)
    $path.AddArc($m + $w - $r, $m + $w - $r, $r, $r, 0, 90); $path.AddArc($m, $m + $w - $r, $r, $r, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 0)), ([System.Drawing.PointF]::new(0, $s)), ([System.Drawing.Color]::FromArgb(255, 72, 149, 239)), ([System.Drawing.Color]::FromArgb(255, 30, 94, 200))
    $g.FillPath($brush, $path)
    # zipper teeth down the middle
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $cx = $s / 2; $tooth = 18 * $f
    for ($y = 34 * $f; $y -lt 150 * $f; $y += 2 * $tooth) {
        $g.FillRectangle($white, $cx - 2 * $tooth, $y, 2 * $tooth - 2 * $f, $tooth - 3 * $f)
        $g.FillRectangle($white, $cx + 2 * $f, $y + $tooth, 2 * $tooth - 2 * $f, $tooth - 3 * $f)
    }
    # pull tab
    $g.FillRectangle($white, $cx - 30 * $f, 160 * $f, 60 * $f, 22 * $f)
    $tab = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tab.AddArc($cx - 26 * $f, 176 * $f, 52 * $f, 60 * $f, 0, 180)
    $g.FillPath($white, $tab)
    $g.FillEllipse($brush, $cx - 10 * $f, 196 * $f, 20 * $f, 20 * $f)
    $g.Dispose()
    return $bmp
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $ms = New-Object System.IO.MemoryStream
    (New-IconBitmap $s).Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    , $ms.ToArray()
}
$out = New-Object System.IO.BinaryWriter ([System.IO.File]::Create((Join-Path $here 'icon.ico')))
$out.Write([uint16]0); $out.Write([uint16]1); $out.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $d = $sizes[$i] % 256
    $out.Write([byte]$d); $out.Write([byte]$d); $out.Write([byte]0); $out.Write([byte]0)
    $out.Write([uint16]1); $out.Write([uint16]32); $out.Write([uint32]$pngs[$i].Length); $out.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $out.Write($p) }
$out.Close()
(New-IconBitmap 128).Save((Join-Path $here 'icon.png'), [System.Drawing.Imaging.ImageFormat]::Png)
