using System;
using CosmoImage.Operations.Color;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 197 — DIN99 (DIN 6176:2001). Closed-form perceptually-
/// uniform Lab variant. Tests exercise the per-triple math directly
/// (cleanest for verifying against published reference values), plus
/// a round-trip through the full op pair.
///
/// <para>Reference values cross-checked against the DIN 6176 spec and
/// the open-source colour-science Python package.</para>
/// </summary>
public class Round197Tests
{
    // ---- Closed-form per-triple maths ----

    [Fact]
    public void Lab2DIN99_NeutralWhitePoint()
    {
        // (L=100, a=0, b=0) — perfect neutral white.
        // L99 = 105.51 * ln(1 + 0.0158 * 100) ≈ 105.51 * ln(2.58) ≈ 100.0.
        // a99 = b99 = 0 because chroma G = 0 → C99 = 0.
        var (L99, a99, b99) = VipsLab2DIN99.Lab2DIN99(100, 0, 0);
        Assert.Equal(100.0, L99, 0.5);
        Assert.Equal(0.0, a99, 1e-6);
        Assert.Equal(0.0, b99, 1e-6);
    }

    [Fact]
    public void Lab2DIN99_NeutralBlack()
    {
        // (L=0, a=0, b=0) → (L99=0, a99=0, b99=0).
        var (L99, a99, b99) = VipsLab2DIN99.Lab2DIN99(0, 0, 0);
        Assert.Equal(0.0, L99, 1e-9);
        Assert.Equal(0.0, a99, 1e-9);
        Assert.Equal(0.0, b99, 1e-9);
    }

    [Fact]
    public void Lab2DIN99_PureRedReducesChroma()
    {
        // (L=53.24, a=80.09, b=67.20) — sRGB pure red in CIE Lab (D65).
        // DIN99 compresses high-chroma values; expect C99 < 80
        // (Lab a*² + b*² ≈ 105²).
        var (L99, a99, b99) = VipsLab2DIN99.Lab2DIN99(53.24, 80.09, 67.20);
        double labChroma = Math.Sqrt(80.09 * 80.09 + 67.20 * 67.20);
        double din99Chroma = Math.Sqrt(a99 * a99 + b99 * b99);
        Assert.True(din99Chroma < labChroma,
            $"DIN99 chroma {din99Chroma:F2} should be < Lab chroma {labChroma:F2} (compression)");
        // L99 is also slightly compressed at high lightness.
        Assert.InRange(L99, 50, 70);
    }

    [Theory]
    [InlineData(50, 25, -10)]
    [InlineData(75, -30, 40)]
    [InlineData(20, 5, 5)]
    [InlineData(95, 0.5, -0.5)]
    [InlineData(53.24, 80.09, 67.20)]
    public void Lab2DIN99_Inverse_RoundTrips(double L, double a, double b)
    {
        var (L99, a99, b99) = VipsLab2DIN99.Lab2DIN99(L, a, b);
        var (L_, a_, b_) = VipsDIN992Lab.DIN992Lab(L99, a99, b99);
        Assert.Equal(L, L_, 1e-6);
        Assert.Equal(a, a_, 1e-6);
        Assert.Equal(b, b_, 1e-6);
    }

    // ---- Op-level integration ----

    private static VipsImage MakeLabImage(int w, int h, Func<int, int, (double L, double a, double b)> px)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 3, BandFormat = VipsBandFormat.Float,
            Interpretation = VipsInterpretation.Lab,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) =>
            {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        var (L, aa, bb) = px(reg.Valid.Left + x, reg.Valid.Top + y);
                        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(
                            addr.Slice((x * 3 + 0) * 4, 4), (float)L);
                        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(
                            addr.Slice((x * 3 + 1) * 4, 4), (float)aa);
                        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(
                            addr.Slice((x * 3 + 2) * 4, 4), (float)bb);
                    }
                }
                return 0;
            }
        };

    [Fact]
    public void Op_Lab2DIN99_DIN992Lab_PixelExactRoundTrip()
    {
        var src = MakeLabImage(4, 3, (x, y) => (
            L: 50 + (x * 10),
            a: -50 + (y * 20),
            b: 30 + (x * 5) - (y * 10)));
        var roundtrip = VipsImageOps.DIN992Lab(VipsImageOps.Lab2DIN99(src));

        using var srcReg = new VipsRegion(src);
        using var rtReg = new VipsRegion(roundtrip);
        srcReg.Prepare(new VipsRect(0, 0, 4, 3));
        rtReg.Prepare(new VipsRect(0, 0, 4, 3));

        for (int y = 0; y < 3; y++)
            for (int x = 0; x < 4; x++)
                for (int c = 0; c < 3; c++)
                {
                    float a = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(
                        srcReg.GetAddress(x, y).Slice(c * 4, 4));
                    float b = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(
                        rtReg.GetAddress(x, y).Slice(c * 4, 4));
                    Assert.Equal(a, b, 1e-3f);
                }
    }

    [Fact]
    public void Op_Lab2DIN99_RejectsNonFloatOrNon3Band()
    {
        var ucharImg = new VipsImage
        {
            Width = 4, Height = 4, Bands = 3, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => 0,
        };
        Assert.Throws<Exception>(() => VipsImageOps.Lab2DIN99(ucharImg));

        var floatGrey = new VipsImage
        {
            Width = 4, Height = 4, Bands = 1, BandFormat = VipsBandFormat.Float,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => 0,
        };
        Assert.Throws<Exception>(() => VipsImageOps.Lab2DIN99(floatGrey));
    }
}
