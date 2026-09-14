# Turns the Downlism brand mark into every raster the product ships.
#
# The mark is a folded ribbon bent into a down arrow: one continuous strip of blue that turns
# over on itself, which is the shape of the product — one file, split into strands that travel
# separately and fold back into a single thing. It is drawn once, by hand, and lives in
# docs/assets/brand/downlism-mark.png on an opaque white ground.
#
# This script does the three jobs that separate that drawing from a shippable icon: it lifts
# the white ground out to alpha, trims to the mark and re-pads it so every size has the same
# optical margin, and writes the sizes Windows actually asks for. Downsampling the 1250 px
# original beats redrawing at 16 px — the fold survives as a diagonal value break even when
# the individual edges are gone.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$assets = Join-Path $projectRoot 'docs/assets'
$source = Join-Path $assets 'brand/downlism-mark.png'
if (!(Test-Path -LiteralPath $source)) { throw "Brand mark missing: $source" }

# Per-pixel work on a 1.5-megapixel source is minutes of GetPixel and milliseconds of this.
Add-Type -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;

public static class DownlismMark
{
    // White is the paper the mark was drawn on, not part of it, so it has to become alpha
    // rather than be trusted to sit behind the icon: Windows composites these over the user's
    // taskbar, their Mica title bars and a dark tray, and a white square would show in all
    // three. A flood fill from the border is what keeps the light cyan inside the ribbon --
    // a plain whiteness threshold cannot tell the brightest part of the fold from the ground
    // around it, but the ground is the only white region that touches the frame edge.
    public static Bitmap Cut(Bitmap source)
    {
        int width = source.Width, height = source.Height;
        Bitmap flat = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        // Flattened onto white first, because the drawing may arrive either way: exported flat
        // it has a white ground, exported with alpha it has transparent black, and reading the
        // second as if it were the first cuts the mark out and keeps the background. One white
        // ground underneath makes both inputs the same input.
        using (Graphics g = Graphics.FromImage(flat))
        {
            g.Clear(Color.White);
            g.DrawImage(source, 0, 0, width, height);
        }

        BitmapData data = flat.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        byte[] pixels = new byte[data.Stride * height];
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);

        bool[] ground = new bool[width * height];
        int[] queue = new int[width * height];
        int head = 0, tail = 0;
        for (int x = 0; x < width; x++)
        {
            Seed(pixels, data.Stride, ground, queue, ref tail, x, 0, width);
            Seed(pixels, data.Stride, ground, queue, ref tail, x, height - 1, width);
        }
        for (int y = 0; y < height; y++)
        {
            Seed(pixels, data.Stride, ground, queue, ref tail, 0, y, width);
            Seed(pixels, data.Stride, ground, queue, ref tail, width - 1, y, width);
        }
        while (head < tail)
        {
            int index = queue[head++];
            int x = index % width, y = index / width;
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) { continue; }
                    Seed(pixels, data.Stride, ground, queue, ref tail, nx, ny, width);
                }
            }
        }

        // The drawing is anti-aliased, so the pixels just inside the fill are blends of blue
        // and paper. Left opaque they ring the mark in white on a dark taskbar; cut on the
        // same hard threshold they leave a stair-stepped edge. Grading their alpha by how much
        // paper is left in them, and un-blending the colour by the same fraction, reconstructs
        // the edge the artwork had before it met the background.
        const int Feather = 236;
        for (int y = 0; y < height; y++)
        {
            int row = y * data.Stride;
            for (int x = 0; x < width; x++)
            {
                int offset = row + x * 4;
                int index = y * width + x;
                if (ground[index])
                {
                    pixels[offset] = 0; pixels[offset + 1] = 0; pixels[offset + 2] = 0; pixels[offset + 3] = 0;
                    continue;
                }
                int b = pixels[offset], g2 = pixels[offset + 1], r = pixels[offset + 2];
                int lightest = Math.Max(r, Math.Max(g2, b));
                int darkest = Math.Min(r, Math.Min(g2, b));
                if (darkest <= Feather || lightest < 250 || !TouchesGround(ground, width, height, x, y))
                {
                    pixels[offset + 3] = 255;
                    continue;
                }
                double coverage = (255.0 - darkest) / (255.0 - Feather);
                if (coverage <= 0.004) { pixels[offset] = 0; pixels[offset + 1] = 0; pixels[offset + 2] = 0; pixels[offset + 3] = 0; continue; }
                pixels[offset] = Unblend(b, coverage);
                pixels[offset + 1] = Unblend(g2, coverage);
                pixels[offset + 2] = Unblend(r, coverage);
                pixels[offset + 3] = (byte)Math.Round(coverage * 255.0);
            }
        }

        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        flat.UnlockBits(data);
        return flat;
    }

    static void Seed(byte[] pixels, int stride, bool[] ground, int[] queue, ref int tail, int x, int y, int width)
    {
        int index = y * width + x;
        if (ground[index]) { return; }
        int offset = y * stride + x * 4;
        if (pixels[offset] < 250 || pixels[offset + 1] < 250 || pixels[offset + 2] < 250) { return; }
        ground[index] = true;
        queue[tail++] = index;
    }

    static bool TouchesGround(bool[] ground, int width, int height, int x, int y)
    {
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= width || ny >= height) { continue; }
                if (ground[ny * width + nx]) { return true; }
            }
        }
        return false;
    }

    static byte Unblend(int channel, double coverage)
    {
        double value = (channel - 255.0 * (1.0 - coverage)) / coverage;
        return (byte)Math.Max(0, Math.Min(255, Math.Round(value)));
    }

    // The hand-drawn canvas has whatever margin the drawing happened to be centred in. Icons
    // need a margin that is the same fraction at every size, so the mark is measured and
    // re-centred in a square of its own rather than scaled inside the original frame.
    public static Rectangle Bounds(Bitmap image)
    {
        BitmapData data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        byte[] pixels = new byte[data.Stride * image.Height];
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
        image.UnlockBits(data);
        int left = image.Width, top = image.Height, right = -1, bottom = -1;
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                if (pixels[y * data.Stride + x * 4 + 3] < 8) { continue; }
                if (x < left) { left = x; }
                if (x > right) { right = x; }
                if (y < top) { top = y; }
                if (y > bottom) { bottom = y; }
            }
        }
        if (right < 0) { throw new InvalidOperationException("The brand mark is empty after cutting the background."); }
        return new Rectangle(left, top, right - left + 1, bottom - top + 1);
    }
}
'@ -ReferencedAssemblies System.Drawing.Common, System.Drawing.Primitives, System.Private.Windows.GdiPlus, System.Private.Windows.Core

$original = [Drawing.Bitmap]::new($source)
try {
    $cut = [DownlismMark]::Cut($original)
} finally {
    $original.Dispose()
}

$bounds = [DownlismMark]::Bounds($cut)

# 88% of the tile, which is where a Fluent mark sits: full-bleed and it crowds the icons either
# side of it in the taskbar, much smaller and it reads as a stamp on an empty square.
$square = [Math]::Max($bounds.Width, $bounds.Height)
$canvas = [int][Math]::Round($square / 0.88)
$master = [Drawing.Bitmap]::new($canvas, $canvas, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [Drawing.Graphics]::FromImage($master)
$graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
$graphics.DrawImage(
    $cut,
    [Drawing.Rectangle]::new([int](($canvas - $bounds.Width) / 2), [int](($canvas - $bounds.Height) / 2), $bounds.Width, $bounds.Height),
    $bounds,
    [Drawing.GraphicsUnit]::Pixel)
$graphics.Dispose()
$cut.Dispose()

function New-Mark([int]$Size) {
    $bitmap = [Drawing.Bitmap]::new($Size, $Size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bitmap)
    $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    # Without this the edge pixels are sampled against the transparent black outside the
    # bitmap and the whole mark gains a dark rim.
    $attributes = [Drawing.Imaging.ImageAttributes]::new()
    $attributes.SetWrapMode([Drawing.Drawing2D.WrapMode]::TileFlipXY)
    $g.DrawImage($master, [Drawing.Rectangle]::new(0, 0, $Size, $Size), 0, 0, $master.Width, $master.Height, [Drawing.GraphicsUnit]::Pixel, $attributes)
    $attributes.Dispose()
    $g.Dispose()
    return $bitmap
}

# Full-size art for the installer window, the in-app about panel, the extension and the website.
foreach ($target in @(
    @{ Path = (Join-Path $assets 'downlism-icon-fluent.png'); Size = 256 },
    @{ Path = (Join-Path $assets 'brand/downlism-mark-512.png'); Size = 512 },
    @{ Path = (Join-Path $projectRoot 'extension/icon128.png'); Size = 128 }
)) {
    $bitmap = New-Mark $target.Size
    $bitmap.Save([IO.Path]::GetFullPath($target.Path), [Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

# Multi-resolution .ico for the executables and the taskbar.
$frames = @()
foreach ($size in @(16, 24, 32, 48, 64, 128, 256)) {
    $bitmap = New-Mark $size
    $stream = [IO.MemoryStream]::new()
    $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
    $frames += @{ Size = $size; Bytes = $stream.ToArray() }
    $stream.Dispose()
    $bitmap.Dispose()
}
$master.Dispose()

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
