using System;
using CosmoImage.Operations.Color;
using Xunit;

namespace CosmoImage.Tests;

public class Round82Tests
{
    private const double Tol = 1e-3;

    // ---- HunterLab ----

    [Fact]
    public void HunterLab_Black_LIsZero()
    {
        var lab = VipsColorConvert.XyzToHunterLab(new VipsColorXyz(0, 0, 0));
        Assert.Equal(0, lab.L, Tol);
    }

    [Fact]
    public void HunterLab_White_LIs100()
    {
        // White at D65 (Y=1) → L = 100·sqrt(1) = 100; a, b ≈ 0.
        var lab = VipsColorConvert.XyzToHunterLab(VipsColorConvert.WhitePointXyz(VipsWhitePoint.D65));
        Assert.Equal(100, lab.L, 1e-6);
        Assert.True(Math.Abs(lab.A) < 0.5, $"a should be ≈ 0, got {lab.A:F3}");
        Assert.True(Math.Abs(lab.B) < 0.5, $"b should be ≈ 0, got {lab.B:F3}");
    }

    [Fact]
    public void HunterLab_RoundTrip()
    {
        var xyz = new VipsColorXyz(0.4, 0.5, 0.6);
        var back = VipsColorConvert.HunterLabToXyz(VipsColorConvert.XyzToHunterLab(xyz));
        Assert.Equal(xyz.X, back.X, 1e-9);
        Assert.Equal(xyz.Y, back.Y, 1e-9);
        Assert.Equal(xyz.Z, back.Z, 1e-9);
    }

    [Fact]
    public void HunterLab_ConvertGenericRoundTrip()
    {
        var rgb = new VipsColorRgb(0.5, 0.3, 0.8);
        var hLab = VipsColorConvert.Convert<VipsColorRgb, VipsColorHunterLab>(rgb);
        var back = VipsColorConvert.Convert<VipsColorHunterLab, VipsColorRgb>(hLab);
        Assert.Equal(rgb.R, back.R, Tol);
        Assert.Equal(rgb.G, back.G, Tol);
        Assert.Equal(rgb.B, back.B, Tol);
    }

    // ---- LMS variants ----

    [Theory]
    [InlineData(VipsLmsAdaptation.Bradford)]
    [InlineData(VipsLmsAdaptation.Cat02)]
    [InlineData(VipsLmsAdaptation.Cat97s)]
    public void Lms_RoundTrip_AllMatrices(VipsLmsAdaptation method)
    {
        var xyz = new VipsColorXyz(0.4, 0.55, 0.7);
        var lms = VipsColorConvert.XyzToLms(xyz, method);
        var back = VipsColorConvert.LmsToXyz(lms, method);
        Assert.Equal(xyz.X, back.X, 1e-6);
        Assert.Equal(xyz.Y, back.Y, 1e-6);
        Assert.Equal(xyz.Z, back.Z, 1e-6);
    }

    [Fact]
    public void Lms_DifferentMatrices_GiveDifferentValues()
    {
        // Same XYZ, different methods → different LMS triples.
        var xyz = new VipsColorXyz(0.4, 0.5, 0.6);
        var bf = VipsColorConvert.XyzToLms(xyz, VipsLmsAdaptation.Bradford);
        var c02 = VipsColorConvert.XyzToLms(xyz, VipsLmsAdaptation.Cat02);
        var c97 = VipsColorConvert.XyzToLms(xyz, VipsLmsAdaptation.Cat97s);
        Assert.NotEqual(bf.L, c02.L);
        Assert.NotEqual(bf.L, c97.L);
        Assert.NotEqual(c02.L, c97.L);
    }

    [Fact]
    public void ChromaticAdapt_Cat02_AdaptsWhite()
    {
        // D65 white via CAT02 → D50 white (within rounding).
        var d65W = VipsColorConvert.WhitePointXyz(VipsWhitePoint.D65);
        var d50W = VipsColorConvert.WhitePointXyz(VipsWhitePoint.D50);
        var adapted = VipsColorConvert.ChromaticAdapt(d65W, VipsWhitePoint.D65, VipsWhitePoint.D50,
            VipsLmsAdaptation.Cat02);
        Assert.Equal(d50W.X, adapted.X, 1e-3);
        Assert.Equal(d50W.Y, adapted.Y, 1e-3);
        Assert.Equal(d50W.Z, adapted.Z, 1e-3);
    }

    [Fact]
    public void ChromaticAdapt_RoundTrip_AllMatrices()
    {
        var xyz = new VipsColorXyz(0.4, 0.5, 0.6);
        foreach (var m in new[] { VipsLmsAdaptation.Bradford, VipsLmsAdaptation.Cat02, VipsLmsAdaptation.Cat97s })
        {
            var toD50 = VipsColorConvert.ChromaticAdapt(xyz, VipsWhitePoint.D65, VipsWhitePoint.D50, m);
            var back = VipsColorConvert.ChromaticAdapt(toD50, VipsWhitePoint.D50, VipsWhitePoint.D65, m);
            Assert.Equal(xyz.X, back.X, 1e-6);
            Assert.Equal(xyz.Y, back.Y, 1e-6);
            Assert.Equal(xyz.Z, back.Z, 1e-6);
        }
    }

    // ---- YCbCr ----

    [Fact]
    public void YCbCr_PureRed_KnownValue()
    {
        // BT.601: R=1 → Y=0.299, Cb=-0.168736+0.5=0.331264, Cr=0.5+0.5=1.0
        var yc = VipsColorConvert.RgbToYCbCr(new VipsColorRgb(1, 0, 0));
        Assert.Equal(0.299, yc.Y, Tol);
        Assert.Equal(0.331264, yc.Cb, Tol);
        Assert.Equal(1.0, yc.Cr, Tol);
    }

    [Fact]
    public void YCbCr_NeutralGrey_ChromaIsHalf()
    {
        // Grey (R=G=B): Cb = Cr = 0.5 (neutral chroma).
        var yc = VipsColorConvert.RgbToYCbCr(new VipsColorRgb(0.5, 0.5, 0.5));
        Assert.Equal(0.5, yc.Y, Tol);
        Assert.Equal(0.5, yc.Cb, Tol);
        Assert.Equal(0.5, yc.Cr, Tol);
    }

    [Fact]
    public void YCbCr_RoundTrip()
    {
        // BT.601 forward and inverse coefficients are 6-decimal rounded
        // (0.299 / 1.402 / 0.344136 / 0.714136 / 0.5 / 1.772) — not
        // exact mathematical inverses, so round-trip error ~ 1e-6.
        var rgb = new VipsColorRgb(0.4, 0.7, 0.2);
        var back = VipsColorConvert.YCbCrToRgb(VipsColorConvert.RgbToYCbCr(rgb));
        Assert.Equal(rgb.R, back.R, 1e-5);
        Assert.Equal(rgb.G, back.G, 1e-5);
        Assert.Equal(rgb.B, back.B, 1e-5);
    }

    [Fact]
    public void YCbCr_ConvertGenericRoundTrip()
    {
        var rgb = new VipsColorRgb(0.6, 0.3, 0.85);
        var yc = VipsColorConvert.Convert<VipsColorRgb, VipsColorYCbCr>(rgb);
        var back = VipsColorConvert.Convert<VipsColorYCbCr, VipsColorRgb>(yc);
        Assert.Equal(rgb.R, back.R, Tol);
        Assert.Equal(rgb.G, back.G, Tol);
        Assert.Equal(rgb.B, back.B, Tol);
    }
}
