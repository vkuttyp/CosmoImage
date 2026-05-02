using System;
using CosmoImage.Operations.Misc;
using Xunit;

namespace CosmoImage.Tests;

public class Round28Tests
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

    /// <summary>Per-band-constant filler — band b gets value bytes[b].</summary>
    private static VipsImage UCharBands(int w, int h, byte[] bands)
        => new VipsImage
        {
            Width = w, Height = h, Bands = bands.Length, BandFormat = VipsBandFormat.UChar,
            Interpretation = bands.Length switch { 1 or 2 => VipsInterpretation.BW, _ => VipsInterpretation.RGB },
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                        for (int bnd = 0; bnd < bands.Length; bnd++)
                            addr[x * bands.Length + bnd] = bands[bnd];
                }
                return 0;
            }
        };

    /// <summary>Single-band gradient left-to-right.</summary>
    private static VipsImage GradientGray(int w, int h)
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

    // ---- ExtractBand ----

    [Fact]
    public void ExtractBand_PullsSingleBand()
    {
        var src = UCharBands(2, 2, new byte[] { 10, 20, 30 });
        var g = src.ExtractBand(1);
        Assert.Equal(1, g.Bands);
        using var reg = new VipsRegion(g);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        Assert.Equal(20, reg.GetAddress(0, 0)[0]);
        Assert.Equal(20, reg.GetAddress(1, 1)[0]);
    }

    [Fact]
    public void ExtractBand_PullsMultipleBands()
    {
        var src = UCharBands(2, 2, new byte[] { 10, 20, 30, 40 });
        var gb = src.ExtractBand(1, 2);
        Assert.Equal(2, gb.Bands);
        using var reg = new VipsRegion(gb);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        var p = reg.GetAddress(0, 0);
        Assert.Equal(20, p[0]);
        Assert.Equal(30, p[1]);
    }

    [Fact]
    public void ExtractBand_RejectsOutOfRange()
    {
        var src = UCharBands(2, 2, new byte[] { 10, 20, 30 });
        Assert.Throws<Exception>(() => src.ExtractBand(2, 2));
    }

    // ---- Bandbool ----

    [Fact]
    public void Bandbool_Or_FoldsAcrossBands()
    {
        var src = UCharBands(2, 2, new byte[] { 0b0001, 0b0010, 0b0100 });
        var or = src.Bandbool(VipsBooleanOperation.Or);
        Assert.Equal(1, or.Bands);
        using var reg = new VipsRegion(or);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        Assert.Equal(0b0111, reg.GetAddress(0, 0)[0]);
    }

    [Fact]
    public void Bandbool_And_FoldsAcrossBands()
    {
        var src = UCharBands(2, 2, new byte[] { 0b1011, 0b1110, 0b1010 });
        var and = src.Bandbool(VipsBooleanOperation.And);
        using var reg = new VipsRegion(and);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        Assert.Equal(0b1010, reg.GetAddress(0, 0)[0]);
    }

    [Fact]
    public void Bandbool_SingleBand_PassesThrough()
    {
        var src = UCharImage(2, 2, 1, 100);
        var same = src.Bandbool(VipsBooleanOperation.Or);
        Assert.Same(src, same);
    }

    [Fact]
    public void Bandbool_RejectsFloat()
    {
        var src = new VipsImage
        {
            Width = 4, Height = 4, Bands = 3, BandFormat = VipsBandFormat.Float,
        };
        Assert.Throws<Exception>(() => src.Bandbool(VipsBooleanOperation.Or));
    }

    // ---- Bandmean ----

    [Fact]
    public void Bandmean_AveragesUCharBands()
    {
        // (10 + 20 + 30) / 3 = 20.
        var src = UCharBands(2, 2, new byte[] { 10, 20, 30 });
        var m = src.Bandmean();
        Assert.Equal(1, m.Bands);
        using var reg = new VipsRegion(m);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        Assert.Equal(20, reg.GetAddress(0, 0)[0]);
    }

    [Fact]
    public void Bandmean_RoundsCorrectly()
    {
        // (10 + 11) / 2 with rounding = (21 + 1) / 2 = 11.
        var src = UCharBands(2, 2, new byte[] { 10, 11 });
        var m = src.Bandmean();
        using var reg = new VipsRegion(m);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        Assert.Equal(11, reg.GetAddress(0, 0)[0]);
    }

    // ---- Ifthenelse ----

    [Fact]
    public void Ifthenelse_PicksFromThenWhenConditionNonzero()
    {
        var cond = UCharImage(2, 2, 1, 1);
        var thn = UCharImage(2, 2, 3, 100);
        var els = UCharImage(2, 2, 3, 200);
        var pick = cond.Ifthenelse(thn, els);
        using var reg = new VipsRegion(pick);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        var p = reg.GetAddress(0, 0);
        Assert.Equal(100, p[0]);
        Assert.Equal(100, p[1]);
        Assert.Equal(100, p[2]);
    }

    [Fact]
    public void Ifthenelse_PicksFromElseWhenConditionZero()
    {
        var cond = UCharImage(2, 2, 1, 0);
        var thn = UCharImage(2, 2, 3, 100);
        var els = UCharImage(2, 2, 3, 200);
        var pick = cond.Ifthenelse(thn, els);
        using var reg = new VipsRegion(pick);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        Assert.Equal(200, reg.GetAddress(0, 0)[0]);
    }

    [Fact]
    public void Ifthenelse_PerBandMask_SelectsBandwise()
    {
        // Condition has 3 bands: (1, 0, 1). Output band 0 from then,
        // band 1 from else, band 2 from then.
        var cond = UCharBands(2, 2, new byte[] { 1, 0, 1 });
        var thn = UCharImage(2, 2, 3, 100);
        var els = UCharImage(2, 2, 3, 200);
        var pick = cond.Ifthenelse(thn, els);
        using var reg = new VipsRegion(pick);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        var p = reg.GetAddress(0, 0);
        Assert.Equal(100, p[0]);
        Assert.Equal(200, p[1]);
        Assert.Equal(100, p[2]);
    }

    // ---- Replicate ----

    [Fact]
    public void Replicate_TilesAcrossAndDown()
    {
        var src = UCharImage(2, 2, 1, 50);
        var tiled = src.Replicate(across: 3, down: 2);
        Assert.Equal(6, tiled.Width);
        Assert.Equal(4, tiled.Height);
        using var reg = new VipsRegion(tiled);
        reg.Prepare(new VipsRect(0, 0, 6, 4));
        Assert.Equal(50, reg.GetAddress(0, 0)[0]);
        Assert.Equal(50, reg.GetAddress(5, 3)[0]);
    }

    [Fact]
    public void Replicate_PreservesGradientPattern()
    {
        // 4-pixel ramp tiled 3 times → values repeat (0, 85, 170, 255, 0, 85, …).
        var src = GradientGray(4, 1);
        var tiled = src.Replicate(across: 3, down: 1);
        Assert.Equal(12, tiled.Width);
        using var reg = new VipsRegion(tiled);
        reg.Prepare(new VipsRect(0, 0, 12, 1));
        // First and second tile must agree at the same intra-tile offset.
        Assert.Equal(reg.GetAddress(0, 0)[0], reg.GetAddress(4, 0)[0]);
        Assert.Equal(reg.GetAddress(1, 0)[0], reg.GetAddress(5, 0)[0]);
        Assert.Equal(reg.GetAddress(3, 0)[0], reg.GetAddress(11, 0)[0]);
    }

    [Fact]
    public void Replicate_CrossesPartialTilesCorrectly()
    {
        // Read a region that straddles a tile boundary. 4-wide tile,
        // request offset (3..5) → spans the seam.
        var src = GradientGray(4, 1);
        var tiled = src.Replicate(across: 3, down: 1);
        using var reg = new VipsRegion(tiled);
        // Request a region that straddles two tile seams (3..7 covers
        // end of tile 0, all of tile 1, start of tile 2).
        reg.Prepare(new VipsRect(0, 0, 12, 1));
        // x=3 is last pixel of tile 0; x=4 is first pixel of tile 1.
        Assert.Equal(reg.GetAddress(3, 0)[0], reg.GetAddress(7, 0)[0]); // both end-of-tile
        Assert.Equal(reg.GetAddress(4, 0)[0], reg.GetAddress(0, 0)[0]); // both start-of-tile
    }

    // ---- Falsecolor ----

    [Fact]
    public void Falsecolor_GrayscaleBecomesRgb()
    {
        var src = UCharImage(4, 4, 1, 128);
        var fc = src.Falsecolor();
        Assert.Equal(3, fc.Bands);
        Assert.Equal(VipsInterpretation.RGB, fc.Interpretation);
    }

    [Fact]
    public void Falsecolor_ExtremesGiveExpectedColours()
    {
        // Jet endpoints: 0 → low blue, 255 → low red.
        var black = UCharImage(2, 2, 1, 0);
        var white = UCharImage(2, 2, 1, 255);
        var fcBlack = black.Falsecolor();
        var fcWhite = white.Falsecolor();
        using var rb = new VipsRegion(fcBlack); rb.Prepare(new VipsRect(0, 0, 2, 2));
        using var rw = new VipsRegion(fcWhite); rw.Prepare(new VipsRect(0, 0, 2, 2));
        // Low end: blue dominates, red is zero.
        Assert.Equal(0, rb.GetAddress(0, 0)[0]);
        Assert.True(rb.GetAddress(0, 0)[2] > 100);
        // High end: red dominates, blue is zero.
        Assert.True(rw.GetAddress(0, 0)[0] > 100);
        Assert.Equal(0, rw.GetAddress(0, 0)[2]);
    }

    [Fact]
    public void Falsecolor_RejectsRgbInput()
    {
        var src = UCharImage(2, 2, 3, 100);
        Assert.Throws<Exception>(() => src.Falsecolor());
    }
}
