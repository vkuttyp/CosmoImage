using System;
using System.Buffers.Binary;
using CosmoImage.Operations.Geometric;
using Xunit;

namespace CosmoImage.Tests;

public class Round26Tests
{
    private static VipsImage UCharImage(int w, int h, int bands, byte value)
        => new VipsImage
        {
            Width = w, Height = h, Bands = bands, BandFormat = VipsBandFormat.UChar,
            Interpretation = bands == 1 ? VipsInterpretation.BW : VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int i = 0; i < reg.Valid.Width * bands; i++) addr[i] = value;
                }
                return 0;
            }
        };

    private static VipsImage FloatImage(int w, int h, int bands, System.Func<int, int, int, float> fill)
        => new VipsImage
        {
            Width = w, Height = h, Bands = bands, BandFormat = VipsBandFormat.Float,
            Interpretation = bands == 1 ? VipsInterpretation.BW : VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        for (int bnd = 0; bnd < bands; bnd++)
                        {
                            int gx = reg.Valid.Left + x;
                            int gy = reg.Valid.Top + y;
                            BinaryPrimitives.WriteSingleLittleEndian(
                                addr.Slice((x * bands + bnd) * 4, 4),
                                fill(gx, gy, bnd));
                        }
                    }
                }
                return 0;
            }
        };

    private static float ReadFloat(VipsRegion reg, int x, int y, int bnd, int bands)
        => BinaryPrimitives.ReadSingleLittleEndian(reg.GetAddress(x, y).Slice(bnd * 4, 4));

    // ---- Image-image arithmetic ----

    [Fact]
    public void Add_UChar_ClampsToMax()
    {
        var l = UCharImage(2, 2, 1, 200);
        var r = UCharImage(2, 2, 1, 100);
        var sum = l.Add(r);
        using var reg = new VipsRegion(sum);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        Assert.Equal(255, reg.GetAddress(0, 0)[0]); // 200 + 100 → clamp to 255
    }

    [Fact]
    public void Subtract_UChar_ClampsToZero()
    {
        var l = UCharImage(2, 2, 1, 50);
        var r = UCharImage(2, 2, 1, 200);
        var d = l.Subtract(r);
        using var reg = new VipsRegion(d);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        Assert.Equal(0, reg.GetAddress(0, 0)[0]); // 50 - 200 → clamp to 0
    }

    [Fact]
    public void Multiply_UChar_TreatsAsFractions()
    {
        // 128 * 128 → 128/255 * 128/255 ≈ 0.252 → 64
        var l = UCharImage(2, 2, 1, 128);
        var r = UCharImage(2, 2, 1, 128);
        var prod = l.Multiply(r);
        using var reg = new VipsRegion(prod);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        // (128 * 128 + 127) / 255 = 64
        Assert.InRange(reg.GetAddress(0, 0)[0], 63, 65);
    }

    [Fact]
    public void Divide_UChar_HandlesZeroDivisor()
    {
        var l = UCharImage(2, 2, 1, 100);
        var r = UCharImage(2, 2, 1, 0);
        var q = l.Divide(r);
        using var reg = new VipsRegion(q);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        Assert.Equal(0, reg.GetAddress(0, 0)[0]);
    }

    [Fact]
    public void Add_Float_NoClamp()
    {
        var l = FloatImage(2, 2, 1, (x, y, b) => 1.5f);
        var r = FloatImage(2, 2, 1, (x, y, b) => 2.5f);
        var sum = l.Add(r);
        Assert.Equal(VipsBandFormat.Float, sum.BandFormat);
        using var reg = new VipsRegion(sum);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        Assert.Equal(4.0f, ReadFloat(reg, 0, 0, 0, 1));
    }

    [Fact]
    public void Multiply_Float_DirectMul()
    {
        var l = FloatImage(2, 2, 1, (x, y, b) => 2.0f);
        var r = FloatImage(2, 2, 1, (x, y, b) => 3.0f);
        var prod = l.Multiply(r);
        using var reg = new VipsRegion(prod);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        // Float multiply is direct — 2.0 * 3.0 = 6.0 (no /255 scaling)
        Assert.Equal(6.0f, ReadFloat(reg, 0, 0, 0, 1));
    }

    // ---- Premultiply / Unpremultiply ----

    [Fact]
    public void Premultiply_UChar_ScalesColorByAlpha()
    {
        // RGBA pixel: R=200, G=100, B=50, A=128 (~half-transparent)
        var src = new VipsImage
        {
            Width = 1, Height = 1, Bands = 4, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                var addr = reg.GetAddress(0, 0);
                addr[0] = 200; addr[1] = 100; addr[2] = 50; addr[3] = 128;
                return 0;
            }
        };
        var pre = src.Premultiply();
        using var reg = new VipsRegion(pre);
        reg.Prepare(new VipsRect(0, 0, 1, 1));
        // (200 * 128 + 127) / 255 ≈ 100; (100 * 128 + 127) / 255 ≈ 50; (50 * 128 + 127) / 255 ≈ 25
        Assert.InRange(reg.GetAddress(0, 0)[0], 99, 101);
        Assert.InRange(reg.GetAddress(0, 0)[1], 49, 51);
        Assert.InRange(reg.GetAddress(0, 0)[2], 24, 26);
        Assert.Equal(128, reg.GetAddress(0, 0)[3]);
    }

    [Fact]
    public void PremultiplyUnpremultiply_UChar_RoundTripsApproximately()
    {
        var src = new VipsImage
        {
            Width = 2, Height = 2, Bands = 4, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width * 4; x += 4)
                    {
                        addr[x] = 200; addr[x + 1] = 100; addr[x + 2] = 50; addr[x + 3] = 200;
                    }
                }
                return 0;
            }
        };
        var rt = src.Premultiply().Unpremultiply();
        using var reg = new VipsRegion(rt);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        // Round-trip is lossy in UChar (rounding); allow ±2.
        Assert.InRange(reg.GetAddress(0, 0)[0], 198, 202);
        Assert.InRange(reg.GetAddress(0, 0)[1], 98, 102);
        Assert.Equal(200, reg.GetAddress(0, 0)[3]);
    }

    [Fact]
    public void Premultiply_Float_TreatsAlphaAsNominalUnit()
    {
        // Float alpha = 0.5, color = 1.0 → premultiplied color = 0.5
        var src = FloatImage(1, 1, 4, (x, y, b) => b == 3 ? 0.5f : 1.0f);
        var pre = src.Premultiply();
        using var reg = new VipsRegion(pre);
        reg.Prepare(new VipsRect(0, 0, 1, 1));
        Assert.Equal(0.5f, ReadFloat(reg, 0, 0, 0, 4));
        Assert.Equal(0.5f, ReadFloat(reg, 0, 0, 1, 4));
        Assert.Equal(0.5f, ReadFloat(reg, 0, 0, 2, 4));
        Assert.Equal(0.5f, ReadFloat(reg, 0, 0, 3, 4)); // alpha unchanged
    }

    [Fact]
    public void Premultiply_NoAlpha_PassesThrough()
    {
        var src = UCharImage(2, 2, 3, 100);
        var pre = src.Premultiply();
        using var reg = new VipsRegion(pre);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        Assert.Equal(100, reg.GetAddress(0, 0)[0]); // RGB has no alpha → pass-through
    }

    // ---- Embed ----

    [Fact]
    public void Embed_BlackExtension_FillsBackgroundWithZero()
    {
        var src = UCharImage(2, 2, 1, 200);
        var embedded = src.Embed(x: 1, y: 1, width: 4, height: 4);
        using var reg = new VipsRegion(embedded);
        reg.Prepare(new VipsRect(0, 0, 4, 4));
        // Inside source rect (1..2, 1..2) = 200; outside = 0
        Assert.Equal(0, reg.GetAddress(0, 0)[0]);
        Assert.Equal(200, reg.GetAddress(1, 1)[0]);
        Assert.Equal(200, reg.GetAddress(2, 2)[0]);
        Assert.Equal(0, reg.GetAddress(3, 3)[0]);
    }

    [Fact]
    public void Embed_CopyExtension_ReplicatesEdgePixel()
    {
        // 2x2 with distinct corner values to verify which edge replicates where.
        var src = new VipsImage
        {
            Width = 2, Height = 2, Bands = 1, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        int gx = reg.Valid.Left + x; int gy = reg.Valid.Top + y;
                        addr[x] = (byte)(gx * 10 + gy * 100);
                    }
                }
                return 0;
            }
        };
        // Source: (0,0)=0, (1,0)=10, (0,1)=100, (1,1)=110.
        var embedded = src.Embed(x: 2, y: 2, width: 6, height: 6, extend: VipsExtend.Copy);
        using var reg = new VipsRegion(embedded);
        reg.Prepare(new VipsRect(0, 0, 6, 6));
        // Top-left replicates (0,0)=0; bottom-right replicates (1,1)=110.
        Assert.Equal(0, reg.GetAddress(0, 0)[0]);
        Assert.Equal(110, reg.GetAddress(5, 5)[0]);
    }

    [Fact]
    public void Embed_BackgroundExtension_FillsCallerColor()
    {
        var src = UCharImage(2, 2, 3, 100);
        var embedded = src.Embed(x: 1, y: 1, width: 4, height: 4,
            extend: VipsExtend.Background, background: new[] { 200.0, 50.0, 25.0 });
        using var reg = new VipsRegion(embedded);
        reg.Prepare(new VipsRect(0, 0, 4, 4));
        // Out-of-source pixel should have the supplied background color.
        var corner = reg.GetAddress(0, 0);
        Assert.Equal(200, corner[0]);
        Assert.Equal(50, corner[1]);
        Assert.Equal(25, corner[2]);
        // Inside-source pixel keeps original value.
        var inside = reg.GetAddress(1, 1);
        Assert.Equal(100, inside[0]);
    }

    [Fact]
    public void Embed_MirrorExtension_ReflectsSource()
    {
        var src = new VipsImage
        {
            Width = 2, Height = 1, Bands = 1, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                var addr = reg.GetAddress(0, 0); addr[0] = 10; addr[1] = 20;
                return 0;
            }
        };
        // Embed at (2, 0) → sources at output x=2 → src x=0 → 10; output x=3 → src x=1 → 20.
        // Output x=0 with mirror: src x = -2 → mirror → 1 → 20.
        // Output x=1 with mirror: src x = -1 → mirror → 0 → 10.
        var embedded = src.Embed(x: 2, y: 0, width: 4, height: 1, extend: VipsExtend.Mirror);
        using var reg = new VipsRegion(embedded);
        reg.Prepare(new VipsRect(0, 0, 4, 1));
        Assert.Equal(20, reg.GetAddress(0, 0)[0]);
        Assert.Equal(10, reg.GetAddress(1, 0)[0]);
        Assert.Equal(10, reg.GetAddress(2, 0)[0]);
        Assert.Equal(20, reg.GetAddress(3, 0)[0]);
    }

    // ---- Bandjoin ----

    [Fact]
    public void Bandjoin_RgbPlusAlpha_ProducesRgba()
    {
        var rgb = UCharImage(2, 2, 3, 100);
        var alpha = UCharImage(2, 2, 1, 128);
        var rgba = rgb.Bandjoin(alpha);
        Assert.Equal(4, rgba.Bands);
        using var reg = new VipsRegion(rgba);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        var px = reg.GetAddress(0, 0);
        Assert.Equal(100, px[0]); Assert.Equal(100, px[1]); Assert.Equal(100, px[2]);
        Assert.Equal(128, px[3]);
    }

    [Fact]
    public void Bandjoin_ThreeImages_ConcatenatesInOrder()
    {
        var a = UCharImage(2, 2, 1, 10);
        var b = UCharImage(2, 2, 1, 20);
        var c = UCharImage(2, 2, 1, 30);
        var joined = a.Bandjoin(b, c);
        Assert.Equal(3, joined.Bands);
        using var reg = new VipsRegion(joined);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        var px = reg.GetAddress(0, 0);
        Assert.Equal(10, px[0]); Assert.Equal(20, px[1]); Assert.Equal(30, px[2]);
    }

    [Fact]
    public void Bandjoin_Float_PreservesValues()
    {
        var a = FloatImage(2, 2, 1, (x, y, _) => 1.5f);
        var b = FloatImage(2, 2, 1, (x, y, _) => 2.5f);
        var joined = a.Bandjoin(b);
        Assert.Equal(VipsBandFormat.Float, joined.BandFormat);
        using var reg = new VipsRegion(joined);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        Assert.Equal(1.5f, ReadFloat(reg, 0, 0, 0, 2));
        Assert.Equal(2.5f, ReadFloat(reg, 0, 0, 1, 2));
    }

    [Fact]
    public void Bandjoin_SingleInput_PassesThrough()
    {
        var src = UCharImage(2, 2, 3, 100);
        var same = VipsImageOps.Bandjoin(src);
        Assert.Same(src, same);
    }
}
