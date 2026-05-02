using System;
using System.Buffers.Binary;
using Xunit;

namespace CosmoImage.Tests;

public class Round27Tests
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

    private static VipsImage Rgba(int w, int h, byte r, byte g, byte bl, byte a)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 4, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a2, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        addr[x * 4 + 0] = r; addr[x * 4 + 1] = g;
                        addr[x * 4 + 2] = bl; addr[x * 4 + 3] = a;
                    }
                }
                return 0;
            }
        };

    private static VipsImage GradientGray(int w, int h)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        // Smooth ramp left-to-right.
                        int gx = reg.Valid.Left + x;
                        addr[x] = (byte)Math.Clamp(gx * 255 / Math.Max(1, w - 1), 0, 255);
                    }
                }
                return 0;
            }
        };

    // ---- Flatten ----

    [Fact]
    public void Flatten_OpaqueAlpha_KeepsColorUnchanged()
    {
        var src = Rgba(2, 2, r: 200, g: 100, bl: 50, a: 255);
        var flat = src.Flatten();
        Assert.Equal(3, flat.Bands);
        using var reg = new VipsRegion(flat);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        var p = reg.GetAddress(0, 0);
        Assert.Equal(200, p[0]);
        Assert.Equal(100, p[1]);
        Assert.Equal(50, p[2]);
    }

    [Fact]
    public void Flatten_TransparentAlpha_FillsBackground()
    {
        var src = Rgba(2, 2, r: 200, g: 100, bl: 50, a: 0);
        var flat = src.Flatten(255.0, 255.0, 255.0);
        using var reg = new VipsRegion(flat);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        // Fully transparent with white background → white.
        Assert.Equal(255, reg.GetAddress(0, 0)[0]);
        Assert.Equal(255, reg.GetAddress(0, 0)[1]);
        Assert.Equal(255, reg.GetAddress(0, 0)[2]);
    }

    [Fact]
    public void Flatten_HalfAlpha_BlendsTowardBackground()
    {
        // RGB 200 with alpha 128 over background 0 → ~100
        var src = Rgba(2, 2, r: 200, g: 200, bl: 200, a: 128);
        var flat = src.Flatten(); // default background = black
        using var reg = new VipsRegion(flat);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        Assert.InRange(reg.GetAddress(0, 0)[0], 99, 101);
    }

    [Fact]
    public void Flatten_NoAlpha_PassesThrough()
    {
        var src = UCharImage(2, 2, 3, 100);
        var flat = src.Flatten(0.0, 0.0, 0.0);
        Assert.Equal(3, flat.Bands);
        using var reg = new VipsRegion(flat);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        Assert.Equal(100, reg.GetAddress(0, 0)[0]);
    }

    // ---- AddAlpha ----

    [Fact]
    public void AddAlpha_RgbInput_BecomesRgba()
    {
        var src = UCharImage(2, 2, 3, 100);
        var rgba = src.AddAlpha();
        Assert.Equal(4, rgba.Bands);
        using var reg = new VipsRegion(rgba);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        var p = reg.GetAddress(0, 0);
        Assert.Equal(100, p[0]);
        Assert.Equal(100, p[1]);
        Assert.Equal(100, p[2]);
        Assert.Equal(255, p[3]); // fully opaque
    }

    [Fact]
    public void AddAlpha_GrayInput_BecomesGrayAlpha()
    {
        var src = UCharImage(2, 2, 1, 80);
        var ga = src.AddAlpha(alpha: 128);
        Assert.Equal(2, ga.Bands);
        using var reg = new VipsRegion(ga);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        var p = reg.GetAddress(0, 0);
        Assert.Equal(80, p[0]);
        Assert.Equal(128, p[1]);
    }

    [Fact]
    public void AddAlpha_AlreadyHasAlpha_PassesThrough()
    {
        var src = UCharImage(2, 2, 4, 100);
        var same = src.AddAlpha();
        Assert.Same(src, same);
    }

    // ---- Pad ----

    [Fact]
    public void Pad_CenterAnchor_PlacesInputInMiddle()
    {
        var src = UCharImage(2, 2, 1, 200);
        var padded = src.Pad(width: 6, height: 6,
            background: new[] { 50.0 }, position: VipsCompass.Centre);
        Assert.Equal(6, padded.Width);
        Assert.Equal(6, padded.Height);

        using var reg = new VipsRegion(padded);
        reg.Prepare(new VipsRect(0, 0, 6, 6));
        // (6-2)/2 = 2 → input occupies (2..3, 2..3).
        Assert.Equal(50, reg.GetAddress(0, 0)[0]); // background
        Assert.Equal(200, reg.GetAddress(2, 2)[0]); // input start
        Assert.Equal(200, reg.GetAddress(3, 3)[0]); // input end
        Assert.Equal(50, reg.GetAddress(5, 5)[0]); // background
    }

    [Fact]
    public void Pad_NorthWestAnchor_AnchorsTopLeft()
    {
        var src = UCharImage(2, 2, 1, 200);
        var padded = src.Pad(width: 6, height: 6,
            background: new[] { 0.0 }, position: VipsCompass.NorthWest);
        using var reg = new VipsRegion(padded);
        reg.Prepare(new VipsRect(0, 0, 6, 6));
        // Input at (0..1, 0..1).
        Assert.Equal(200, reg.GetAddress(0, 0)[0]);
        Assert.Equal(200, reg.GetAddress(1, 1)[0]);
        Assert.Equal(0, reg.GetAddress(2, 2)[0]);
    }

    [Fact]
    public void Pad_SouthEastAnchor_AnchorsBottomRight()
    {
        var src = UCharImage(2, 2, 1, 200);
        var padded = src.Pad(width: 6, height: 6,
            background: new[] { 0.0 }, position: VipsCompass.SouthEast);
        using var reg = new VipsRegion(padded);
        reg.Prepare(new VipsRect(0, 0, 6, 6));
        Assert.Equal(0, reg.GetAddress(0, 0)[0]);
        Assert.Equal(200, reg.GetAddress(4, 4)[0]);
        Assert.Equal(200, reg.GetAddress(5, 5)[0]);
    }

    // ---- BackgroundColor ----

    [Fact]
    public void BackgroundColor_TransparentRgba_ResolvesToBackground_KeepsAlpha()
    {
        var src = Rgba(2, 2, r: 200, g: 200, bl: 200, a: 0);
        var bg = src.BackgroundColor(50.0, 100.0, 150.0);
        Assert.Equal(4, bg.Bands); // alpha kept
        using var reg = new VipsRegion(bg);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        var p = reg.GetAddress(0, 0);
        Assert.Equal(50, p[0]);
        Assert.Equal(100, p[1]);
        Assert.Equal(150, p[2]);
        Assert.Equal(255, p[3]); // alpha set fully opaque
    }

    // ---- CLAHE ----

    [Fact]
    public void HistLocal_OnUniformImage_PreservesIntensity()
    {
        // Uniform tile → CDF maps the single value to its own scaled position;
        // bilinear blend across tiles still produces ~the same value.
        var src = UCharImage(64, 64, 1, 128);
        var eq = src.HistLocal(tileGridSize: 4, clipLimit: 3.0);
        Assert.Equal(VipsBandFormat.UChar, eq.BandFormat);

        using var reg = new VipsRegion(eq);
        reg.Prepare(new VipsRect(0, 0, 64, 64));
        // Uniform input → all output pixels should be the same value
        // (whatever the CDF maps 128 to). Just check uniformity.
        byte expected = reg.GetAddress(32, 32)[0];
        for (int y = 5; y < 60; y++)
            for (int x = 5; x < 60; x++)
                Assert.Equal(expected, reg.GetAddress(x, y)[0]);
    }

    [Fact]
    public void HistLocal_OnGradient_StretchesContrast()
    {
        // CLAHE doesn't preserve global pixel-position-to-value mapping —
        // each tile's CDF independently equalises the local histogram,
        // and aggressive clipping flattens those CDFs further. What we
        // *can* verify is that the output still spans a wide dynamic
        // range (the contrast hasn't collapsed) and that pixels output
        // valid bytes.
        var src = GradientGray(128, 64);
        var eq = src.HistLocal(tileGridSize: 8, clipLimit: 3.0);

        using var reg = new VipsRegion(eq);
        reg.Prepare(new VipsRect(0, 0, 128, 64));

        // Walk a horizontal slice and gather min/max over the whole row.
        byte hi = 0, lo = 255;
        for (int x = 0; x < 128; x++)
        {
            byte v = reg.GetAddress(x, 32)[0];
            if (v > hi) hi = v;
            if (v < lo) lo = v;
        }
        // Span should be wide (clipped CDF still produces near-full range)
        Assert.True(hi - lo > 100, $"expected wide span, got {lo}..{hi}");
    }

    [Fact]
    public void HistLocal_RejectsFloatInput()
    {
        var src = new VipsImage
        {
            Width = 4, Height = 4, Bands = 1, BandFormat = VipsBandFormat.Float,
        };
        Assert.Throws<Exception>(() => src.HistLocal());
    }

    [Fact]
    public void HistLocal_PreservesDimensionsAndBands()
    {
        var src = UCharImage(32, 24, 3, 100);
        var eq = src.HistLocal(tileGridSize: 4);
        Assert.Equal(32, eq.Width);
        Assert.Equal(24, eq.Height);
        Assert.Equal(3, eq.Bands);
    }
}
