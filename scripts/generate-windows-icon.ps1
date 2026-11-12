param([string]$Output = (Join-Path $PSScriptRoot '..\windows\packaging\ReviewQ.ico'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($Output))) | Out-Null
$images = [System.Collections.Generic.List[byte[]]]::new()
$sizes = @(16, 24, 32, 48, 64, 128, 256)
foreach ($size in $sizes) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $gradient = $null; $pen = $null; $stream = [System.IO.MemoryStream]::new()
    try {
        $graphics.SmoothingMode = 'AntiAlias'
        $graphics.ScaleTransform($size / 256.0, $size / 256.0)
        $path.AddArc(16, 16, 100, 100, 180, 90)
        $path.AddArc(140, 16, 100, 100, 270, 90)
        $path.AddArc(140, 140, 100, 100, 0, 90)
        $path.AddArc(16, 140, 100, 100, 90, 90)
        $path.CloseFigure()
        $gradient = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
            [System.Drawing.Rectangle]::new(16, 16, 224, 224),
            [System.Drawing.Color]::FromArgb(41, 143, 247),
            [System.Drawing.Color]::FromArgb(13, 56, 148), 90.0)
        $graphics.FillPath($gradient, $path)
        $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, 16)
        $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
        $graphics.DrawLine($pen, 82.5, 88, 82.5, 168.5)
        $graphics.DrawLine($pen, 177.5, 168.5, 177.5, 124.5)
        $graphics.DrawBezier($pen, 177.5, 124.5, 177.5, 93.5, 162.5, 74, 137.5, 74)
        $graphics.DrawLine($pen, 137.5, 74, 127.5, 74)
        $graphics.DrawLines($pen, [System.Drawing.PointF[]]@(
            [System.Drawing.PointF]::new(142.5, 53), [System.Drawing.PointF]::new(121.5, 74), [System.Drawing.PointF]::new(142.5, 95)))
        $pen.Width = 14.5
        $graphics.DrawEllipse($pen, 63.5, 49.5, 38, 38)
        $graphics.DrawEllipse($pen, 63.5, 168.5, 38, 38)
        $pen.Color = [System.Drawing.Color]::FromArgb(120, 247, 232)
        $graphics.DrawEllipse($pen, 158.5, 168.5, 38, 38)
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $images.Add($stream.ToArray())
    } finally {
        if ($pen) { $pen.Dispose() }; if ($gradient) { $gradient.Dispose() }
        $path.Dispose(); $graphics.Dispose(); $bitmap.Dispose(); $stream.Dispose()
    }
}
$file = [System.IO.File]::Create([System.IO.Path]::GetFullPath($Output))
$writer = [System.IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($image in $images) { $writer.Write([byte[]]$image) }
} finally { $writer.Dispose(); $file.Dispose() }
Write-Output "Generated $Output"
