using System;
using System.Buffers.Binary;
using CosmoImage.Operations.Misc;
using Xunit;

namespace CosmoImage.Tests;

public class Round46Tests
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

    private static VipsImage UCharSolid(int w, int h, int bands, byte v)
        => new VipsImage
        {
            Width = w, Height = h, Bands = bands, BandFormat = VipsBandFormat.UChar,
            Interpretation = bands == 1 ? VipsInterpretation.BW : VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int i = 0; i < reg.Valid.Width * bands; i++) addr[i] = v;
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

    // ---- Atan ----

    [Fact]
    public void Atan_Float_MatchesMath()
    {
        Assert.Equal(MathF.Atan(1f), ReadFloat1(VipsImageOps.Atan(FloatScalar(1.0))), 4);
        Assert.Equal(MathF.Atan(-2f), ReadFloat1(VipsImageOps.Atan(FloatScalar(-2.0))), 4);
        Assert.Equal(0f, ReadFloat1(VipsImageOps.Atan(FloatScalar(0.0))), 4);
    }

    // ---- Math2 ----

    [Fact]
    public void Pow_FloatPair()
    {
        var l = FloatScalar(3.0);
        var r = FloatScalar(2.0);
        Assert.Equal(9f, ReadFloat1(VipsImageOps.Pow(l, r)), 3);
    }

    [Fact]
    public void Wop_IsReversedPow()
    {
        var l = FloatScalar(2.0);
        var r = FloatScalar(3.0);
        // Wop(l, r) = r^l = 3^2 = 9.
        Assert.Equal(9f, ReadFloat1(VipsImageOps.Wop(l, r)), 3);
    }

    [Fact]
    public void Atan2_FloatPair()
    {
        var l = FloatScalar(1.0); // y
        var r = FloatScalar(0.0); // x → atan2(1, 0) = π/2
        Assert.Equal(MathF.PI / 2, ReadFloat1(VipsImageOps.Atan2(l, r)), 3);
    }

    // ---- Clamp ----

    [Fact]
    public void Clamp_UCharLimitsValuesToRange()
    {
        // Values 50/150/250 — clamp to [100, 200] → 100/150/200.
        var src = new VipsImage
        {
            Width = 3, Height = 1, Bands = 1, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                var addr = reg.GetAddress(0, 0);
                addr[0] = 50; addr[1] = 150; addr[2] = 250;
                return 0;
            }
        };
        var c = VipsImageOps.Clamp(src, min: 100, max: 200);
        using var reg = new VipsRegion(c);
        reg.Prepare(new VipsRect(0, 0, 3, 1));
        Assert.Equal(100, reg.GetAddress(0, 0)[0]);
        Assert.Equal(150, reg.GetAddress(1, 0)[0]);
        Assert.Equal(200, reg.GetAddress(2, 0)[0]);
    }

    [Fact]
    public void Clamp_FloatHandlesNegatives()
    {
        var src = FloatScalar(-3.5);
        var c = VipsImageOps.Clamp(src, min: -1.0, max: 1.0);
        Assert.Equal(-1f, ReadFloat1(c), 4);
    }

    // ---- LinearConst ----

    [Fact]
    public void LinearConst_BroadcastsScalarOverBands()
    {
        // 3-band UChar image of 100 → linear(2, 10) → 210, applied uniformly.
        var src = UCharSolid(2, 2, 3, 100);
        var lin = VipsImageOps.LinearConst(src, a: 2.0, b: 10.0);
        using var reg = new VipsRegion(lin);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        var p = reg.GetAddress(0, 0);
        // Linear is Float in the existing op? Check whatever the existing
        // Linear returns; if UChar then 210, if Float then 210.0.
        if (lin.BandFormat == VipsBandFormat.UChar)
        {
            Assert.Equal(210, p[0]);
            Assert.Equal(210, p[1]);
            Assert.Equal(210, p[2]);
        }
        else
        {
            Assert.Equal(210f, BinaryPrimitives.ReadSingleLittleEndian(p.Slice(0, 4)), 3);
        }
    }

    // ---- Measure ----

    [Fact]
    public void Measure_UniformImage_GivesUniformPatches()
    {
        var src = UCharSolid(20, 20, 1, 100);
        var m = VipsImageOps.Measure(src, h: 4, v: 4);
        Assert.Equal(16, m.Width); // 4×4 = 16 patches
        using var reg = new VipsRegion(m);
        reg.Prepare(new VipsRect(0, 0, 16, 1));
        for (int x = 0; x < 16; x++)
        {
            float v = BinaryPrimitives.ReadSingleLittleEndian(reg.GetAddress(x, 0).Slice(0, 4));
            Assert.Equal(100f, v, 1);
        }
    }

    [Fact]
    public void Measure_GridDistinguishesPatches()
    {
        // Build a 2×1 image: left half value 50, right half value 200.
        var src = new VipsImage
        {
            Width = 20, Height = 20, Bands = 1, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                        addr[x] = (byte)((reg.Valid.Left + x) < 10 ? 50 : 200);
                }
                return 0;
            }
        };
        // 2×1 grid → 2 patches: left ≈ 50, right ≈ 200.
        var m = VipsImageOps.Measure(src, h: 2, v: 1);
        Assert.Equal(2, m.Width);
        using var reg = new VipsRegion(m);
        reg.Prepare(new VipsRect(0, 0, 2, 1));
        float pLeft = BinaryPrimitives.ReadSingleLittleEndian(reg.GetAddress(0, 0).Slice(0, 4));
        float pRight = BinaryPrimitives.ReadSingleLittleEndian(reg.GetAddress(1, 0).Slice(0, 4));
        Assert.InRange(pLeft, 49, 51);
        Assert.InRange(pRight, 199, 201);
    }
}
