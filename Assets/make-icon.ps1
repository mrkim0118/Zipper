# Draws the Zipper icon and writes Assets/icon.ico (multi-size, PNG entries) and Assets/icon.png (128px).
# Design: navy rounded square, silver zipper (closed at the top, opening below the slider), pull tab.
Add-Type -AssemblyName System.Drawing
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$D = [System.Drawing.Drawing2D.LinearGradientMode]

function C($a, $r, $g, $b) { [System.Drawing.Color]::FromArgb($a, $r, $g, $b) }
function P($x, $y) { [System.Drawing.PointF]::new($x, $y) }
function RoundRect($x, $y, $w, $h, $r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); $p
}
function Silver($rect, $mode) {
    $b = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, (C 255 250 251 253), (C 255 140 150 166), $mode
    $blend = New-Object System.Drawing.Drawing2D.ColorBlend 4
    $blend.Colors = @((C 255 252 253 255), (C 255 214 220 229), (C 255 150 160 176), (C 255 205 212 222))
    $blend.Positions = @(0.0, 0.35, 0.75, 1.0)
    $b.InterpolationColors = $blend; $b
}

function New-IconBitmap([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $f = $s / 256.0
    $g.ScaleTransform($f, $f)        # draw everything in a 256-unit space
    $small = $s -le 32               # tiny sizes: fewer, bigger shapes so they stay readable
    $cx = 128

    # navy body with a soft top highlight and a thin inner rim
    $body = RoundRect 10 10 236 236 52
    # point-based brushes: PowerShell picks the wrong overload for the RectangleF+mode form
    $g.FillPath((New-Object System.Drawing.Drawing2D.LinearGradientBrush (P 0 9), (P 0 247), (C 255 40 64 124), (C 255 11 22 56)), $body)
    $g.SetClip($body)
    $g.FillRectangle((New-Object System.Drawing.Drawing2D.LinearGradientBrush (P 0 9), (P 0 141), (C 55 255 255 255), (C 0 255 255 255)), 0, 10, 256, 130)

    # fabric tape: one band above the slider, two diverging strips below it
    $tape = New-Object System.Drawing.SolidBrush (C 255 9 17 44)
    $open = 160; $k = 0.42; $bot = 250
    $g.FillRectangle($tape, $cx - 30, 0, 60, $open)
    $spread = ($bot - $open) * $k
    $g.FillPolygon($tape, [System.Drawing.PointF[]]@((P ($cx - 30) $open), (P ($cx - 1) $open), (P ($cx - 1 - $spread) $bot), (P ($cx - 30 - $spread) $bot)))
    $g.FillPolygon($tape, [System.Drawing.PointF[]]@((P ($cx + 1) $open), (P ($cx + 30) $open), (P ($cx + 30 + $spread) $bot), (P ($cx + 1 + $spread) $bot)))
    if (-not $small) {
        $stitch = New-Object System.Drawing.Pen (C 90 120 140 190), 1.6
        $stitch.DashStyle = 'Dash'
        $g.DrawLine($stitch, $cx - 26, 0, $cx - 26, $open); $g.DrawLine($stitch, $cx + 26, 0, $cx + 26, $open)
    }

    # interlocking silver teeth; below the slider they part with the tape
    $pitch = if ($small) { 26 } else { 15 }
    $th = $pitch * 0.62; $tw = 22
    $edge = New-Object System.Drawing.Pen (C 200 46 56 76), 1.2
    for ($y = 4; $y -lt $bot; $y += $pitch) {
        foreach ($side in -1, 1) {
            $ty = $y + ($(if ($side -gt 0) { $pitch / 2 } else { 0 }))
            $dx = if ($ty -gt $open) { ($ty - $open) * $k } else { 0 }
            $x = if ($side -lt 0) { $cx - 20 - $dx } else { $cx - 2 + $dx }
            $r = [System.Drawing.RectangleF]::new($x, $ty, $tw, $th)
            $tooth = RoundRect $x $ty $tw $th ($th * 0.35)
            $g.FillPath((Silver $r $D::Vertical), $tooth)
            if (-not $small) { $g.DrawPath($edge, $tooth) }
        }
    }
    $g.ResetClip()

    # slider: tapered metal body with a shadow, ridge and rivet
    $g.FillEllipse((New-Object System.Drawing.SolidBrush (C 90 0 0 0)), $cx - 34, 176, 68, 16)
    $slider = New-Object System.Drawing.Drawing2D.GraphicsPath
    $slider.AddPolygon([System.Drawing.PointF[]]@((P ($cx - 32) 128), (P ($cx + 32) 128), (P ($cx + 21) 182), (P ($cx - 21) 182)))
    $sb = RoundRect ($cx - 32) 124 64 14 7
    $g.FillPath((Silver ([System.Drawing.RectangleF]::new($cx - 32, 0, 64, 1)) $D::Horizontal), $slider)
    $g.FillPath((Silver ([System.Drawing.RectangleF]::new($cx - 32, 124, 64, 14)) $D::Vertical), $sb)
    $dark = New-Object System.Drawing.Pen (C 255 58 68 88), 2
    $g.DrawPath($dark, $slider); $g.DrawPath($dark, $sb)
    if (-not $small) {
        $g.DrawLine((New-Object System.Drawing.Pen (C 160 255 255 255), 2), $cx - 8, 142, $cx - 4, 176)
        $g.FillEllipse((New-Object System.Drawing.SolidBrush (C 255 70 80 100)), $cx - 6, 146, 12, 12)
        $g.FillEllipse((New-Object System.Drawing.SolidBrush (C 255 230 234 240)), $cx - 3.5, 148, 6, 6)
    }

    # pull tab hanging from the slider, with a hole
    $tab = RoundRect ($cx - 17) 170 34 64 14
    $g.FillPath((Silver ([System.Drawing.RectangleF]::new($cx - 17, 0, 34, 1)) $D::Horizontal), $tab)
    $g.DrawPath($dark, $tab)
    $g.FillPath((New-Object System.Drawing.SolidBrush (C 255 16 30 70)), (RoundRect ($cx - 7) 204 14 20 7))
    if (-not $small) { $g.DrawLine((New-Object System.Drawing.Pen (C 150 255 255 255), 2.5), $cx - 10, 178, $cx - 10, 224) }
    $g.FillEllipse((Silver ([System.Drawing.RectangleF]::new($cx - 9, 164, 18, 14)) $D::Vertical), $cx - 9, 164, 18, 14)
    $g.DrawEllipse($dark, $cx - 9, 164, 18, 14)

    # rim on top of everything
    $g.DrawPath((New-Object System.Drawing.Pen (C 60 255 255 255), 2), (RoundRect 11 11 234 234 51))
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
if ($args[0]) { (New-IconBitmap 512).Save($args[0], [System.Drawing.Imaging.ImageFormat]::Png) }  # optional large preview
