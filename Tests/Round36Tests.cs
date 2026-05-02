using System;
using System.Buffers.Binary;
using Xunit;

namespace CosmoImage.Tests;

public class Round36Tests
{
    private static VipsImage Float3(double v0, double v1, double v2,
        VipsInterpretation interp = VipsInterpretation.XYZ)
        => new VipsImage
        {
            Width = 1, Height = 1, Bands = 3, BandFormat = VipsBandFormat.Float,
            Interpretation = interp,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                var addr = reg.GetAddress(0, 0);
                BinaryPrimitives.WriteSingleLittleEndian(addr.Slice(0, 4), (float)v0);
                BinaryPrimitives.WriteSingleLittleEndian(addr.Slice(4, 4), (float)v1);
                BinaryPrimitives.WriteSingleLittleEndian(addr.Slice(8, 4), (float)v2);
                return 0;
            }
        };

    private static VipsImage UChar4(byte b0, byte b1, byte b2, byte b3)
        => new VipsImage
        {
            Width = 1, Height = 1, Bands = 4, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.CMYK,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                var addr = reg.GetAddress(0, 0);
                addr[0] = b0; addr[1] = b1; addr[2] = b2; addr[3] = b3;
                return 0;
            }
        };

    private static (float, float, float) ReadFloat3(VipsImage img)
    {
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, 1, 1));
        var addr = reg.GetAddress(0, 0);
        return (
            BinaryPrimitives.ReadSingleLittleEndian(addr.Slice(0, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(addr.Slice(4, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(addr.Slice(8, 4))
        );
    }

    private static (byte, byte, byte, byte) ReadByte4(VipsImage img)
    {
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, 1, 1));
        var addr = reg.GetAddress(0, 0);
        return (addr[0], addr[1], addr[2], addr[3]);
    }

    private static float ReadFloat1(VipsImage img)
    {
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, 1, 1));
        return BinaryPrimitives.ReadSingleLittleEndian(reg.GetAddress(0, 0).Slice(0, 4));
    }

    // ---- scRGB ↔ XYZ ----

    [Fact]
    public void ScRGB2XYZ_PureWhite_GivesD65()
    {
        // (1, 1, 1) in sRGB primaries → D65 (Y = 1).
        var rgb = Float3(1, 1, 1, VipsInterpretation.scRGB);
        var (X, Y, Z) = ReadFloat3(rgb.ScRGB2XYZ());
        Assert.Equal(0.95047f, X, 3);
        Assert.Equal(1.0f, Y, 3);
        Assert.Equal(1.08883f, Z, 3);
    }

    [Fact]
    public void ScRGB_RoundTripsThroughXYZ()
    {
        var rgb = Float3(0.6, 0.3, 0.8, VipsInterpretation.scRGB);
        var back = rgb.ScRGB2XYZ().XYZ2scRGB();
        var (R, G, B) = ReadFloat3(back);
        Assert.Equal(0.6f, R, 4);
        Assert.Equal(0.3f, G, 4);
        Assert.Equal(0.8f, B, 4);
    }

    [Fact]
    public void XYZ2scRGB_AcceptsOutOfGamut()
    {
        // Negative scRGB is the whole point — values < 0 sit outside sRGB gamut.
        var xyz = Float3(0.05, 0.5, 0.05);
        var (R, G, B) = ReadFloat3(xyz.XYZ2scRGB());
        // Pure-green XYZ → R and B should be small or negative.
        Assert.True(G > R && G > B);
    }

    // ---- CMYK ↔ XYZ ----

    [Fact]
    public void CMYK2XYZ_PureBlack_IsZero()
    {
        // K = 255 → all RGB = 0 → XYZ = 0.
        var cmyk = UChar4(0, 0, 0, 255);
        var (X, Y, Z) = ReadFloat3(cmyk.CMYK2XYZ());
        Assert.Equal(0, X, 4);
        Assert.Equal(0, Y, 4);
        Assert.Equal(0, Z, 4);
    }

    [Fact]
    public void CMYK2XYZ_PureWhite_GivesD65()
    {
        // C = M = Y = K = 0 → R = G = B = 1 → D65.
        var cmyk = UChar4(0, 0, 0, 0);
        var (X, Y, Z) = ReadFloat3(cmyk.CMYK2XYZ());
        Assert.Equal(0.95047f, X, 3);
        Assert.Equal(1.0f, Y, 3);
        Assert.Equal(1.08883f, Z, 3);
    }

    [Fact]
    public void CMYK_RoundTripsForPrimaries()
    {
        // Pure cyan (C = 255, M = Y = K = 0) → R = 0, G = 1, B = 1 → cyan-ish XYZ.
        // Round trip should return to (255, 0, 0, 0) within ± 1.
        var cmyk = UChar4(255, 0, 0, 0);
        var back = cmyk.CMYK2XYZ().XYZ2CMYK();
        var (C, M, Y, K) = ReadByte4(back);
        Assert.InRange(C, 254, 255);
        Assert.InRange(M, 0, 1);
        Assert.InRange(Y, 0, 1);
        Assert.InRange(K, 0, 1);
    }

    [Fact]
    public void XYZ2CMYK_PureGray_NoChroma()
    {
        // Mid-grey XYZ (D65 scaled) → CMYK should pick up only K.
        var xyz = Float3(0.95047 * 0.5, 0.5, 1.08883 * 0.5);
        var (C, M, Y, K) = ReadByte4(xyz.XYZ2CMYK());
        // C, M, Y should be tiny (rounding noise); K should carry the value.
        Assert.InRange(C, 0, 1);
        Assert.InRange(M, 0, 1);
        Assert.InRange(Y, 0, 1);
        Assert.True(K > 100, $"expected substantial K, got {K}");
    }

    // ---- dECMC ----

    [Fact]
    public void DECMC_SameLab_IsZero()
    {
        var lab = Float3(50, 10, -10, VipsInterpretation.Lab);
        var d = lab.DECMC(lab);
        Assert.Equal(0f, ReadFloat1(d), 4);
    }

    [Fact]
    public void DECMC_PureLightness_UsesLWeight()
    {
        // Lab (50, 0, 0) vs (60, 0, 0): chroma = 0, only dL contributes.
        // SL = 0.040975 * 50 / (1 + 0.01765 * 50) ≈ 1.0884
        // dE_CMC = |dL| / (l * SL); l = 2 → ≈ 10 / (2 * 1.0884) ≈ 4.594
        double dE = VipsImageOps.DECMC(50, 0, 0, 60, 0, 0);
        Assert.Equal(4.594, dE, 2);
    }

    [Fact]
    public void DECMC_AsymmetricInReference()
    {
        // Swapping arguments changes the result (reference-weighted).
        double a = VipsImageOps.DECMC(50, 30, 20, 55, 35, 25);
        double b = VipsImageOps.DECMC(55, 35, 25, 50, 30, 20);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void DECMC_PerceptibilityWeightsDifferFromAcceptability()
    {
        // l = 1 (perceptibility) emphasises lightness more than l = 2 (acceptability).
        double accept = VipsImageOps.DECMC(50, 30, 20, 60, 30, 20, l: 2, c: 1);
        double percept = VipsImageOps.DECMC(50, 30, 20, 60, 30, 20, l: 1, c: 1);
        Assert.True(percept > accept,
            $"perceptibility ({percept}) should exceed acceptability ({accept})");
    }
}
