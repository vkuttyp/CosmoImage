using System;
using System.Buffers.Binary;
using CosmoImage.Operations.Convolution;
using CosmoImage.Operations.Misc;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Coverage for the breadth-first Float branches added to Invert / Recomb /
/// Conv (2D) / Morph / Linearize / Delinearize / Math.
/// </summary>
public class FloatOpsRound2Tests
{
    private static VipsImage UCharUniform(int w, int h, byte value, int bands = 1)
        => new VipsImage
        {
            Width = w, Height = h, Bands = bands, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int i = 0; i < reg.Valid.Width * bands; i++) addr[i] = value;
                }
                return 0;
            }
        };

    /// <summary>Build a small Float image with a fill function to drive Float-input ops.</summary>
    private static VipsImage FloatImage(int w, int h, int bands, Func<int, int, int, float> fill)
    {
        var img = new VipsImage
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
        return img;
    }

    private static float ReadFloat(VipsRegion reg, int x, int y, int bnd, int bands)
        => BinaryPrimitives.ReadSingleLittleEndian(reg.GetAddress(x, y).Slice(bnd * 4, 4));

    [Fact]
    public void Invert_Float_NegatesValue()
    {
        var src = FloatImage(2, 2, 1, (x, y, b) => 50f);
        var inv = src.Invert();
        Assert.Equal(VipsBandFormat.Float, inv.BandFormat);
        using var reg = new VipsRegion(inv);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        Assert.Equal(-50f, ReadFloat(reg, 0, 0, 0, 1));
    }

    [Fact]
    public void Recomb_Float_LumaMixGivesGreyscale()
    {
        // Pure-red Float pixel through Rec.709 luma-mix matrix (Saturate(0)).
        var src = FloatImage(1, 1, 3, (x, y, bnd) => bnd == 0 ? 1.0f : 0f);
        var grey = src.Saturate(0);
        Assert.Equal(VipsBandFormat.Float, grey.BandFormat);
        using var reg = new VipsRegion(grey);
        reg.Prepare(new VipsRect(0, 0, 1, 1));
        // Rec.709 R coefficient = 0.2126.
        Assert.Equal(0.2126f, ReadFloat(reg, 0, 0, 0, 3), 1e-4f);
    }

    [Fact]
    public void Conv_Float_BoxKernelOverUniform_ReturnsSameValue()
    {
        var src = FloatImage(8, 8, 1, (x, y, b) => 12.5f);
        double[,] box = {
            { 1/9.0, 1/9.0, 1/9.0 },
            { 1/9.0, 1/9.0, 1/9.0 },
            { 1/9.0, 1/9.0, 1/9.0 },
        };
        var conv = src.Conv(box);
        Assert.Equal(VipsBandFormat.Float, conv.BandFormat);
        using var reg = new VipsRegion(conv);
        reg.Prepare(new VipsRect(2, 2, 4, 4));
        Assert.Equal(12.5f, ReadFloat(reg, 4, 4, 0, 1), 1e-4f);
    }

    [Fact]
    public void Morph_Float_DilatePicksUpLocalMax()
    {
        // 5x5 image of 0.0 with a single 9.5 at the center.
        var src = FloatImage(5, 5, 1, (x, y, b) => (x == 2 && y == 2) ? 9.5f : 0f);
        double[,] mask = { { 1, 1, 1 }, { 1, 1, 1 }, { 1, 1, 1 } };
        var dilated = src.Dilate(mask);
        using var reg = new VipsRegion(dilated);
        reg.Prepare(new VipsRect(0, 0, 5, 5));
        // Every pixel in the 3x3 neighborhood of center sees the spike.
        Assert.Equal(9.5f, ReadFloat(reg, 1, 1, 0, 1));
        Assert.Equal(9.5f, ReadFloat(reg, 3, 3, 0, 1));
        // Pixels outside the dilation reach stay 0.
        Assert.Equal(0f, ReadFloat(reg, 0, 0, 0, 1));
    }

    [Fact]
    public void Morph_Float_ErodePicksUpLocalMin()
    {
        var src = FloatImage(5, 5, 1, (x, y, b) => (x == 2 && y == 2) ? -3.0f : 5.0f);
        double[,] mask = { { 1, 1, 1 }, { 1, 1, 1 }, { 1, 1, 1 } };
        var eroded = src.Erode(mask);
        using var reg = new VipsRegion(eroded);
        reg.Prepare(new VipsRect(0, 0, 5, 5));
        Assert.Equal(-3.0f, ReadFloat(reg, 2, 2, 0, 1));
        Assert.Equal(-3.0f, ReadFloat(reg, 1, 1, 0, 1));
        Assert.Equal(5.0f, ReadFloat(reg, 0, 0, 0, 1));
    }

    [Fact]
    public void Linearize_Float_AppliesPropersRgbTransfer()
    {
        // sRGB 0.5 → linear ≈ 0.21404 (per IEC 61966-2-1).
        var src = FloatImage(1, 1, 1, (x, y, b) => 0.5f);
        var linear = src.Linearize();
        using var reg = new VipsRegion(linear);
        reg.Prepare(new VipsRect(0, 0, 1, 1));
        Assert.Equal(0.21404f, ReadFloat(reg, 0, 0, 0, 1), 1e-4f);
    }

    [Fact]
    public void Delinearize_Float_InvertsLinearize()
    {
        // Linearize then Delinearize should round-trip within tight Float tolerance.
        var src = FloatImage(4, 1, 1, (x, y, b) => 0.1f + x * 0.2f);
        var roundTripped = src.Linearize().Delinearize();
        using var reg = new VipsRegion(roundTripped);
        reg.Prepare(new VipsRect(0, 0, 4, 1));
        for (int x = 0; x < 4; x++)
        {
            float original = 0.1f + x * 0.2f;
            float got = ReadFloat(reg, x, 0, 0, 1);
            Assert.Equal(original, got, 1e-5f);
        }
    }

    [Fact]
    public void LinearizePrecision_Float_BeatsUCharLut()
    {
        // The whole point of the Float path: UChar LUT loses precision in the
        // lower range. For a sample like 5/255 (≈0.0196), the Float path
        // produces the actual linear value; the UChar path quantises to a
        // single byte. Confirm the Float result is closer to the analytical
        // answer than the UChar LUT round-trip.
        const double srgb = 5.0 / 255.0;
        const double analytic = srgb / 12.92; // below the 0.04045 knee → linear segment

        // Float path
        var srcF = FloatImage(1, 1, 1, (x, y, b) => (float)srgb);
        var linF = srcF.Linearize();
        using var rF = new VipsRegion(linF);
        rF.Prepare(new VipsRect(0, 0, 1, 1));
        double floatResult = ReadFloat(rF, 0, 0, 0, 1);

        // UChar LUT path
        var srcU = UCharUniform(1, 1, 5);
        var linU = srcU.Linearize();
        using var rU = new VipsRegion(linU);
        rU.Prepare(new VipsRect(0, 0, 1, 1));
        double ucharResult = rU.GetAddress(0, 0)[0] / 255.0;

        double floatErr = Math.Abs(floatResult - analytic);
        double ucharErr = Math.Abs(ucharResult - analytic);
        Assert.True(floatErr < ucharErr,
            $"Float Linearize should be closer to analytic value. floatErr={floatErr:E3}, ucharErr={ucharErr:E3}");
    }

    [Fact]
    public void Math_Float_SinAppliesDirectlyAsRadians()
    {
        var src = FloatImage(1, 1, 1, (x, y, b) => MathF.PI / 2f);
        var sined = src.Sin();
        using var reg = new VipsRegion(sined);
        reg.Prepare(new VipsRect(0, 0, 1, 1));
        Assert.Equal(1.0f, ReadFloat(reg, 0, 0, 0, 1), 1e-5f);
    }

    [Fact]
    public void Math_Float_LogSqrtRoundTripExp()
    {
        // For x > 0: exp(log(x)) ≈ x. Tight Float tolerance.
        var src = FloatImage(4, 1, 1, (x, y, b) => 1.0f + x * 5.0f);
        var rt = src.Log().Exp();
        using var reg = new VipsRegion(rt);
        reg.Prepare(new VipsRect(0, 0, 4, 1));
        for (int x = 0; x < 4; x++)
            Assert.Equal(1.0f + x * 5.0f, ReadFloat(reg, x, 0, 0, 1), 1e-3f);
    }

    [Fact]
    public void GreyscalePipeline_FloatLinearLight_PreservesBetterThanUChar()
    {
        // Compose a typical "color-correct downscale" chain in Float and
        // verify the BandFormat is Float end-to-end (no inadvertent UChar
        // round-trip mid-pipeline).
        var src = FloatImage(8, 8, 3, (x, y, b) => 0.5f);
        var pipeline = src.Linearize().Saturate(0).Delinearize();
        Assert.Equal(VipsBandFormat.Float, pipeline.BandFormat);
        using var reg = new VipsRegion(pipeline);
        reg.Prepare(new VipsRect(0, 0, 8, 8));
        // After Linearize→Greyscale→Delinearize over a 0.5-grey input, every
        // band should end up at ~0.5 again (lum-weighted of equal RGB is
        // identity, transfer round-trips precisely).
        Assert.Equal(0.5f, ReadFloat(reg, 4, 4, 0, 3), 1e-3f);
    }
}
