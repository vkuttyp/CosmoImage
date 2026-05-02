using System;
using System.Buffers.Binary;
using Xunit;

namespace CosmoImage.Tests;

public class Round35Tests
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

    private static VipsImage UChar3(byte b0, byte b1, byte b2,
        VipsInterpretation interp = VipsInterpretation.SRGB)
        => new VipsImage
        {
            Width = 1, Height = 1, Bands = 3, BandFormat = VipsBandFormat.UChar,
            Interpretation = interp,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                var addr = reg.GetAddress(0, 0);
                addr[0] = b0; addr[1] = b1; addr[2] = b2;
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

    private static (byte, byte, byte) ReadByte3(VipsImage img)
    {
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, 1, 1));
        var addr = reg.GetAddress(0, 0);
        return (addr[0], addr[1], addr[2]);
    }

    // ---- XYZ ↔ OkLab ----

    [Fact]
    public void XYZ2OkLab_D65WhitePoint_MapsTo_1_0_0()
    {
        // D65 (X, Y, Z) = (0.95047, 1, 1.08883) → OkLab ≈ (1, 0, 0).
        var xyz = Float3(0.95047, 1.0, 1.08883);
        var ok = xyz.XYZ2OkLab();
        var (L, a, b) = ReadFloat3(ok);
        Assert.Equal(1.0f, L, 3);
        Assert.True(Math.Abs(a) < 0.001f, $"a should be ~0, got {a}");
        Assert.True(Math.Abs(b) < 0.001f, $"b should be ~0, got {b}");
    }

    [Fact]
    public void OkLab_RoundTripsThroughXYZ()
    {
        var xyz = Float3(0.4, 0.3, 0.2);
        var (X, Y, Z) = ReadFloat3(xyz.XYZ2OkLab().OkLab2XYZ());
        Assert.Equal(0.4f, X, 4);
        Assert.Equal(0.3f, Y, 4);
        Assert.Equal(0.2f, Z, 4);
    }

    [Fact]
    public void XYZ2OkLab_PureBlack_IsZero()
    {
        var black = Float3(0, 0, 0);
        var (L, a, b) = ReadFloat3(black.XYZ2OkLab());
        Assert.Equal(0, L, 4);
        Assert.Equal(0, a, 4);
        Assert.Equal(0, b, 4);
    }

    // ---- OkLab ↔ OkLCh ----

    [Fact]
    public void OkLab2OkLCh_PureRedAxis_HasZeroHue()
    {
        var lab = Float3(0.5, 0.1, 0, VipsInterpretation.OkLab);
        var (L, C, h) = ReadFloat3(lab.OkLab2OkLCh());
        Assert.Equal(0.5f, L, 4);
        Assert.Equal(0.1f, C, 4);
        Assert.Equal(0f, h, 3);
    }

    [Fact]
    public void OkLab_RoundTripsThroughOkLCh()
    {
        var lab = Float3(0.6, -0.05, 0.08, VipsInterpretation.OkLab);
        var back = lab.OkLab2OkLCh().OkLCh2OkLab();
        var (L, a, b) = ReadFloat3(back);
        Assert.Equal(0.6f, L, 4);
        Assert.Equal(-0.05f, a, 4);
        Assert.Equal(0.08f, b, 4);
    }

    [Fact]
    public void OkLab2OkLCh_HueIsInDegreesNotRadians()
    {
        // a = 0, b = +chroma → 90°.
        var lab = Float3(0.5, 0, 0.2, VipsInterpretation.OkLab);
        var (_, _, h) = ReadFloat3(lab.OkLab2OkLCh());
        Assert.Equal(90f, h, 2);
    }

    // ---- HSV ↔ sRGB ----

    [Fact]
    public void SRGB2HSV_PureRed_HasZeroHue()
    {
        var rgb = UChar3(255, 0, 0);
        var (H, S, V) = ReadByte3(rgb.SRGB2HSV());
        Assert.Equal(0, H);
        Assert.Equal(255, S);
        Assert.Equal(255, V);
    }

    [Fact]
    public void SRGB2HSV_PureGreen_Has120Degrees()
    {
        // 120° in [0, 256) packing → 256 * 120 / 360 ≈ 85.
        var rgb = UChar3(0, 255, 0);
        var (H, S, V) = ReadByte3(rgb.SRGB2HSV());
        Assert.InRange(H, 84, 86);
        Assert.Equal(255, S);
        Assert.Equal(255, V);
    }

    [Fact]
    public void SRGB2HSV_Grey_HasZeroSaturation()
    {
        var rgb = UChar3(120, 120, 120);
        var (H, S, V) = ReadByte3(rgb.SRGB2HSV());
        Assert.Equal(0, S);
        Assert.Equal(120, V);
    }

    [Fact]
    public void SRGB_RoundTripsThroughHSV_OnSaturatedColours()
    {
        // Saturated colours round-trip cleanly; mid-greys do too.
        foreach (var (r, g, b) in new (byte, byte, byte)[]
                 {
                     (255, 0, 0), (0, 255, 0), (0, 0, 255),
                     (255, 255, 0), (0, 255, 255), (255, 0, 255),
                     (180, 100, 50),
                 })
        {
            var rgb = UChar3(r, g, b);
            var back = rgb.SRGB2HSV().HSV2sRGB();
            var (R, G, B) = ReadByte3(back);
            // Allow ±2 from the H quantisation in libvips' UChar packing.
            Assert.InRange(Math.Abs(R - r), 0, 2);
            Assert.InRange(Math.Abs(G - g), 0, 2);
            Assert.InRange(Math.Abs(B - b), 0, 2);
        }
    }

    [Fact]
    public void HSV2sRGB_ZeroSaturation_IsGrey()
    {
        var hsv = UChar3(100, 0, 200);
        var (R, G, B) = ReadByte3(hsv.HSV2sRGB());
        Assert.Equal(200, R);
        Assert.Equal(200, G);
        Assert.Equal(200, B);
    }
}
