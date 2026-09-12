# Draws the Downlism mark and writes every raster the product needs.
#
# The mark is three stacked bars of decreasing width. Read as a silhouette they point down;
# read as parts they are the segments of a file being filled by separate connections, which is
# the one idea the whole product is built around. Drawn with primitives rather than traced from
# an SVG so the shape stays crisp at 16 px, where an imported path turns to mush.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$assets = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../docs/assets'))
[IO.Directory]::CreateDirectory($assets) | Out-Null

function New-Mark([int]$Size) {
    $bitmap = [Drawing.Bitmap]::new($Size, $Size)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

    $scale = $Size / 256.0
    $radius = 56 * $scale

    # Rounded tile, in the deep slate blue the Fluent system icons sit comfortably beside.
    $tile = [Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $radius * 2
    $tile.AddArc(0, 0, $diameter, $diameter, 180, 90)
    $tile.AddArc($Size - $diameter, 0, $diameter, $diameter, 270, 90)
    $tile.AddArc($Size - $diameter, $Size - $diameter, $diameter, $diameter, 0, 90)
    $tile.AddArc(0, $Size - $diameter, $diameter, $diameter, 90, 90)
    $tile.CloseFigure()

    $gradient = [Drawing.Drawing2D.LinearGradientBrush]::new(
        [Drawing.Point]::new(0, 0),
        [Drawing.Point]::new(0, $Size),
        [Drawing.Color]::FromArgb(255, 22, 86, 116),
        [Drawing.Color]::FromArgb(255, 12, 48, 68))
    $graphics.FillPath($gradient, $tile)

    # Widths narrow downward; opacity fades the same way, so the lowest bar reads as the
    # segment still arriving.
    $bars = @(
        @{ Width = 148; Alpha = 255 },
        @{ Width = 104; Alpha = 216 },
        @{ Width = 60;  Alpha = 168 }
    )
    $barHeight = 30 * $scale
    $gap = 16 * $scale
    $totalHeight = ($bars.Count * $barHeight) + (($bars.Count - 1) * $gap)
    $top = ($Size - $totalHeight) / 2

    foreach ($bar in $bars) {
        $width = $bar.Width * $scale
        $left = ($Size - $width) / 2
        $brush = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb($bar.Alpha, 255, 255, 255))
        $path = [Drawing.Drawing2D.GraphicsPath]::new()
        $cap = [Math]::Min($barHeight, $width)
        $path.AddArc($left, $top, $cap, $barHeight, 90, 180)
        $path.AddArc($left + $width - $cap, $top, $cap, $barHeight, 270, 180)
        $path.CloseFigure()
        $graphics.FillPath($brush, $path)
        $path.Dispose()
        $brush.Dispose()
        $top += $barHeight + $gap
    }

    $gradient.Dispose()
    $tile.Dispose()
    $graphics.Dispose()
    return $bitmap
}

# Full-size art for the installer window and the extension.
foreach ($target in @(
    @{ Path = (Join-Path $assets 'downlism-icon-fluent.png'); Size = 256 },
    @{ Path = (Join-Path $PSScriptRoot '../extension/icon128.png'); Size = 128 }
)) {
    $bitmap = New-Mark $target.Size
    $bitmap.Save([IO.Path]::GetFullPath($target.Path), [Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

# Multi-resolution .ico for the executables and the taskbar.
$frames = @()
foreach ($size in @(16, 24, 32, 48, 64, 256)) {
    $bitmap = New-Mark $size
    $stream = [IO.MemoryStream]::new()
    $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
    $frames += @{ Size = $size; Bytes = $stream.ToArray() }
    $stream.Dispose()
    $bitmap.Dispose()
}

$output = Join-Path $assets 'downlism.ico'
$fileStream = [IO.File]::Create($output)
$writer = [IO.BinaryWriter]::new($fileStream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally {
    $writer.Dispose()
    $fileStream.Dispose()
}

Write-Output $output
