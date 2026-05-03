using System;
using CosmoImage.Operations.Color;
using CosmoImage.Operations.Convolution;
using Xunit;

namespace CosmoImage.Tests;

public class Round52Tests
{
    private static VipsImage RgbSolid(int w, int h, byte r, byte g, byte b)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 3, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? bb, ref bool stop) => {
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

    private static VipsImage VStep(int w, int h, int splitCol)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                        addr[x] = (byte)((reg.Valid.Left + x) >= splitCol ? 255 : 0);
                }
                return 0;
            }
        };

    // ---- Kodachrome ----

    [Fact]
    public void Kodachrome_AltersColorsButPreservesShape()
    {
        var src = RgbSolid(2, 2, 200, 100, 50);
        var k = VipsImageOps.Kodachrome(src);
        Assert.Equal(3, k.Bands);
        using var reg = new VipsRegion(k);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        var p = reg.GetAddress(0, 0);
        // Output differs from input on at least one band (matrix isn't identity).
        bool changed = p[0] != 200 || p[1] != 100 || p[2] != 50;
        Assert.True(changed);
    }

    [Fact]
    public void Kodachrome_PureBlack_StaysBlack()
    {
        var src = RgbSolid(2, 2, 0, 0, 0);
        var k = VipsImageOps.Kodachrome(src);
        using var reg = new VipsRegion(k);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        var p = reg.GetAddress(0, 0);
        Assert.Equal(0, p[0]); Assert.Equal(0, p[1]); Assert.Equal(0, p[2]);
    }

    // ---- Lomograph ----

    [Fact]
    public void Lomograph_BoostsSaturation()
    {
        // Lomograph is a per-channel scale that pushes mid-grey toward
        // saturated values (clamps).
        var src = RgbSolid(2, 2, 200, 100, 50);
        var l = VipsImageOps.Lomograph(src);
        using var reg = new VipsRegion(l);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        var p = reg.GetAddress(0, 0);
        // R channel was 200 * 1.5 = 300 → clamps to 255.
        Assert.Equal(255, p[0]);
        // G channel was 100 * 1.45 = 145.
        Assert.InRange(p[1], 144, 146);
        // B channel was 50 * 1.09 = 54.5.
        Assert.InRange(p[2], 54, 55);
    }

    // ---- ColorBlindness ----

    [Fact]
    public void ColorBlindness_Achromatopsia_GivesGray()
    {
        // Achromatopsia matrix is luminance broadcast: all bands → BT.601 Y.
        var src = RgbSolid(2, 2, 200, 100, 50);
        var cb = VipsImageOps.ColorBlindness(src, VipsColorBlindnessMode.Achromatopsia);
        using var reg = new VipsRegion(cb);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        var p = reg.GetAddress(0, 0);
        Assert.Equal(p[0], p[1]);
        Assert.Equal(p[1], p[2]);
        // Y = 0.299·200 + 0.587·100 + 0.114·50 ≈ 124.
        Assert.InRange(p[0], 122, 126);
    }

    [Fact]
    public void ColorBlindness_Protanopia_RedReducesToGreen()
    {
        // Pure red (255, 0, 0) under protanopia → matrix [0.567, 0.433, 0]
        // gives (145, 142, 0) — red and green confuse.
        var src = RgbSolid(2, 2, 255, 0, 0);
        var cb = VipsImageOps.ColorBlindness(src, VipsColorBlindnessMode.Protanopia);
        using var reg = new VipsRegion(cb);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        var p = reg.GetAddress(0, 0);
        // R ≈ 0.567 * 255 ≈ 145, G ≈ 0.558 * 255 ≈ 142, B = 0.
        Assert.InRange(p[0], 143, 147);
        Assert.InRange(p[1], 140, 144);
        Assert.Equal(0, p[2]);
    }

    [Fact]
    public void ColorBlindness_AllModes_DoNotThrow()
    {
        // Sanity-check every enum value runs.
        var src = RgbSolid(2, 2, 100, 150, 200);
        foreach (VipsColorBlindnessMode mode in Enum.GetValues<VipsColorBlindnessMode>())
        {
            var cb = VipsImageOps.ColorBlindness(src, mode);
            Assert.Equal(3, cb.Bands);
        }
    }

    // ---- Edge kernels ----

    [Fact]
    public void Roberts_StepEdge_RespondsAtTransition()
    {
        var src = VStep(10, 10, splitCol: 5);
        var ed = src.Edge(VipsEdgeMethod.Roberts);
        using var reg = new VipsRegion(ed);
        reg.Prepare(new VipsRect(0, 0, 10, 10));
        // Roberts cross hits at the seam (column 4 picks up the diagonal).
        Assert.Equal(255, reg.GetAddress(4, 4)[0]);
        // Far from seam: zero response.
        Assert.Equal(0, reg.GetAddress(0, 5)[0]);
        Assert.Equal(0, reg.GetAddress(9, 5)[0]);
    }

    [Fact]
    public void Prewitt_StepEdge_RespondsAtTransition()
    {
        var src = VStep(10, 10, splitCol: 5);
        var ed = src.Edge(VipsEdgeMethod.Prewitt);
        using var reg = new VipsRegion(ed);
        reg.Prepare(new VipsRect(0, 0, 10, 10));
        // Prewitt magnitude maxes at the seam.
        Assert.Equal(255, reg.GetAddress(4, 5)[0]);
        Assert.Equal(0, reg.GetAddress(0, 5)[0]);
    }

    [Fact]
    public void Laplacian_StepEdge_PicksUpRipple()
    {
        var src = VStep(10, 10, splitCol: 5);
        var ed = src.Edge(VipsEdgeMethod.Laplacian);
        using var reg = new VipsRegion(ed);
        reg.Prepare(new VipsRect(0, 0, 10, 10));
        // 5-point Laplacian has non-zero response at the seam.
        Assert.True(reg.GetAddress(4, 5)[0] > 0 || reg.GetAddress(5, 5)[0] > 0);
        // Flat region: zero.
        Assert.Equal(0, reg.GetAddress(0, 5)[0]);
    }
}
