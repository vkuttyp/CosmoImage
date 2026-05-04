using System;
using CosmoImage.Operations.Color;
using Xunit;

namespace CosmoImage.Tests;

public class Round80Tests
{
    private const double Tol = 1e-3;

    // ---- xyY ----

    [Fact]
    public void XyzToXyy_PreservesY()
    {
        var xyz = new VipsColorXyz(0.4124, 0.2126, 0.0193);  // pure red
        var xyy = VipsColorConvert.XyzToXyy(xyz);
        // Y component preserved as luminance.
        Assert.Equal(0.2126, xyy.LargeY, Tol);
        // x + y + (1 - x - y) = 1 → chromaticity sums to 1 if normalised.
        Assert.True(xyy.X > 0 && xyy.X < 1);
        Assert.True(xyy.Y > 0 && xyy.Y < 1);
    }

    [Fact]
    public void Xyy_RoundTrip()
    {
        var xyz = new VipsColorXyz(0.4, 0.5, 0.6);
        var back = VipsColorConvert.XyyToXyz(VipsColorConvert.XyzToXyy(xyz));
        Assert.Equal(xyz.X, back.X, 1e-9);
        Assert.Equal(xyz.Y, back.Y, 1e-9);
        Assert.Equal(xyz.Z, back.Z, 1e-9);
    }

    [Fact]
    public void Xyy_BlackHandled()
    {
        // Y=0 should round-trip to (0, 0, 0) without NaN.
        var xyy = VipsColorConvert.XyzToXyy(new VipsColorXyz(0, 0, 0));
        var back = VipsColorConvert.XyyToXyz(xyy);
        Assert.Equal(0, back.X, Tol);
        Assert.Equal(0, back.Y, Tol);
        Assert.Equal(0, back.Z, Tol);
    }

    // ---- Luv ----

    [Fact]
    public void XyzToLuv_PureRed_KnownValue()
    {
        // Pure sRGB red → Luv ≈ (53.24, 175.0, 37.8). Reference values
        // computed from the standard CIE 1976 formulas.
        var xyz = VipsColorConvert.RgbToXyz(new VipsColorRgb(1, 0, 0));
        var luv = VipsColorConvert.XyzToLuv(xyz);
        Assert.Equal(53.24, luv.L, 1);
        Assert.Equal(175.0, luv.U, 1);
        Assert.Equal(37.8, luv.V, 1);
    }

    [Fact]
    public void Luv_RoundTrip()
    {
        var xyz = new VipsColorXyz(0.4, 0.5, 0.6);
        var back = VipsColorConvert.LuvToXyz(VipsColorConvert.XyzToLuv(xyz));
        Assert.Equal(xyz.X, back.X, 1e-6);
        Assert.Equal(xyz.Y, back.Y, 1e-6);
        Assert.Equal(xyz.Z, back.Z, 1e-6);
    }

    [Fact]
    public void Luv_BlackHasZeroL()
    {
        var luv = VipsColorConvert.XyzToLuv(new VipsColorXyz(0, 0, 0));
        Assert.Equal(0, luv.L, Tol);
        // Below the linearity threshold the formula returns L=0; u and v are 0 too.
    }

    // ---- Lchuv ----

    [Fact]
    public void Lchuv_RoundTrip()
    {
        var luv = new VipsColorLuv(50, 30, -40);
        var back = VipsColorConvert.LchuvToLuv(VipsColorConvert.LuvToLchuv(luv));
        Assert.Equal(luv.L, back.L, 1e-9);
        Assert.Equal(luv.U, back.U, 1e-9);
        Assert.Equal(luv.V, back.V, 1e-9);
    }

    [Fact]
    public void Lchuv_HueIsAtan2OfUv()
    {
        var lchuv = VipsColorConvert.LuvToLchuv(new VipsColorLuv(50, 30, 0));
        Assert.Equal(0, lchuv.H, Tol);
        Assert.Equal(30, lchuv.C, Tol);
        var lchuv2 = VipsColorConvert.LuvToLchuv(new VipsColorLuv(50, 0, 30));
        Assert.Equal(90, lchuv2.H, Tol);
    }

    // ---- LMS (Bradford) ----

    [Fact]
    public void Lms_BradfordRoundTrip()
    {
        var xyz = new VipsColorXyz(0.5, 0.6, 0.7);
        var back = VipsColorConvert.LmsToXyz(VipsColorConvert.XyzToLms(xyz));
        Assert.Equal(xyz.X, back.X, 1e-6);
        Assert.Equal(xyz.Y, back.Y, 1e-6);
        Assert.Equal(xyz.Z, back.Z, 1e-6);
    }

    // ---- Chromatic adaptation ----

    [Fact]
    public void ChromaticAdapt_SameWhitePoint_IsIdentity()
    {
        var xyz = new VipsColorXyz(0.4, 0.5, 0.6);
        var adapted = VipsColorConvert.ChromaticAdapt(xyz, VipsWhitePoint.D65, VipsWhitePoint.D65);
        Assert.Equal(xyz.X, adapted.X, 1e-9);
        Assert.Equal(xyz.Y, adapted.Y, 1e-9);
        Assert.Equal(xyz.Z, adapted.Z, 1e-9);
    }

    [Fact]
    public void ChromaticAdapt_D65WhiteToD50_LandsOnD50White()
    {
        // D65 reference white XYZ adapted to D50 should equal the
        // D50 reference white XYZ (within Bradford rounding).
        var d65W = VipsColorConvert.WhitePointXyz(VipsWhitePoint.D65);
        var d50W = VipsColorConvert.WhitePointXyz(VipsWhitePoint.D50);
        var adapted = VipsColorConvert.ChromaticAdapt(d65W, VipsWhitePoint.D65, VipsWhitePoint.D50);
        Assert.Equal(d50W.X, adapted.X, 1e-3);
        Assert.Equal(d50W.Y, adapted.Y, 1e-3);
        Assert.Equal(d50W.Z, adapted.Z, 1e-3);
    }

    [Fact]
    public void ChromaticAdapt_RoundTrip()
    {
        // D65 → D50 → D65 should recover the original XYZ.
        var xyz = new VipsColorXyz(0.4, 0.5, 0.6);
        var toD50 = VipsColorConvert.ChromaticAdapt(xyz, VipsWhitePoint.D65, VipsWhitePoint.D50);
        var back = VipsColorConvert.ChromaticAdapt(toD50, VipsWhitePoint.D50, VipsWhitePoint.D65);
        Assert.Equal(xyz.X, back.X, 1e-6);
        Assert.Equal(xyz.Y, back.Y, 1e-6);
        Assert.Equal(xyz.Z, back.Z, 1e-6);
    }

    [Fact]
    public void WhitePointXyz_KnownReferences()
    {
        var d65 = VipsColorConvert.WhitePointXyz(VipsWhitePoint.D65);
        Assert.Equal(0.95047, d65.X, Tol);
        Assert.Equal(1.0, d65.Y, Tol);
        Assert.Equal(1.08883, d65.Z, Tol);

        var e = VipsColorConvert.WhitePointXyz(VipsWhitePoint.E);
        Assert.Equal(1.0, e.X, Tol);
        Assert.Equal(1.0, e.Y, Tol);
        Assert.Equal(1.0, e.Z, Tol);
    }

    // ---- Generic dispatcher with new types ----

    [Fact]
    public void Convert_RgbToLuv_MatchesDirect()
    {
        var rgb = new VipsColorRgb(0.5, 0.25, 0.75);
        var direct = VipsColorConvert.XyzToLuv(VipsColorConvert.RgbToXyz(rgb));
        var via = VipsColorConvert.Convert<VipsColorRgb, VipsColorLuv>(rgb);
        Assert.Equal(direct.L, via.L, Tol);
        Assert.Equal(direct.U, via.U, Tol);
        Assert.Equal(direct.V, via.V, Tol);
    }

    [Fact]
    public void Convert_LchuvRoundTrip()
    {
        var rgb = new VipsColorRgb(0.6, 0.3, 0.2);
        var lchuv = VipsColorConvert.Convert<VipsColorRgb, VipsColorLchuv>(rgb);
        var back = VipsColorConvert.Convert<VipsColorLchuv, VipsColorRgb>(lchuv);
        Assert.Equal(rgb.R, back.R, Tol);
        Assert.Equal(rgb.G, back.G, Tol);
        Assert.Equal(rgb.B, back.B, Tol);
    }

    [Fact]
    public void Convert_XyyAndLmsBothInDispatcher()
    {
        var rgb = new VipsColorRgb(0.4, 0.6, 0.8);
        // Round-trip via xyY.
        var viaXyy = VipsColorConvert.Convert<VipsColorXyy, VipsColorRgb>(
            VipsColorConvert.Convert<VipsColorRgb, VipsColorXyy>(rgb));
        Assert.Equal(rgb.R, viaXyy.R, Tol);
        Assert.Equal(rgb.G, viaXyy.G, Tol);
        Assert.Equal(rgb.B, viaXyy.B, Tol);
        // Round-trip via LMS.
        var viaLms = VipsColorConvert.Convert<VipsColorLms, VipsColorRgb>(
            VipsColorConvert.Convert<VipsColorRgb, VipsColorLms>(rgb));
        Assert.Equal(rgb.R, viaLms.R, Tol);
        Assert.Equal(rgb.G, viaLms.G, Tol);
        Assert.Equal(rgb.B, viaLms.B, Tol);
    }
}
