using System;
using System.Buffers.Binary;
using Xunit;

namespace CosmoImage.Tests;

public class Round33Tests
{
    /// <summary>Build a single-pixel Float Lab image with the given (L, a, b).</summary>
    private static VipsImage Lab1(double L, double a, double b)
        => new VipsImage
        {
            Width = 1, Height = 1, Bands = 3, BandFormat = VipsBandFormat.Float,
            Interpretation = VipsInterpretation.Lab,
            GenerateFn = (VipsRegion reg, object? seq, object? ca, object? cb, ref bool stop) => {
                var addr = reg.GetAddress(0, 0);
                BinaryPrimitives.WriteSingleLittleEndian(addr.Slice(0, 4), (float)L);
                BinaryPrimitives.WriteSingleLittleEndian(addr.Slice(4, 4), (float)a);
                BinaryPrimitives.WriteSingleLittleEndian(addr.Slice(8, 4), (float)b);
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

    private static float ReadFloat1(VipsImage img)
    {
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, 1, 1));
        return BinaryPrimitives.ReadSingleLittleEndian(reg.GetAddress(0, 0).Slice(0, 4));
    }

    // ---- Lab2XYZ / XYZ2Lab ----

    [Fact]
    public void Lab2XYZ_D65WhitePoint_GivesCorrectXYZ()
    {
        // Lab (100, 0, 0) is the D65 white point → XYZ ≈ (0.95047, 1, 1.08883).
        var lab = Lab1(100, 0, 0);
        var xyz = lab.Lab2XYZ();
        var (X, Y, Z) = ReadFloat3(xyz);
        Assert.Equal(0.95047f, X, 4);
        Assert.Equal(1.0f, Y, 4);
        Assert.Equal(1.08883f, Z, 4);
    }

    [Fact]
    public void Lab_RoundTripsThroughXYZ()
    {
        var lab = Lab1(50, 25, -30);
        var lab2 = lab.Lab2XYZ().XYZ2Lab();
        var (L, a, b) = ReadFloat3(lab2);
        Assert.Equal(50f, L, 3);
        Assert.Equal(25f, a, 3);
        Assert.Equal(-30f, b, 3);
    }

    // ---- Lab2LCh / LCh2Lab ----

    [Fact]
    public void Lab2LCh_PureRedAxis_HasZeroHue()
    {
        // a = +sqrt(2)*C, b = 0 → C = a, h = 0°.
        var lab = Lab1(50, 30, 0);
        var lch = lab.Lab2LCh();
        var (L, C, h) = ReadFloat3(lch);
        Assert.Equal(50f, L, 3);
        Assert.Equal(30f, C, 3);
        Assert.Equal(0f, h, 3);
    }

    [Fact]
    public void Lab2LCh_PureBPlusAxis_HasNinetyHue()
    {
        var lab = Lab1(50, 0, 40);
        var lch = lab.Lab2LCh();
        var (_, C, h) = ReadFloat3(lch);
        Assert.Equal(40f, C, 3);
        Assert.Equal(90f, h, 3);
    }

    [Fact]
    public void Lab_RoundTripsThroughLCh()
    {
        var lab = Lab1(60, -20, 35);
        var lab2 = lab.Lab2LCh().LCh2Lab();
        var (L, a, b) = ReadFloat3(lab2);
        Assert.Equal(60f, L, 3);
        Assert.Equal(-20f, a, 3);
        Assert.Equal(35f, b, 3);
    }

    // ---- dE76 ----

    [Fact]
    public void DE76_SameImage_IsZero()
    {
        var lab = Lab1(50, 10, -10);
        var d = lab.DE76(lab);
        Assert.Equal(0f, ReadFloat1(d), 4);
    }

    [Fact]
    public void DE76_KnownDifference_Pythagorean()
    {
        // Lab1(50, 0, 0) vs Lab1(50, 3, 4) → ΔE = 5 exactly.
        var l1 = Lab1(50, 0, 0);
        var l2 = Lab1(50, 3, 4);
        var d = l1.DE76(l2);
        Assert.Equal(5f, ReadFloat1(d), 4);
    }

    // ---- dE2000 ----

    [Fact]
    public void DE2000_SameColour_IsZero()
    {
        var lab = Lab1(50, 10, -10);
        var d = lab.DE2000(lab);
        Assert.Equal(0f, ReadFloat1(d), 4);
    }

    [Fact]
    public void DE2000_Sharma_TestVector_1()
    {
        // From the Sharma supplemental table:
        // Lab(50, 2.6772, -79.7751) vs Lab(50, 0, -82.7485) → ΔE2000 = 2.0425.
        double dE = VipsImageOps.DE2000(50, 2.6772, -79.7751, 50, 0, -82.7485);
        Assert.Equal(2.0425, dE, 3);
    }

    [Fact]
    public void DE2000_Sharma_TestVector_2()
    {
        // Lab(50, 3.1571, -77.2803) vs Lab(50, 0, -82.7485) → ΔE2000 = 2.8615.
        double dE = VipsImageOps.DE2000(50, 3.1571, -77.2803, 50, 0, -82.7485);
        Assert.Equal(2.8615, dE, 3);
    }

    [Fact]
    public void DE2000_Sharma_TestVector_HuePeak()
    {
        // Lab(50, 50, 0) vs Lab(50, 50, -0.0010) → ΔE2000 ≈ 0.000175 (via hue rotation term).
        double dE = VipsImageOps.DE2000(50, 50, 0, 50, 50, -0.0010);
        Assert.True(dE < 0.001, $"expected tiny ΔE near zero, got {dE}");
    }

    [Fact]
    public void DE2000_DiffersFromDE76_ForSaturatedColours()
    {
        // Saturated blues: dE2000 should be smaller than dE76 for these.
        var l1 = Lab1(50, 0, -50);
        var l2 = Lab1(50, 5, -45);
        var d76 = ReadFloat1(l1.DE76(l2));
        var d00 = ReadFloat1(l1.DE2000(l2));
        Assert.True(d00 < d76, $"expected dE2000 < dE76 for sat. colours, got dE76={d76} dE2000={d00}");
    }
}
