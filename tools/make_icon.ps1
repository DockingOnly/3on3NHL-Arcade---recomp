# Renders launcher/launcher.ico: a hockey puck on an ice-blue rounded tile.
# Run from the repository root: pwsh tools/make_icon.ps1
Add-Type -AssemblyName System.Drawing

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0

    # Rounded tile
    $r = 56 * $s
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $r, $r, 180, 90)
    $path.AddArc($size - $r - 1, 0, $r, $r, 270, 90)
    $path.AddArc($size - $r - 1, $size - $r - 1, $r, $r, 0, 90)
    $path.AddArc(0, $size - $r - 1, $r, $r, 90, 90)
    $path.CloseFigure()
    $tile = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $size, $size), ([System.Drawing.Color]::FromArgb(255, 0x3C, 0x9C, 0xFF)), ([System.Drawing.Color]::FromArgb(255, 0x12, 0x3A, 0x9E))
    $g.FillPath($tile, $path)

    # Faint centre-ice circle and red line
    $g.SetClip($path)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(60, 255, 255, 255)), (10 * $s)
    $g.DrawEllipse($pen, 40 * $s, 40 * $s, 176 * $s, 176 * $s)
    $red = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(150, 0xE6, 0x39, 0x46))
    $g.FillRectangle($red, 120 * $s, 0, 16 * $s, $size)
    $g.ResetClip()

    # Puck: side, then top face
    $cx = 128 * $s; $w = 150 * $s; $h = 62 * $s; $depth = 34 * $s; $top = 98 * $s
    $shadow = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(90, 0, 0, 20))
    $g.FillEllipse($shadow, $cx - $w / 2 + 6 * $s, $top + $depth + 10 * $s, $w, $h)
    $side = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x0A, 0x0E, 0x18))
    $g.FillEllipse($side, $cx - $w / 2, $top + $depth, $w, $h)
    $g.FillRectangle($side, $cx - $w / 2, $top + $h / 2, $w, $depth)
    $face = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, $top), (New-Object System.Drawing.PointF 0, ($top + $h)), ([System.Drawing.Color]::FromArgb(255, 0x4A, 0x58, 0x72)), ([System.Drawing.Color]::FromArgb(255, 0x1A, 0x22, 0x33))
    $g.FillEllipse($face, $cx - $w / 2, $top, $w, $h)
    $rim = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(200, 0x9F, 0xDC, 0xFF)), ([Math]::Max(1, 3 * $s))
    $g.DrawEllipse($rim, $cx - $w / 2, $top, $w, $h)

    $g.Dispose()
    return $bmp
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($sz in $sizes) {
    $bmp = New-IconBitmap $sz
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

$out = Join-Path $PSScriptRoot '..\launcher\launcher.ico'
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $data = $images[$i]
    $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$data.Length); $bw.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($data in $images) { $bw.Write($data) }
$bw.Dispose()
Write-Host "Wrote $out"
