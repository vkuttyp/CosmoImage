using System;
using Xunit;

namespace CosmoImage.Tests;

public class Round55Tests
{
    private static VipsImage UCharBands(int w, int h, byte[] bandValues)
        => new VipsImage
        {
            Width = w, Height = h, Bands = bandValues.Length, BandFormat = VipsBandFormat.UChar,
            Interpretation = bandValues.Length switch { 1 or 2 => VipsInterpretation.BW, _ => VipsInterpretation.RGB },
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                        for (int bnd = 0; bnd < bandValues.Length; bnd++)
                            addr[x * bandValues.Length + bnd] = bandValues[bnd];
                }
                return 0;
            }
        };

    private static byte[] ReadPel(VipsImage img, int x, int y)
    {
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, img.Width, img.Height));
        var addr = reg.GetAddress(x, y);
        return addr.Slice(0, img.Bands).ToArray();
    }

    // ---- ToL8 ----

    [Fact]
    public void ToL8_FromRgb_GivesBT601Luminance()
    {
        // Pure red (255, 0, 0) → Y = 0.299·255 ≈ 76.
        var src = UCharBands(2, 2, new byte[] { 255, 0, 0 });
        var l = src.ToL8();
        Assert.Equal(1, l.Bands);
        var p = ReadPel(l, 0, 0);
        Assert.InRange(p[0], 75, 77);
    }

    [Fact]
    public void ToL8_FromRgba_DropsAlpha()
    {
        var src = UCharBands(2, 2, new byte[] { 100, 100, 100, 50 });
        var l = src.ToL8();
        var p = ReadPel(l, 0, 0);
        Assert.Equal(100, p[0]); // luminance of grey 100 = 100; alpha gone
    }

    [Fact]
    public void ToL8_FromL8_PassesThrough()
    {
        var src = UCharBands(2, 2, new byte[] { 99 });
        var l = src.ToL8();
        Assert.Equal(99, ReadPel(l, 0, 0)[0]);
    }

    // ---- ToLa16 ----

    [Fact]
    public void ToLa16_FromRgb_AddsOpaqueAlpha()
    {
        var src = UCharBands(2, 2, new byte[] { 255, 0, 0 });
        var la = src.ToLa16();
        Assert.Equal(2, la.Bands);
        var p = ReadPel(la, 0, 0);
        Assert.InRange(p[0], 75, 77); // luminance
        Assert.Equal(255, p[1]);      // opaque
    }

    [Fact]
    public void ToLa16_FromRgba_PreservesAlpha()
    {
        var src = UCharBands(2, 2, new byte[] { 200, 200, 200, 128 });
        var la = src.ToLa16();
        var p = ReadPel(la, 0, 0);
        Assert.Equal(200, p[0]);
        Assert.Equal(128, p[1]);
    }

    // ---- ToRgb24 ----

    [Fact]
    public void ToRgb24_FromL8_ReplicatesLuminance()
    {
        var src = UCharBands(2, 2, new byte[] { 130 });
        var rgb = src.ToRgb24();
        Assert.Equal(3, rgb.Bands);
        var p = ReadPel(rgb, 0, 0);
        Assert.Equal(130, p[0]);
        Assert.Equal(130, p[1]);
        Assert.Equal(130, p[2]);
    }

    [Fact]
    public void ToRgb24_FromRgba_DropsAlpha()
    {
        var src = UCharBands(2, 2, new byte[] { 50, 100, 150, 200 });
        var rgb = src.ToRgb24();
        var p = ReadPel(rgb, 0, 0);
        Assert.Equal(50, p[0]); Assert.Equal(100, p[1]); Assert.Equal(150, p[2]);
    }

    // ---- ToRgba32 ----

    [Fact]
    public void ToRgba32_FromRgb_AddsOpaqueAlpha()
    {
        var src = UCharBands(2, 2, new byte[] { 50, 100, 150 });
        var rgba = src.ToRgba32();
        Assert.Equal(4, rgba.Bands);
        var p = ReadPel(rgba, 0, 0);
        Assert.Equal(50, p[0]); Assert.Equal(100, p[1]); Assert.Equal(150, p[2]); Assert.Equal(255, p[3]);
    }

    [Fact]
    public void ToRgba32_FromL8_ReplicatesAndAddsAlpha()
    {
        var src = UCharBands(2, 2, new byte[] { 200 });
        var rgba = src.ToRgba32();
        var p = ReadPel(rgba, 0, 0);
        Assert.Equal(200, p[0]); Assert.Equal(200, p[1]); Assert.Equal(200, p[2]); Assert.Equal(255, p[3]);
    }

    // ---- SwapRb ----

    [Fact]
    public void SwapRb_Rgb_BecomesBgr()
    {
        var src = UCharBands(2, 2, new byte[] { 10, 20, 30 });
        var bgr = src.SwapRb();
        var p = ReadPel(bgr, 0, 0);
        Assert.Equal(30, p[0]); Assert.Equal(20, p[1]); Assert.Equal(10, p[2]);
    }

    [Fact]
    public void SwapRb_Rgba_BecomesBgraPreservesAlpha()
    {
        var src = UCharBands(2, 2, new byte[] { 10, 20, 30, 200 });
        var bgra = src.SwapRb();
        var p = ReadPel(bgra, 0, 0);
        Assert.Equal(30, p[0]); Assert.Equal(20, p[1]); Assert.Equal(10, p[2]); Assert.Equal(200, p[3]);
    }

    // ---- ToArgb ----

    [Fact]
    public void ToArgb_Rgba_BecomesArgb()
    {
        var src = UCharBands(2, 2, new byte[] { 10, 20, 30, 200 });
        var argb = src.ToArgb();
        var p = ReadPel(argb, 0, 0);
        Assert.Equal(200, p[0]); // alpha first
        Assert.Equal(10, p[1]);
        Assert.Equal(20, p[2]);
        Assert.Equal(30, p[3]);
    }
}
