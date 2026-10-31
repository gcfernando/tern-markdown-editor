# Generates the Tern application icon (a teal "T" mark on a navy rounded square) as tern.png and a multi-size tern.ico.
# Run once; the outputs are committed under src\Tern.Desktop\Assets.
Add-Type -AssemblyName System.Drawing

$outDir = Join-Path $PSScriptRoot '..\src\Tern.Desktop\Assets'
New-Item -ItemType Directory -Force $outDir | Out-Null

function New-Mark([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    $radius = $size * 0.22
    $d = $radius * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($size - $d - 1, 0, $d, $d, 270, 90)
    $path.AddArc($size - $d - 1, $size - $d - 1, $d, $d, 0, 90)
    $path.AddArc(0, $size - $d - 1, $d, $d, 90, 90)
    $path.CloseFigure()
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 0)), ([System.Drawing.PointF]::new($size, $size)), ([System.Drawing.ColorTranslator]::FromHtml('#14213A')), ([System.Drawing.ColorTranslator]::FromHtml('#0B1220'))
    $g.FillPath($bg, $path)

    $teal = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#2DD4BF'))
    $bar = $size * 0.15
    $top = $size * 0.24
    $left = $size * 0.2
    $width = $size * 0.6
    $g.FillRectangle($teal, [single]$left, [single]$top, [single]$width, [single]$bar)
    $g.FillRectangle($teal, [single](($size - $bar) / 2), [single]$top, [single]$bar, [single]($size * 0.52))

    $g.Dispose()
    return $bmp
}

function Get-PngBytes($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($s in $sizes) { , @($s, (Get-PngBytes (New-Mark $s))) }

[System.IO.File]::WriteAllBytes((Join-Path $outDir 'tern.png'), (Get-PngBytes (New-Mark 256)))

$ms = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ms
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$images.Count)
$offset = 6 + (16 * $images.Count)
foreach ($img in $images) {
    $s = $img[0]; $data = $img[1]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim)
    $w.Write([byte]$dim)
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$data.Length); $w.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($img in $images) { $w.Write([byte[]]$img[1]) }
[System.IO.File]::WriteAllBytes((Join-Path $outDir 'tern.ico'), $ms.ToArray())
Write-Host "Wrote tern.png and tern.ico to $outDir"
