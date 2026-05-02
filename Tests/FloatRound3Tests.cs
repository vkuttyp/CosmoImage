using System;
using System.Buffers.Binary;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round-3 Float coverage: Composite, Gamma, Vignette, Rank, Stats, Glow.
/// Plus a verification that Pixelate inherits Float through composition.
/// </summary>
public class FloatRound3Tests
{
    private static VipsImage FloatImage(int w, int h, int bands, Func<int, int, int, float> fill)
        => new VipsImage
        {
            Width = w, Height = h, Bands = bands, BandFormat = VipsBandFormat.Float,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        for (int bnd = 0; bnd < bands; bnd++)
                        {
                            int gx = reg.Valid.Left + x;
                            int gy = reg.Valid.Top + y;
                            BinaryPrimitives.WriteSingleLittleEndian(
                                addr.Slice((x * bands + bnd) * 4, 4),
                                fill(gx, gy, bnd));
                        }
                    }
                }
                return 0;
            }
        };

    private static float ReadFloat(VipsRegion reg, int x, int y, int bnd, int bands)
        => BinaryPrimitives.ReadSingleLittleEndian(reg.GetAddress(x, y).Slice(bnd * 4, 4));

    [Fact]
    public void Composite_Float_HalfAlphaMidpointBlend()
    {
        // Base: solid 0.2 in 3 bands (RGB).
        var baseImg = FloatImage(2, 2, 3, (x, y, b) => 0.2f);
        // Overlay: solid 0.8 with alpha 0.5 (RGBA).
        var overlay = FloatImage(2, 2, 4, (x, y, b) => b == 3 ? 0.5f : 0.8f);

        var result = baseImg.Composite(overlay, 0, 0);
        Assert.Equal(VipsBandFormat.Float, result.BandFormat);

        using var reg = new VipsRegion(result);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        // 0.2 * (1 - 0.5) + 0.8 * 0.5 = 0.5
        Assert.Equal(0.5f, ReadFloat(reg, 0, 0, 0, 3), 1e-5f);
        Assert.Equal(0.5f, ReadFloat(reg, 1, 1, 2, 3), 1e-5f);
    }

    [Fact]
    public void Composite_Float_NoOverlap_BasePassesThrough()
    {
        var baseImg = FloatImage(4, 4, 3, (x, y, b) => x + y * 0.1f);
        var overlay = FloatImage(2, 2, 4, (x, y, b) => 99f);
        // Place overlay way outside the base region.
        var result = baseImg.Composite(overlay, 100, 100);
        using var reg = new VipsRegion(result);
        reg.Prepare(new VipsRect(0, 0, 4, 4));
        Assert.Equal(2 + 1 * 0.1f, ReadFloat(reg, 2, 1, 0, 3), 1e-5f);
    }

    [Fact]
    public void Gamma_Float_AppliesPowDirectly()
    {
        // gamma 2.0 with input 0.25 → pow(0.25, 1/2) = 0.5
        var src = FloatImage(2, 2, 1, (x, y, b) => 0.25f);
        var corrected = src.Gamma(2.0);
        using var reg = new VipsRegion(corrected);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        Assert.Equal(0.5f, ReadFloat(reg, 0, 0, 0, 1), 1e-5f);
    }

    [Fact]
    public void Gamma_Float_PrecisionBeatsUCharLut()
    {
        // For a low UChar value (5), the LUT quantises heavily; Float computes
        // the actual pow result.
        const double srgb = 5.0 / 255.0;
        const double exp = 2.2;
        double analytic = Math.Pow(srgb, 1.0 / exp);

        var srcF = FloatImage(1, 1, 1, (x, y, b) => (float)srgb);
        var resF = srcF.Gamma(exp);
        using var rF = new VipsRegion(resF);
        rF.Prepare(new VipsRect(0, 0, 1, 1));
        double floatErr = Math.Abs(ReadFloat(rF, 0, 0, 0, 1) - analytic);

        var srcU = new VipsImage
        {
            Width = 1, Height = 1, Bands = 1, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                reg.GetAddress(0, 0)[0] = 5;
                return 0;
            }
        };
        var resU = srcU.Gamma(exp);
        using var rU = new VipsRegion(resU);
        rU.Prepare(new VipsRect(0, 0, 1, 1));
        double ucharErr = Math.Abs(rU.GetAddress(0, 0)[0] / 255.0 - analytic);

        Assert.True(floatErr < ucharErr,
            $"Float Gamma should beat UChar LUT in low range. floatErr={floatErr:E3}, ucharErr={ucharErr:E3}");
    }

    [Fact]
    public void Vignette_Float_CenterUnchanged_CornersDarken()
    {
        // Image center is at (W*0.5, H*0.5) = (4.5, 4.5) for a 9x9 image —
        // halfway between pixels — so even the central pixel sits at half-a-
        // pixel from the geometric center and gets a tiny bit of falloff.
        // The corner is the meaningful assertion.
        var src = FloatImage(9, 9, 1, (x, y, b) => 1.0f);
        var v = src.Vignette(0.6);
        Assert.Equal(VipsBandFormat.Float, v.BandFormat);
        using var reg = new VipsRegion(v);
        reg.Prepare(new VipsRect(0, 0, 9, 9));
        // Central pixel: nearly identity (within 1% of 1.0).
        Assert.InRange(ReadFloat(reg, 4, 4, 0, 1), 0.99f, 1.0f);
        // Corner: r2norm = 1 (clamped), factor = 1 - 0.6 = 0.4.
        Assert.Equal(0.4f, ReadFloat(reg, 0, 0, 0, 1), 0.05f);
    }

    [Fact]
    public void Rank_Float_MedianOfImpulse_RemovesSpike()
    {
        // 5x5 of 0.1 with one 100.0 spike at the center.
        var src = FloatImage(5, 5, 1, (x, y, b) => (x == 2 && y == 2) ? 100f : 0.1f);
        var med = src.Median(3);
        Assert.Equal(VipsBandFormat.Float, med.BandFormat);
        using var reg = new VipsRegion(med);
        reg.Prepare(new VipsRect(0, 0, 5, 5));
        // Median of nine values (eight 0.1s + one 100) is 0.1.
        Assert.Equal(0.1f, ReadFloat(reg, 2, 2, 0, 1), 1e-5f);
    }

    [Fact]
    public void Rank_Float_MaxRankPicksLargest()
    {
        var src = FloatImage(3, 3, 1, (x, y, b) => x + y * 0.1f);
        // 3x3 window, top rank = max. Sample pixel (1,1) → max of 9 = 2 + 0.2 = 2.2.
        var maxImg = src.Rank(3, 3, 8);
        using var reg = new VipsRegion(maxImg);
        reg.Prepare(new VipsRect(0, 0, 3, 3));
        Assert.Equal(2.2f, ReadFloat(reg, 1, 1, 0, 1), 1e-5f);
    }

    [Fact]
    public void Stats_Float_ReadsValuesAsFloat()
    {
        // Half the pixels at -1, half at +3. Expected mean = 1, deviate = 2.
        var src = FloatImage(4, 4, 1, (x, y, b) => x % 2 == 0 ? -1f : 3f);
        var stats = src.Stats();
        Assert.Equal(-1.0, stats.Min[0], 1e-6);
        Assert.Equal(3.0, stats.Max[0], 1e-6);
        Assert.Equal(1.0, stats.Avg[0], 1e-6);
        Assert.Equal(2.0, stats.Deviate[0], 1e-6);
    }

    [Fact]
    public void Glow_Float_AddsBlurredFraction_NoClamp()
    {
        // Uniform 1.0 input, sigma 1.0, strength 0.5.
        // Blur of uniform = uniform = 1.0. Output = 1 + 0.5 * 1 = 1.5.
        // No clamp, so value > 1.0 survives.
        var src = FloatImage(20, 20, 1, (x, y, b) => 1.0f);
        var glown = src.Glow(1.0, 0.5);
        Assert.Equal(VipsBandFormat.Float, glown.BandFormat);
        using var reg = new VipsRegion(glown);
        reg.Prepare(new VipsRect(8, 8, 4, 4));
        Assert.Equal(1.5f, ReadFloat(reg, 10, 10, 0, 1), 1e-3f);
    }

    [Fact]
    public void Pixelate_Float_InheritsThroughShrinkAndResize()
    {
        // Pixelate composes Shrink + nearest-Resize — both already Float.
        // No new code needed; this test just locks that in.
        var src = FloatImage(8, 8, 1, (x, y, b) => x * 0.1f + y * 0.01f);
        var px = src.Pixelate(2);
        Assert.Equal(VipsBandFormat.Float, px.BandFormat);
        using var reg = new VipsRegion(px);
        reg.Prepare(new VipsRect(0, 0, px.Width, px.Height));
        // First 2x2 block averages to (0+0.1+0.01+0.11)/4 = 0.055; nearest
        // resize back broadcasts that block. Just sanity-check it's finite.
        float v = ReadFloat(reg, 0, 0, 0, 1);
        Assert.True(float.IsFinite(v));
        Assert.InRange(v, 0.04f, 0.07f);
    }
}
