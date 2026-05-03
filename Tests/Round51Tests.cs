using System;
using System.Buffers.Binary;
using Xunit;

namespace CosmoImage.Tests;

public class Round51Tests
{
    private static VipsImage UCharImage(int w, int h, int bands, byte v)
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

    private static VipsImage RgbaSolid(int w, int h, byte r, byte g, byte b, byte a)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 4, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? aa, object? bb, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        addr[x * 4 + 0] = r;
                        addr[x * 4 + 1] = g;
                        addr[x * 4 + 2] = b;
                        addr[x * 4 + 3] = a;
                    }
                }
                return 0;
            }
        };

    // ---- Opacity ----

    [Fact]
    public void Opacity_HalvesAlpha()
    {
        var src = RgbaSolid(2, 2, 100, 150, 200, 200);
        var faded = src.Opacity(0.5);
        using var reg = new VipsRegion(faded);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        var p = reg.GetAddress(0, 0);
        Assert.Equal(100, p[0]); // colour unchanged
        Assert.Equal(150, p[1]);
        Assert.Equal(200, p[2]);
        Assert.Equal(100, p[3]); // 200 * 0.5
    }

    [Fact]
    public void Opacity_NoAlpha_PassesThrough()
    {
        var src = UCharImage(2, 2, 3, 100);
        var same = src.Opacity(0.5);
        Assert.Same(src, same);
    }

    // ---- Threshold ----

    [Fact]
    public void Threshold_BinarisesAtCutoff()
    {
        var src = new VipsImage
        {
            Width = 4, Height = 1, Bands = 1, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                var addr = reg.GetAddress(0, 0);
                addr[0] = 50; addr[1] = 100; addr[2] = 150; addr[3] = 200;
                return 0;
            }
        };
        var t = src.Threshold(128);
        using var reg = new VipsRegion(t);
        reg.Prepare(new VipsRect(0, 0, 4, 1));
        Assert.Equal(0, reg.GetAddress(0, 0)[0]);
        Assert.Equal(0, reg.GetAddress(1, 0)[0]);
        Assert.Equal(255, reg.GetAddress(2, 0)[0]);
        Assert.Equal(255, reg.GetAddress(3, 0)[0]);
    }

    // ---- BlackWhite ----

    [Fact]
    public void BlackWhite_DesaturatesRGB()
    {
        // BlackWhite = Saturate(0).
        var src = UCharImage(2, 2, 3, 0);
        // Replace by a known colour: pure red → after BW should be grey at luminance.
        var red = new VipsImage
        {
            Width = 2, Height = 2, Bands = 3, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        addr[x * 3 + 0] = 255; addr[x * 3 + 1] = 0; addr[x * 3 + 2] = 0;
                    }
                }
                return 0;
            }
        };
        var bw = red.BlackWhite();
        using var reg = new VipsRegion(bw);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        var p = reg.GetAddress(0, 0);
        // Desaturated red ≈ 76 (BT.601 luminance). Same value across all bands.
        Assert.InRange(p[0], 50, 100);
        Assert.Equal(p[0], p[1]);
        Assert.Equal(p[1], p[2]);
    }

    // ---- Clear ----

    [Fact]
    public void Clear_FillsWithColour()
    {
        var src = UCharImage(4, 4, 3, 100);
        var c = src.Clear(50.0, 150.0, 200.0);
        using var reg = new VipsRegion(c);
        reg.Prepare(new VipsRect(0, 0, 4, 4));
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                var p = reg.GetAddress(x, y);
                Assert.Equal(50, p[0]);
                Assert.Equal(150, p[1]);
                Assert.Equal(200, p[2]);
            }
    }

    [Fact]
    public void Clear_RejectsBandMismatch()
    {
        var src = UCharImage(2, 2, 3, 100);
        Assert.Throws<Exception>(() => src.Clear(50.0)); // 1 colour for 3-band
    }

    // ---- ColorMatrix ----

    [Fact]
    public void ColorMatrix_Identity_PassesThrough()
    {
        var src = RgbaSolid(2, 2, 100, 150, 200, 250);
        var identity = new double[,]
        {
            { 1, 0, 0, 0, 0 },
            { 0, 1, 0, 0, 0 },
            { 0, 0, 1, 0, 0 },
            { 0, 0, 0, 1, 0 },
        };
        var result = src.ColorMatrix(identity);
        using var reg = new VipsRegion(result);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        var p = reg.GetAddress(0, 0);
        Assert.Equal(100, p[0]);
        Assert.Equal(150, p[1]);
        Assert.Equal(200, p[2]);
        Assert.Equal(250, p[3]);
    }

    [Fact]
    public void ColorMatrix_SwapsRedAndBlue()
    {
        var src = RgbaSolid(2, 2, 100, 0, 200, 255);
        var swap = new double[,]
        {
            { 0, 0, 1, 0, 0 }, // out R = in B
            { 0, 1, 0, 0, 0 },
            { 1, 0, 0, 0, 0 }, // out B = in R
            { 0, 0, 0, 1, 0 },
        };
        var result = src.ColorMatrix(swap);
        using var reg = new VipsRegion(result);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        var p = reg.GetAddress(0, 0);
        Assert.Equal(200, p[0]);
        Assert.Equal(0, p[1]);
        Assert.Equal(100, p[2]);
        Assert.Equal(255, p[3]);
    }

    [Fact]
    public void ColorMatrix_TranslationColumn_AddsBrightness()
    {
        var src = RgbaSolid(2, 2, 100, 100, 100, 200);
        // Identity + 30 to RGB.
        var brighten = new double[,]
        {
            { 1, 0, 0, 0, 30 },
            { 0, 1, 0, 0, 30 },
            { 0, 0, 1, 0, 30 },
            { 0, 0, 0, 1, 0 },
        };
        var result = src.ColorMatrix(brighten);
        using var reg = new VipsRegion(result);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        var p = reg.GetAddress(0, 0);
        Assert.Equal(130, p[0]);
        Assert.Equal(130, p[1]);
        Assert.Equal(130, p[2]);
        Assert.Equal(200, p[3]); // alpha untouched
    }

    // ---- Skew ----

    [Fact]
    public void Skew_ZeroDegrees_PassesThrough()
    {
        var src = UCharImage(8, 8, 1, 100);
        var s = src.Skew(0, 0);
        using var reg = new VipsRegion(s);
        reg.Prepare(new VipsRect(0, 0, 8, 8));
        Assert.Equal(100, reg.GetAddress(4, 4)[0]);
    }

    [Fact]
    public void Skew_NonZero_ProducesValidImage()
    {
        var src = UCharImage(16, 16, 1, 200);
        var s = src.Skew(10, 0);
        Assert.Equal(16, s.Width);
        Assert.Equal(16, s.Height);
        // Centre pixel should still be 200 (interior unchanged for solid image).
        using var reg = new VipsRegion(s);
        reg.Prepare(new VipsRect(0, 0, 16, 16));
        Assert.Equal(200, reg.GetAddress(8, 8)[0]);
    }
}
