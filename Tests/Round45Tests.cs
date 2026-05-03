using System;
using System.Buffers.Binary;
using CosmoImage.Operations.Analysis;
using Xunit;

namespace CosmoImage.Tests;

public class Round45Tests
{
    private static VipsImage FloatScalar(double v)
        => new VipsImage
        {
            Width = 1, Height = 1, Bands = 1, BandFormat = VipsBandFormat.Float,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                BinaryPrimitives.WriteSingleLittleEndian(reg.GetAddress(0, 0).Slice(0, 4), (float)v);
                return 0;
            }
        };

    private static VipsImage FloatGen(int w, int h, Func<int, int, float> fn)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 1, BandFormat = VipsBandFormat.Float,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                        BinaryPrimitives.WriteSingleLittleEndian(addr.Slice(x * 4, 4),
                            fn(reg.Valid.Left + x, reg.Valid.Top + y));
                }
                return 0;
            }
        };

    private static float ReadFloat1(VipsImage img)
    {
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, 1, 1));
        return BinaryPrimitives.ReadSingleLittleEndian(reg.GetAddress(0, 0).Slice(0, 4));
    }

    private static (double Re, double Im) ReadComplex1(VipsImage img)
    {
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, 1, 1));
        var addr = reg.GetAddress(0, 0);
        return (
            BinaryPrimitives.ReadDoubleLittleEndian(addr.Slice(0, 8)),
            BinaryPrimitives.ReadDoubleLittleEndian(addr.Slice(8, 8))
        );
    }

    // ---- Sign / Floor / Ceil / Rint ----

    [Fact]
    public void Sign_Float()
    {
        Assert.Equal(1f, ReadFloat1(VipsImageOps.Sign(FloatScalar(3.5))), 4);
        Assert.Equal(-1f, ReadFloat1(VipsImageOps.Sign(FloatScalar(-2.0))), 4);
        Assert.Equal(0f, ReadFloat1(VipsImageOps.Sign(FloatScalar(0.0))), 4);
    }

    [Fact]
    public void Floor_Ceil_Rint_Float()
    {
        Assert.Equal(2f, ReadFloat1(VipsImageOps.Floor(FloatScalar(2.7))));
        Assert.Equal(3f, ReadFloat1(VipsImageOps.Ceil(FloatScalar(2.1))));
        Assert.Equal(3f, ReadFloat1(VipsImageOps.Rint(FloatScalar(2.6))));
        Assert.Equal(-3f, ReadFloat1(VipsImageOps.Floor(FloatScalar(-2.1))));
    }

    // ---- ComplexForm / ComplexGet ----

    [Fact]
    public void ComplexForm_RoundTripsThroughComplexGet()
    {
        var re = FloatGen(2, 2, (x, y) => 3.5f);
        var im = FloatGen(2, 2, (x, y) => -1.25f);
        var z = VipsImageOps.ComplexForm(re, im);
        Assert.Equal(VipsBandFormat.DPComplex, z.BandFormat);

        Assert.Equal(3.5f, ReadFloat1(VipsImageOps.Real(VipsImageOps.ComplexForm(
            FloatScalar(3.5), FloatScalar(-1.25)))), 4);
        Assert.Equal(-1.25f, ReadFloat1(VipsImageOps.Imag(VipsImageOps.ComplexForm(
            FloatScalar(3.5), FloatScalar(-1.25)))), 4);
    }

    [Fact]
    public void ComplexGet_MagnitudeAndPhase()
    {
        // 3 + 4i: magnitude = 5, phase = atan2(4, 3) ≈ 0.9273.
        var z = VipsImageOps.ComplexForm(FloatScalar(3), FloatScalar(4));
        Assert.Equal(5f, ReadFloat1(VipsImageOps.Magnitude(z)), 4);
        Assert.Equal((float)Math.Atan2(4, 3), ReadFloat1(VipsImageOps.Phase(z)), 4);
    }

    // ---- Complex (unary) ----

    [Fact]
    public void Complex_Polar_RectIsRoundTrip()
    {
        var z = VipsImageOps.ComplexForm(FloatScalar(3), FloatScalar(4));
        var polar = VipsImageOps.Polar(z);
        var (mag, ang) = ReadComplex1(polar);
        Assert.Equal(5.0, mag, 4);
        Assert.Equal(Math.Atan2(4, 3), ang, 4);

        var back = VipsImageOps.Rect(polar);
        var (re, im) = ReadComplex1(back);
        Assert.Equal(3.0, re, 4);
        Assert.Equal(4.0, im, 4);
    }

    [Fact]
    public void Complex_Conj_NegatesImaginary()
    {
        var z = VipsImageOps.ComplexForm(FloatScalar(2), FloatScalar(7));
        var conj = VipsImageOps.Conj(z);
        var (re, im) = ReadComplex1(conj);
        Assert.Equal(2.0, re);
        Assert.Equal(-7.0, im);
    }

    // ---- Complex2 (CrossPhase) ----

    [Fact]
    public void CrossPhase_OfZAndItself_HasZeroImag()
    {
        // z · conj(z) = |z|² (purely real).
        var z = VipsImageOps.ComplexForm(FloatScalar(3), FloatScalar(4));
        var c = VipsImageOps.CrossPhase(z, z);
        var (re, im) = ReadComplex1(c);
        Assert.Equal(25.0, re, 4); // |3+4i|² = 25
        Assert.Equal(0.0, im, 4);
    }

    [Fact]
    public void CrossPhase_DifferentZ_HasMatchingMagnitude()
    {
        // z1 · conj(z2): magnitude = |z1| · |z2|.
        var z1 = VipsImageOps.ComplexForm(FloatScalar(3), FloatScalar(4));   // |z1| = 5
        var z2 = VipsImageOps.ComplexForm(FloatScalar(0), FloatScalar(2));   // |z2| = 2
        var c = VipsImageOps.CrossPhase(z1, z2);
        var (re, im) = ReadComplex1(c);
        double mag = Math.Sqrt(re * re + im * im);
        Assert.Equal(10.0, mag, 4);
    }
}
