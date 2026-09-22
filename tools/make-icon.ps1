# Draws the GlassLink icon (an attitude indicator: sky over earth, white horizon, yellow wings) at 16, 24, 32, 48,
# 64, 128 and 256 px and writes dotnet\src\GlassLink.Dmc\glasslink.ico (PNG-compressed entries, as Windows Vista and
# later read them). The same picture as StatusWindow.AppIcon in code; run this again if that drawing changes.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$target = "$root\dotnet\src\GlassLink.Dmc\glasslink.ico"

function Draw([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $s = $size / 64.0
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = 14 * $s; $d = $r * 2; $m = 2 * $s; $w = $size - 2 * $m
    $path.AddArc($m, $m, $d, $d, 180, 90)
    $path.AddArc($m + $w - $d, $m, $d, $d, 270, 90)
    $path.AddArc($m + $w - $d, $m + $w - $d, $d, $d, 0, 90)
    $path.AddArc($m, $m + $w - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $g.SetClip($path)
    $sky = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0x2f, 0x8f, 0xe0))
    $earth = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0x8a, 0x5a, 0x2b))
    $g.FillRectangle($sky, 0, 0, $size, $size / 2)
    $g.FillRectangle($earth, 0, $size / 2, $size, $size / 2)
    $horizon = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([Math]::Max(1, 3 * $s))
    $g.DrawLine($horizon, 0, $size / 2, $size, $size / 2)
    $wings = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(0xff, 0xd0, 0x30)), ([Math]::Max(1.5, 5 * $s))
    $wings.StartCap = 'Round'; $wings.EndCap = 'Round'
    $g.DrawLine($wings, 12 * $s, $size / 2, 24 * $s, $size / 2)
    $g.DrawLine($wings, 40 * $s, $size / 2, 52 * $s, $size / 2)
    $g.FillEllipse([System.Drawing.Brushes]::White, 29 * $s, 29 * $s, 6 * $s, 6 * $s)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = @(); foreach ($size in $sizes) { $images += ,[byte[]](Draw $size) }
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $size = $sizes[$i]; [byte[]]$png = $images[$i]
    $bw.Write([byte]($(if ($size -ge 256) { 0 } else { $size })))
    $bw.Write([byte]($(if ($size -ge 256) { 0 } else { $size })))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$png.Length); $bw.Write([uint32]$offset)
    $offset += $png.Length
}
foreach ($png in $images) { $bw.Write([byte[]]$png) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($target, $out.ToArray())
"wrote $target ($($out.Length) bytes)"
