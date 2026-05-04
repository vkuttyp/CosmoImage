using System;
using System.Collections.Generic;
using System.Linq;
using CosmoImage.Operations.Misc;
using Xunit;

namespace CosmoImage.Tests;

public class Round101Tests
{
    /// <summary>Smooth horizontal greyscale gradient (R=G=B varies 0..255).</summary>
    private static VipsImage Gradient256(int height = 8)
        => new VipsImage
        {
            Width = 256, Height = height, Bands = 3, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? bb, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        byte v = (byte)(reg.Valid.Left + x);
                        addr[x * 3 + 0] = v; addr[x * 3 + 1] = v; addr[x * 3 + 2] = v;
                    }
                }
                return 0;
            }
        };

    private static VipsImage Solid(int w, int h, byte r, byte g, byte b, int bands = 3)
        => new VipsImage
        {
            Width = w, Height = h, Bands = bands, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? bb, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        addr[x * bands + 0] = r;
                        addr[x * bands + 1] = g;
                        addr[x * bands + 2] = b;
                        if (bands == 4) addr[x * bands + 3] = 255;
                    }
                }
                return 0;
            }
        };

    private static int CountUniqueColors(VipsImage img)
    {
        var seen = new HashSet<(int, int, int)>();
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, img.Width, img.Height));
        for (int y = 0; y < img.Height; y++)
        {
            var addr = reg.GetAddress(0, y);
            for (int x = 0; x < img.Width; x++)
                seen.Add((addr[x * img.Bands], addr[x * img.Bands + 1], addr[x * img.Bands + 2]));
        }
        return seen.Count;
    }

    private static byte[] ReadPel(VipsImage img, int x, int y)
    {
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, img.Width, img.Height));
        return reg.GetAddress(x, y).Slice(0, img.Bands).ToArray();
    }

    /// <summary>Count distinct (R,G,B) triples within a single row of an image.</summary>
    private static int CountColorsInRow(VipsImage img, int y)
    {
        var seen = new HashSet<(int, int, int)>();
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, img.Width, img.Height));
        var addr = reg.GetAddress(0, y);
        for (int x = 0; x < img.Width; x++)
            seen.Add((addr[x * img.Bands], addr[x * img.Bands + 1], addr[x * img.Bands + 2]));
        return seen.Count;
    }

    // ---- Output stays within palette ----

    [Fact]
    public void Dither_OutputColorsAllFromInnerPalette()
    {
        var src = Gradient256(4);
        var inner = new VipsOctreeQuantizer { Colors = 8 };
        var ditherer = new VipsFloydSteinbergQuantizer(inner);

        // The dithered output must use only colours from the inner's
        // quantized output (the palette).
        var innerOut = inner.Apply(src);
        var innerPalette = new HashSet<(int, int, int)>();
        using (var reg = new VipsRegion(innerOut))
        {
            reg.Prepare(new VipsRect(0, 0, 256, 4));
            for (int y = 0; y < 4; y++)
            {
                var a = reg.GetAddress(0, y);
                for (int x = 0; x < 256; x++)
                    innerPalette.Add((a[x * 3], a[x * 3 + 1], a[x * 3 + 2]));
            }
        }

        var dithered = ditherer.Apply(src);
        using (var reg = new VipsRegion(dithered))
        {
            reg.Prepare(new VipsRect(0, 0, 256, 4));
            for (int y = 0; y < 4; y++)
            {
                var a = reg.GetAddress(0, y);
                for (int x = 0; x < 256; x++)
                    Assert.Contains(((int)a[x * 3], (int)a[x * 3 + 1], (int)a[x * 3 + 2]), innerPalette);
            }
        }
    }

    // ---- Dither produces more distinct colours per row than naive ----

    [Fact]
    public void Dither_GradientHasMoreColorsPerRowThanCrispQuantization()
    {
        // Crisp octree on a smooth gradient produces piecewise-constant
        // bands per row. Floyd-Steinberg shuffles error around so adjacent
        // pixels diverge — within a single row, we'll see MORE distinct
        // (R,G,B) triples chosen from the palette.
        var src = Gradient256(2);
        var inner = new VipsOctreeQuantizer { Colors = 8 };
        var crispOut = inner.Apply(src);
        var ditherOut = new VipsFloydSteinbergQuantizer(inner).Apply(src);

        int crispRowColors = CountColorsInRow(crispOut, 0);
        int ditherRowColors = CountColorsInRow(ditherOut, 0);
        Assert.True(ditherRowColors >= crispRowColors,
            $"dither row colours ({ditherRowColors}) should be ≥ crisp ({crispRowColors})");
    }

    // ---- Solid colour stays solid ----

    [Fact]
    public void Dither_SolidInputProducesSolidOutput()
    {
        // A solid-colour input has no error to diffuse — every pixel
        // should map to the single palette entry the inner picks.
        var src = Solid(8, 8, 100, 100, 100);
        var ditherer = new VipsFloydSteinbergQuantizer(new VipsOctreeQuantizer { Colors = 4 });
        var output = ditherer.Apply(src);
        // All pixels equal.
        var first = ReadPel(output, 0, 0);
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                var pel = ReadPel(output, x, y);
                Assert.Equal(first[0], pel[0]);
                Assert.Equal(first[1], pel[1]);
                Assert.Equal(first[2], pel[2]);
            }
    }

    // ---- Composition with each inner quantizer ----

    [Fact]
    public void Dither_WrapsPaletteQuantizer()
    {
        var palette = new List<byte[]> {
            new byte[] { 0, 0, 0 },
            new byte[] { 255, 255, 255 },
        };
        var ditherer = new VipsFloydSteinbergQuantizer(new VipsPaletteQuantizer(palette));
        var output = ditherer.Apply(Gradient256(4));
        // Output must use only black or white.
        Assert.Equal(2, CountUniqueColors(output));
    }

    [Fact]
    public void Dither_WrapsOctreeQuantizer()
    {
        var ditherer = new VipsFloydSteinbergQuantizer(new VipsOctreeQuantizer { Colors = 16 });
        var output = ditherer.Apply(Gradient256(4));
        Assert.True(CountUniqueColors(output) <= 16);
    }

    // ---- Alpha preservation ----

    [Fact]
    public void Dither_PreservesAlpha()
    {
        var src = Solid(8, 8, 80, 80, 80, bands: 4);
        var ditherer = new VipsFloydSteinbergQuantizer(new VipsOctreeQuantizer { Colors = 4 });
        var output = ditherer.Apply(src);
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                Assert.Equal(255, ReadPel(output, x, y)[3]);
    }

    // ---- Pluggable via VipsImageOps.Quantize ----

    [Fact]
    public void Dither_PluggableViaQuantizeOverload()
    {
        var src = Gradient256(2);
        var ditherer = new VipsFloydSteinbergQuantizer(
            new VipsPaletteQuantizer(new List<byte[]> {
                new byte[] { 0, 0, 0 },
                new byte[] { 255, 255, 255 },
            }));
        var output = VipsImageOps.Quantize(src, ditherer);
        Assert.Equal(2, CountUniqueColors(output));
    }

    // ---- Validation ----

    [Fact]
    public void Dither_NullInner_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new VipsFloydSteinbergQuantizer(null!));
    }

    [Fact]
    public void Dither_BadBandCount_Throws()
    {
        var oneBand = new VipsImage
        {
            Width = 4, Height = 4, Bands = 1, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? bb, ref bool stop) => 0,
        };
        var ditherer = new VipsFloydSteinbergQuantizer(new VipsOctreeQuantizer());
        Assert.Throws<ArgumentException>(() => ditherer.Apply(oneBand));
    }

    // ---- Average colour roughly preserved ----

    [Fact]
    public void Dither_PreservesAverageColor()
    {
        // Floyd-Steinberg's defining property: error diffusion conserves
        // total brightness. Average colour of dither output should be
        // close to the average of the input.
        var src = Gradient256(4);
        var ditherer = new VipsFloydSteinbergQuantizer(new VipsOctreeQuantizer { Colors = 4 });
        var output = ditherer.Apply(src);

        long origR = 0, origG = 0, origB = 0;
        long ditherR = 0, ditherG = 0, ditherB = 0;
        using (var rIn = new VipsRegion(src))
        using (var rOut = new VipsRegion(output))
        {
            rIn.Prepare(new VipsRect(0, 0, 256, 4));
            rOut.Prepare(new VipsRect(0, 0, 256, 4));
            for (int y = 0; y < 4; y++)
            {
                var aIn = rIn.GetAddress(0, y);
                var aOut = rOut.GetAddress(0, y);
                for (int x = 0; x < 256; x++)
                {
                    origR += aIn[x * 3]; origG += aIn[x * 3 + 1]; origB += aIn[x * 3 + 2];
                    ditherR += aOut[x * 3]; ditherG += aOut[x * 3 + 1]; ditherB += aOut[x * 3 + 2];
                }
            }
        }
        long n = 256 * 4;
        double diff = Math.Abs((double)(ditherR - origR)) / n;
        // Within ±10 average grey levels — allows for end-of-image edge effects.
        Assert.True(diff < 10, $"average diff = {diff:F2}");
    }
}
