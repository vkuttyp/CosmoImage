using System;
using CosmoImage.Operations.Color;
using Xunit;

namespace CosmoImage.Tests;

public class Round79Tests
{
    private const double Tol = 1e-3;

    private static void AssertCloseRgb(VipsColorRgb a, VipsColorRgb b, double tol = Tol)
    {
        Assert.Equal(a.R, b.R, tol);
        Assert.Equal(a.G, b.G, tol);
        Assert.Equal(a.B, b.B, tol);
    }

    // ---- RGB ↔ HSL ----

    [Fact]
    public void RgbToHsl_PureRed()
    {
        var hsl = VipsColorConvert.RgbToHsl(new VipsColorRgb(1, 0, 0));
        Assert.Equal(0, hsl.H, Tol);
        Assert.Equal(1, hsl.S, Tol);
        Assert.Equal(0.5, hsl.L, Tol);
    }

    [Fact]
    public void RgbToHsl_PureGreen_Hue120()
    {
        var hsl = VipsColorConvert.RgbToHsl(new VipsColorRgb(0, 1, 0));
        Assert.Equal(120, hsl.H, Tol);
    }

    [Fact]
    public void RgbToHsl_PureBlue_Hue240()
    {
        var hsl = VipsColorConvert.RgbToHsl(new VipsColorRgb(0, 0, 1));
        Assert.Equal(240, hsl.H, Tol);
    }

    [Fact]
    public void RgbToHsl_GreyHasZeroSaturation()
    {
        var hsl = VipsColorConvert.RgbToHsl(new VipsColorRgb(0.5, 0.5, 0.5));
        Assert.Equal(0, hsl.S, Tol);
        Assert.Equal(0.5, hsl.L, Tol);
    }

    [Theory]
    [InlineData(0.7, 0.4, 0.2)]
    [InlineData(0.1, 0.9, 0.5)]
    [InlineData(0.0, 0.0, 0.0)]
    [InlineData(1.0, 1.0, 1.0)]
    public void RgbToHsl_RoundTrip(double r, double g, double b)
    {
        var rgb = new VipsColorRgb(r, g, b);
        var hsl = VipsColorConvert.RgbToHsl(rgb);
        var back = VipsColorConvert.HslToRgb(hsl);
        AssertCloseRgb(rgb, back);
    }

    // ---- RGB ↔ HSV ----

    [Fact]
    public void RgbToHsv_PureRed()
    {
        var hsv = VipsColorConvert.RgbToHsv(new VipsColorRgb(1, 0, 0));
        Assert.Equal(0, hsv.H, Tol);
        Assert.Equal(1, hsv.S, Tol);
        Assert.Equal(1, hsv.V, Tol);
    }

    [Fact]
    public void RgbToHsv_RoundTripRandom()
    {
        var rgb = new VipsColorRgb(0.3, 0.6, 0.85);
        var hsv = VipsColorConvert.RgbToHsv(rgb);
        var back = VipsColorConvert.HsvToRgb(hsv);
        AssertCloseRgb(rgb, back);
    }

    // ---- RGB ↔ CMYK ----

    [Fact]
    public void RgbToCmyk_PureRed()
    {
        var c = VipsColorConvert.RgbToCmyk(new VipsColorRgb(1, 0, 0));
        Assert.Equal(0, c.C, Tol);
        Assert.Equal(1, c.M, Tol);
        Assert.Equal(1, c.Y, Tol);
        Assert.Equal(0, c.K, Tol);
    }

    [Fact]
    public void RgbToCmyk_Black()
    {
        var c = VipsColorConvert.RgbToCmyk(new VipsColorRgb(0, 0, 0));
        Assert.Equal(1, c.K, Tol);
    }

    [Fact]
    public void RgbToCmyk_RoundTrip()
    {
        var rgb = new VipsColorRgb(0.4, 0.7, 0.2);
        var cmyk = VipsColorConvert.RgbToCmyk(rgb);
        var back = VipsColorConvert.CmykToRgb(cmyk);
        AssertCloseRgb(rgb, back);
    }

    // ---- RGB ↔ XYZ (sRGB → D65) ----

    [Fact]
    public void RgbToXyz_PureRed_KnownValue()
    {
        // Reference: pure sRGB red → XYZ ≈ (0.4124, 0.2126, 0.0193).
        var xyz = VipsColorConvert.RgbToXyz(new VipsColorRgb(1, 0, 0));
        Assert.Equal(0.4124, xyz.X, 3);
        Assert.Equal(0.2126, xyz.Y, 3);
        Assert.Equal(0.0193, xyz.Z, 3);
    }

    [Fact]
    public void RgbToXyz_RoundTrip()
    {
        var rgb = new VipsColorRgb(0.3, 0.6, 0.9);
        var back = VipsColorConvert.XyzToRgb(VipsColorConvert.RgbToXyz(rgb));
        AssertCloseRgb(rgb, back, 1e-6);
    }

    // ---- XYZ ↔ Lab ----

    [Fact]
    public void XyzToLab_PureRed_KnownValue()
    {
        // Pure sRGB red → Lab ≈ (53.24, 80.09, 67.20).
        var lab = VipsColorConvert.XyzToLab(VipsColorConvert.RgbToXyz(new VipsColorRgb(1, 0, 0)));
        Assert.Equal(53.24, lab.L, 1);
        Assert.Equal(80.09, lab.A, 1);
        Assert.Equal(67.20, lab.B, 1);
    }

    [Fact]
    public void LabToXyz_RoundTrip()
    {
        var xyz = new VipsColorXyz(0.4, 0.5, 0.6);
        var back = VipsColorConvert.LabToXyz(VipsColorConvert.XyzToLab(xyz));
        Assert.Equal(xyz.X, back.X, 1e-9);
        Assert.Equal(xyz.Y, back.Y, 1e-9);
        Assert.Equal(xyz.Z, back.Z, 1e-9);
    }

    // ---- Lab ↔ Lch ----

    [Fact]
    public void LabToLch_RoundTrip()
    {
        var lab = new VipsColorLab(50, 30, -40);
        var back = VipsColorConvert.LchToLab(VipsColorConvert.LabToLch(lab));
        Assert.Equal(lab.L, back.L, 1e-9);
        Assert.Equal(lab.A, back.A, 1e-9);
        Assert.Equal(lab.B, back.B, 1e-9);
    }

    [Fact]
    public void Lch_HueIsAngle()
    {
        // a=positive, b=0 → hue=0 (along +a axis).
        var lch = VipsColorConvert.LabToLch(new VipsColorLab(50, 30, 0));
        Assert.Equal(0, lch.H, Tol);
        Assert.Equal(30, lch.C, Tol);
        // a=0, b=positive → hue=90.
        var lch2 = VipsColorConvert.LabToLch(new VipsColorLab(50, 0, 30));
        Assert.Equal(90, lch2.H, Tol);
    }

    // ---- Generic Convert ----

    [Fact]
    public void Convert_Identity_ReturnsSame()
    {
        var rgb = new VipsColorRgb(0.3, 0.7, 0.5);
        var same = VipsColorConvert.Convert<VipsColorRgb, VipsColorRgb>(rgb);
        AssertCloseRgb(rgb, same, 1e-9);
    }

    [Fact]
    public void Convert_RgbToHsl_MatchesDirect()
    {
        var rgb = new VipsColorRgb(0.4, 0.6, 0.2);
        var direct = VipsColorConvert.RgbToHsl(rgb);
        var via = VipsColorConvert.Convert<VipsColorRgb, VipsColorHsl>(rgb);
        Assert.Equal(direct.H, via.H, Tol);
        Assert.Equal(direct.S, via.S, Tol);
        Assert.Equal(direct.L, via.L, Tol);
    }

    [Fact]
    public void Convert_HslToCmyk_RoundsThroughRgb()
    {
        var hsl = new VipsColorHsl(120, 1, 0.5);  // pure green
        var cmyk = VipsColorConvert.Convert<VipsColorHsl, VipsColorCmyk>(hsl);
        // Pure green → CMYK (1, 0, 1, 0).
        Assert.Equal(1, cmyk.C, Tol);
        Assert.Equal(0, cmyk.M, Tol);
        Assert.Equal(1, cmyk.Y, Tol);
        Assert.Equal(0, cmyk.K, Tol);
    }

    [Fact]
    public void Convert_RgbToLab_MatchesChained()
    {
        var rgb = new VipsColorRgb(0.5, 0.25, 0.75);
        var direct = VipsColorConvert.XyzToLab(VipsColorConvert.RgbToXyz(rgb));
        var via = VipsColorConvert.Convert<VipsColorRgb, VipsColorLab>(rgb);
        Assert.Equal(direct.L, via.L, Tol);
        Assert.Equal(direct.A, via.A, Tol);
        Assert.Equal(direct.B, via.B, Tol);
    }

    [Fact]
    public void Convert_LchRoundTrip()
    {
        var rgb = new VipsColorRgb(0.6, 0.3, 0.2);
        var lch = VipsColorConvert.Convert<VipsColorRgb, VipsColorLch>(rgb);
        var back = VipsColorConvert.Convert<VipsColorLch, VipsColorRgb>(lch);
        AssertCloseRgb(rgb, back);
    }
}
