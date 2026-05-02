using System;
using System.Buffers.Binary;
using Xunit;

namespace CosmoImage.Tests;

public class Round34Tests
{
    /// <summary>Build a single-pixel Float 3-band image with the given values.</summary>
    private static VipsImage Float3(double v0, double v1, double v2,
        VipsInterpretation interp = VipsInterpretation.Lab)
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

    // ---- XYZ ↔ Yxy ----

    [Fact]
    public void XYZ2Yxy_D65WhitePoint_GivesKnownChromaticity()
    {
        // D65 (X, Y, Z) = (0.95047, 1, 1.08883) → (Y, x, y) = (1, 0.3127, 0.3290).
        var xyz = Float3(0.95047, 1.0, 1.08883, VipsInterpretation.XYZ);
        var yxy = xyz.XYZ2Yxy();
        var (Y, x, y) = ReadFloat3(yxy);
        Assert.Equal(1.0f, Y, 4);
        Assert.Equal(0.3127f, x, 3);
        Assert.Equal(0.3290f, y, 3);
    }

    [Fact]
    public void XYZ_RoundTripsThroughYxy()
    {
        var src = Float3(0.5, 0.7, 0.9, VipsInterpretation.XYZ);
        var back = src.XYZ2Yxy().Yxy2XYZ();
        var (X, Y, Z) = ReadFloat3(back);
        Assert.Equal(0.5f, X, 4);
        Assert.Equal(0.7f, Y, 4);
        Assert.Equal(0.9f, Z, 4);
    }

    [Fact]
    public void XYZ2Yxy_PureBlack_IsZeros()
    {
        var black = Float3(0, 0, 0, VipsInterpretation.XYZ);
        var yxy = black.XYZ2Yxy();
        var (Y, x, y) = ReadFloat3(yxy);
        Assert.Equal(0, Y);
        Assert.Equal(0, x);
        Assert.Equal(0, y);
    }

    // ---- Lab ↔ LabQ ----

    [Fact]
    public void Lab2LabQ_BandsAndCoding()
    {
        var lab = Float3(50, 10, -20);
        var q = lab.Lab2LabQ();
        Assert.Equal(4, q.Bands);
        Assert.Equal(VipsBandFormat.UChar, q.BandFormat);
        Assert.Equal(VipsCoding.LabQ, q.Coding);
    }

    [Fact]
    public void Lab_RoundTripsThroughLabQ_WithinPrecision()
    {
        // LabQ: L has 10 bits over [0,100] → step ≈ 0.0978
        // a / b: 11 bits over [-128,128] → step = 0.125
        var lab = Float3(50.0, 25.5, -30.2);
        var back = lab.Lab2LabQ().LabQ2Lab();
        var (L, a, b) = ReadFloat3(back);
        Assert.Equal(50.0, L, 1);
        Assert.Equal(25.5, a, 1);
        Assert.Equal(-30.2, b, 1);
    }

    [Fact]
    public void LabQ_PreservesNeutralAxis()
    {
        // Neutral grey (a = b = 0) must round-trip exactly.
        var lab = Float3(60, 0, 0);
        var back = lab.Lab2LabQ().LabQ2Lab();
        var (L, a, b) = ReadFloat3(back);
        Assert.Equal(60.0, L, 1);
        Assert.Equal(0, a, 3);
        Assert.Equal(0, b, 3);
    }

    [Fact]
    public void LabQ_ExtremesClamp()
    {
        // Out-of-gamut input clamps to L=100 / |a|=128.
        var lab = Float3(110, 200, -200);
        var back = lab.Lab2LabQ().LabQ2Lab();
        var (L, a, b) = ReadFloat3(back);
        Assert.Equal(100.0, L, 1);
        Assert.True(Math.Abs(a) <= 128.5);
        Assert.True(Math.Abs(b) <= 128.5);
    }

    // ---- Lab ↔ LabS ----

    [Fact]
    public void Lab2LabS_BandsAndFormat()
    {
        var lab = Float3(50, 10, -20);
        var s = lab.Lab2LabS();
        Assert.Equal(3, s.Bands);
        Assert.Equal(VipsBandFormat.Short, s.BandFormat);
        Assert.Equal(VipsInterpretation.LabS, s.Interpretation);
    }

    [Fact]
    public void Lab_RoundTripsThroughLabS_HighPrecision()
    {
        // LabS step on a/b: 1/256 ≈ 0.0039.
        var lab = Float3(42.7, 11.3, -27.9);
        var back = lab.Lab2LabS().LabS2Lab();
        var (L, a, b) = ReadFloat3(back);
        Assert.Equal(42.7, L, 2);
        Assert.Equal(11.3, a, 2);
        Assert.Equal(-27.9, b, 2);
    }

    [Fact]
    public void LabS_BeatsLabQ_OnPrecision()
    {
        // The same input through LabS should drift less than through LabQ.
        var lab = Float3(50.0, 10.0, -20.0);

        var (Ls, As, Bs) = ReadFloat3(lab.Lab2LabS().LabS2Lab());
        var (Lq, Aq, Bq) = ReadFloat3(lab.Lab2LabQ().LabQ2Lab());

        double driftS = Math.Abs(Ls - 50) + Math.Abs(As - 10) + Math.Abs(Bs - (-20));
        double driftQ = Math.Abs(Lq - 50) + Math.Abs(Aq - 10) + Math.Abs(Bq - (-20));
        Assert.True(driftS <= driftQ,
            $"LabS drift {driftS} should be ≤ LabQ drift {driftQ}");
    }
}
