using System;
using System.Buffers.Binary;
using Xunit;

namespace CosmoImage.Tests;

public class Round30Tests
{
    private static VipsImage UCharImage(int w, int h, int bands, byte value)
        => new VipsImage
        {
            Width = w, Height = h, Bands = bands, BandFormat = VipsBandFormat.UChar,
            Interpretation = bands == 1 ? VipsInterpretation.BW : VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int i = 0; i < reg.Valid.Width * bands; i++) addr[i] = value;
                }
                return 0;
            }
        };

    /// <summary>1-band UChar gradient left-to-right; pixel (x, y) = floor(255 * x / (w-1)).</summary>
    private static VipsImage RampX(int w, int h)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        int gx = reg.Valid.Left + x;
                        addr[x] = (byte)Math.Clamp(gx * 255 / Math.Max(1, w - 1), 0, 255);
                    }
                }
                return 0;
            }
        };

    private static VipsImage FloatImage(int w, int h, Func<int, int, float> fn)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 1, BandFormat = VipsBandFormat.Float,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                        BinaryPrimitives.WriteSingleLittleEndian(addr.Slice(x * 4, 4),
                            fn(reg.Valid.Left + x, reg.Valid.Top + y));
                }
                return 0;
            }
        };

    // ---- HistMatch ----

    [Fact]
    public void HistMatch_RoundTripsAgainstSelf()
    {
        // Matching an image against itself should produce ~the same image.
        var src = RampX(64, 4);
        var matched = src.HistMatch(src);
        Assert.Equal(src.Width, matched.Width);
        using var ra = new VipsRegion(src);
        using var rb = new VipsRegion(matched);
        ra.Prepare(new VipsRect(0, 0, 64, 4));
        rb.Prepare(new VipsRect(0, 0, 64, 4));
        for (int x = 0; x < 64; x++)
        {
            // Allow ±1 from the LUT-search rounding at bin edges.
            int diff = Math.Abs(ra.GetAddress(x, 1)[0] - rb.GetAddress(x, 1)[0]);
            Assert.True(diff <= 1, $"position {x}: diff {diff}");
        }
    }

    [Fact]
    public void HistMatch_StretchesNarrowToWide()
    {
        // Source is concentrated in the low half [0..127];
        // reference spans full [0..255]. Output should stretch up.
        var narrow = new VipsImage
        {
            Width = 64, Height = 1, Bands = 1, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                var addr = reg.GetAddress(reg.Valid.Left, 0);
                for (int x = 0; x < reg.Valid.Width; x++)
                    addr[x] = (byte)((reg.Valid.Left + x) * 127 / 63);
                return 0;
            }
        };
        var wide = RampX(64, 1);
        var matched = narrow.HistMatch(wide);
        using var reg = new VipsRegion(matched);
        reg.Prepare(new VipsRect(0, 0, 64, 1));
        // Last sample in source was 127; should now be near 255.
        Assert.True(reg.GetAddress(63, 0)[0] > 240,
            $"expected last pel to stretch toward 255, got {reg.GetAddress(63, 0)[0]}");
    }

    // ---- HistEntropy ----

    [Fact]
    public void HistEntropy_UniformImage_IsZero()
    {
        var src = UCharImage(16, 16, 1, 100);
        var h = src.HistEntropy();
        Assert.Equal(2, h.Length); // [band0, aggregate]
        Assert.Equal(0.0, h[0], 6);
    }

    [Fact]
    public void HistEntropy_GradientHasNonzeroEntropy()
    {
        var src = RampX(256, 1);
        var h = src.HistEntropy();
        // 256 distinct values, equal counts → log2(256) = 8 bits.
        Assert.InRange(h[0], 7.9, 8.01);
    }

    // ---- Percent ----

    [Fact]
    public void Percent_GradientGives50Percent_AtMidpoint()
    {
        var src = RampX(256, 1);
        int mid = src.Percent(50.0);
        // 50% of a uniform 0..255 ramp → ~127.
        Assert.InRange(mid, 125, 130);
    }

    [Fact]
    public void Percent_AtBoundaries()
    {
        var src = RampX(256, 1);
        // 0% should fall at value 0, 100% at value 255.
        Assert.Equal(0, src.Percent(0.0));
        Assert.Equal(255, src.Percent(100.0));
    }

    // ---- Bandrank ----

    [Fact]
    public void Bandrank_DefaultsToMedian()
    {
        var a = UCharImage(2, 2, 1, 50);
        var b = UCharImage(2, 2, 1, 100);
        var c = UCharImage(2, 2, 1, 200);
        var med = VipsImageOps.Bandrank(new[] { a, b, c });
        using var reg = new VipsRegion(med);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        Assert.Equal(100, reg.GetAddress(0, 0)[0]);
    }

    [Fact]
    public void Bandrank_PicksMin_AtIndex0()
    {
        var a = UCharImage(2, 2, 1, 50);
        var b = UCharImage(2, 2, 1, 100);
        var c = UCharImage(2, 2, 1, 200);
        var min = VipsImageOps.Bandrank(new[] { a, b, c }, index: 0);
        using var reg = new VipsRegion(min);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        Assert.Equal(50, reg.GetAddress(0, 0)[0]);
    }

    [Fact]
    public void Bandrank_PicksMax_AtIndexN_1()
    {
        var a = UCharImage(2, 2, 1, 50);
        var b = UCharImage(2, 2, 1, 100);
        var c = UCharImage(2, 2, 1, 200);
        var max = VipsImageOps.Bandrank(new[] { a, b, c }, index: 2);
        using var reg = new VipsRegion(max);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        Assert.Equal(200, reg.GetAddress(0, 0)[0]);
    }

    // ---- Byteswap ----

    [Fact]
    public void Byteswap_UCharIsPassThrough()
    {
        var src = UCharImage(4, 4, 3, 100);
        var same = src.Byteswap();
        Assert.Same(src, same);
    }

    [Fact]
    public void Byteswap_FloatReversesEndian()
    {
        // 1.0f little-endian = [0x00, 0x00, 0x80, 0x3F]
        var src = FloatImage(2, 1, (x, y) => 1.0f);
        var swapped = src.Byteswap();
        Assert.Equal(VipsBandFormat.Float, swapped.BandFormat);
        using var reg = new VipsRegion(swapped);
        reg.Prepare(new VipsRect(0, 0, 2, 1));
        var p = reg.GetAddress(0, 0);
        // After swap: [0x3F, 0x80, 0x00, 0x00]
        Assert.Equal(0x3F, p[0]);
        Assert.Equal(0x80, p[1]);
        Assert.Equal(0x00, p[2]);
        Assert.Equal(0x00, p[3]);
    }

    // ---- Grid ----

    [Fact]
    public void Grid_LaysOutTilesIn2D()
    {
        // Build 6 stacked 2×2 tiles by varying value with y/2.
        var stacked = new VipsImage
        {
            Width = 2, Height = 12, Bands = 1, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    int gy = reg.Valid.Top + y;
                    int tile = gy / 2;
                    for (int x = 0; x < reg.Valid.Width; x++) addr[x] = (byte)tile;
                }
                return 0;
            }
        };
        var grid = stacked.Grid(tileHeight: 2, across: 3, down: 2);
        Assert.Equal(6, grid.Width);
        Assert.Equal(4, grid.Height);

        using var reg = new VipsRegion(grid);
        reg.Prepare(new VipsRect(0, 0, 6, 4));
        // Tile k=0 at (0,0)..(1,1). Tile k=1 at (2,0)..(3,1). Tile k=2 at (4,0)..(5,1).
        // Tile k=3 at (0,2)..(1,3). Tile k=4 at (2,2)..(3,3). Tile k=5 at (4,2)..(5,3).
        Assert.Equal(0, reg.GetAddress(0, 0)[0]);
        Assert.Equal(1, reg.GetAddress(2, 0)[0]);
        Assert.Equal(2, reg.GetAddress(4, 0)[0]);
        Assert.Equal(3, reg.GetAddress(0, 2)[0]);
        Assert.Equal(4, reg.GetAddress(2, 2)[0]);
        Assert.Equal(5, reg.GetAddress(4, 2)[0]);
    }

    [Fact]
    public void Grid_ZeroFillsOverflowCells()
    {
        // 2 tiles into a 2×2 grid → trailing 2 cells zero-filled.
        var stacked = new VipsImage
        {
            Width = 2, Height = 4, Bands = 1, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++) addr[x] = 100;
                }
                return 0;
            }
        };
        var grid = stacked.Grid(tileHeight: 2, across: 2, down: 2);
        using var reg = new VipsRegion(grid);
        reg.Prepare(new VipsRect(0, 0, 4, 4));
        Assert.Equal(100, reg.GetAddress(0, 0)[0]); // tile 0
        Assert.Equal(100, reg.GetAddress(2, 0)[0]); // tile 1
        Assert.Equal(0, reg.GetAddress(0, 2)[0]);   // tile 2 absent → zero
        Assert.Equal(0, reg.GetAddress(2, 2)[0]);   // tile 3 absent → zero
    }
}
