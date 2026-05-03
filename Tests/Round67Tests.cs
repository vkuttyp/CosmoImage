using System;
using CosmoImage.Operations.Drawing;
using Xunit;

namespace CosmoImage.Tests;

public class Round67Tests
{
    private static VipsImage RgbSolid(int w, int h, byte r, byte g, byte b)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 3, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? aa, object? bb, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        addr[x * 3 + 0] = r;
                        addr[x * 3 + 1] = g;
                        addr[x * 3 + 2] = b;
                    }
                }
                return 0;
            }
        };

    /// <summary>Build a 3-band UChar image where each pel = (x*16, y*16, 50).</summary>
    private static VipsImage Gradient3(int w, int h)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 3, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? aa, object? bb, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    int gy = reg.Valid.Top + y;
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        int gx = reg.Valid.Left + x;
                        addr[x * 3 + 0] = (byte)Math.Min(255, gx * 16);
                        addr[x * 3 + 1] = (byte)Math.Min(255, gy * 16);
                        addr[x * 3 + 2] = 50;
                    }
                }
                return 0;
            }
        };

    private static byte[] ReadPel(VipsImage img, int x, int y)
    {
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, img.Width, img.Height));
        return reg.GetAddress(x, y).Slice(0, img.Bands).ToArray();
    }

    // ---- ImageBrush ----

    [Fact]
    public void ImageBrush_PaintsSourceColors()
    {
        var bg = RgbSolid(20, 20, 0, 0, 0);
        var src = Gradient3(8, 8);
        var brush = new VipsImageBrush(src);
        // Fill a rect that fits entirely inside the source.
        var painted = VipsImageOps.Fill(bg, brush, x: 0, y: 0, w: 8, h: 8, aa: false);
        // Pixel at (3, 5) should match source's (3, 5).
        var p = ReadPel(painted, 3, 5);
        Assert.Equal(48, p[0]);   // 3 * 16
        Assert.Equal(80, p[1]);   // 5 * 16
        Assert.Equal(50, p[2]);
    }

    [Fact]
    public void ImageBrush_Clamp_ExtendsEdgePixelsBeyondSource()
    {
        var bg = RgbSolid(20, 8, 0, 0, 0);
        var src = Gradient3(4, 4);
        var brush = new VipsImageBrush(src, tiling: VipsBrushTiling.Clamp);
        var painted = VipsImageOps.Fill(bg, brush, 0, 0, 16, 4, aa: false);
        // Outside source (x ≥ 4): should clamp to source x=3 → R = 48.
        var clamped = ReadPel(painted, 10, 2);
        Assert.Equal(48, clamped[0]);
    }

    [Fact]
    public void ImageBrush_Repeat_TilesSource()
    {
        var bg = RgbSolid(20, 8, 0, 0, 0);
        var src = Gradient3(4, 4);
        var brush = new VipsImageBrush(src, tiling: VipsBrushTiling.Repeat);
        var painted = VipsImageOps.Fill(bg, brush, 0, 0, 16, 4, aa: false);
        // x=4 wraps to source x=0 → R = 0.
        var wrap = ReadPel(painted, 4, 0);
        Assert.Equal(0, wrap[0]);
        // x=5 wraps to source x=1 → R = 16.
        var wrap2 = ReadPel(painted, 5, 0);
        Assert.Equal(16, wrap2[0]);
    }

    [Fact]
    public void ImageBrush_Mirror_ReflectsAtEdges()
    {
        var bg = RgbSolid(20, 8, 0, 0, 0);
        var src = Gradient3(4, 4);
        var brush = new VipsImageBrush(src, tiling: VipsBrushTiling.Mirror);
        var painted = VipsImageOps.Fill(bg, brush, 0, 0, 16, 4, aa: false);
        // x=4 in mirror cycle = 8: m=4, m >= 4, so source x = 7 - 4 = 3 → R = 48.
        var mirrored = ReadPel(painted, 4, 0);
        Assert.Equal(48, mirrored[0]);
        // x=7: m=7, source x = 7 - 7 = 0 → R = 0.
        var endMirror = ReadPel(painted, 7, 0);
        Assert.Equal(0, endMirror[0]);
    }

    [Fact]
    public void ImageBrush_OffsetShiftsSourceOrigin()
    {
        var bg = RgbSolid(20, 8, 0, 0, 0);
        var src = Gradient3(4, 4);
        // Offset = (10, 0): destination pixel (10, 0) reads source (0, 0).
        var brush = new VipsImageBrush(src, offsetX: 10, offsetY: 0,
            tiling: VipsBrushTiling.Clamp);
        var painted = VipsImageOps.Fill(bg, brush, 10, 0, 4, 4, aa: false);
        // Destination (10, 0) → source (0, 0) → R = 0.
        Assert.Equal(0, ReadPel(painted, 10, 0)[0]);
        // Destination (13, 0) → source (3, 0) → R = 48.
        Assert.Equal(48, ReadPel(painted, 13, 0)[0]);
    }

    // ---- PatternBrush ----

    [Fact]
    public void PatternBrush_RepeatsTile()
    {
        // 2×2 tile with distinct corners.
        var tile = new VipsImage
        {
            Width = 2, Height = 2, Bands = 3, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                var a0 = reg.GetAddress(0, 0);
                a0[0] = 100; a0[1] = 0; a0[2] = 0;
                a0[3] = 0; a0[4] = 100; a0[5] = 0;
                var a1 = reg.GetAddress(0, 1);
                a1[0] = 0; a1[1] = 0; a1[2] = 100;
                a1[3] = 100; a1[4] = 100; a1[5] = 100;
                return 0;
            }
        };
        var bg = RgbSolid(8, 8, 0, 0, 0);
        var brush = new VipsPatternBrush(tile);
        var painted = VipsImageOps.Fill(bg, brush, 0, 0, 8, 8, aa: false);

        // (0, 0) ≡ tile (0, 0) ≡ (100, 0, 0).
        Assert.Equal(100, ReadPel(painted, 0, 0)[0]);
        // (2, 0) wraps to tile (0, 0).
        Assert.Equal(100, ReadPel(painted, 2, 0)[0]);
        // (1, 1) ≡ tile (1, 1) ≡ (100, 100, 100).
        Assert.Equal(100, ReadPel(painted, 1, 1)[0]);
        Assert.Equal(100, ReadPel(painted, 1, 1)[1]);
        Assert.Equal(100, ReadPel(painted, 1, 1)[2]);
        // (3, 3) wraps to tile (1, 1).
        Assert.Equal(100, ReadPel(painted, 3, 3)[2]);
    }

    [Fact]
    public void ImageBrush_RejectsNonUCharSource()
    {
        var floatSrc = new VipsImage
        {
            Width = 2, Height = 2, Bands = 1, BandFormat = VipsBandFormat.Float,
        };
        Assert.Throws<ArgumentException>(() => new VipsImageBrush(floatSrc));
    }
}
