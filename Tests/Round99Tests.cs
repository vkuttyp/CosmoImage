using System;
using System.Collections.Generic;
using CosmoImage.Operations.Misc;
using Xunit;

namespace CosmoImage.Tests;

public class Round99Tests
{
    /// <summary>RGB image with a smooth horizontal greyscale gradient (256 unique colours per row).</summary>
    private static VipsImage Gradient256(int height = 4)
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

    /// <summary>A small 4×4 RGBA image with 16 distinct fully-saturated colours.</summary>
    private static VipsImage SixteenColors()
        => new VipsImage
        {
            Width = 4, Height = 4, Bands = 4, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? bb, ref bool stop) => {
                byte[] palette = new byte[] {
                    255,0,0,255,    0,255,0,255,   0,0,255,255,    255,255,0,255,
                    255,0,255,255,  0,255,255,255, 128,64,0,255,   200,100,50,255,
                    50,50,200,255,  100,200,100,255, 200,200,255,255, 50,50,50,255,
                    255,128,64,255, 64,255,128,255, 128,64,255,255, 255,255,255,255,
                };
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        int gx = reg.Valid.Left + x;
                        int gy = reg.Valid.Top + y;
                        int srcIdx = (gy * 4 + gx) * 4;
                        for (int k = 0; k < 4; k++) addr[x * 4 + k] = palette[srcIdx + k];
                    }
                }
                return 0;
            }
        };

    private static int CountUniqueColors(VipsImage img, int channels = 3)
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

    // ---- Reduces unique colours ----

    [Fact]
    public void Octree_ReducesUniqueColorsToTarget()
    {
        var src = Gradient256(2);
        var quantizer = new VipsOctreeQuantizer { Colors = 16 };
        var output = quantizer.Apply(src);
        int unique = CountUniqueColors(output);
        Assert.True(unique <= 16, $"expected ≤ 16 unique colours, got {unique}");
    }

    [Fact]
    public void Octree_8Colors_OutputUsesAtMost8()
    {
        var src = Gradient256(4);
        var output = new VipsOctreeQuantizer { Colors = 8 }.Apply(src);
        int unique = CountUniqueColors(output);
        Assert.True(unique <= 8, $"expected ≤ 8, got {unique}");
    }

    // ---- Preserves shape ----

    [Fact]
    public void Octree_PreservesDimensions()
    {
        var src = Gradient256(3);
        var output = new VipsOctreeQuantizer { Colors = 32 }.Apply(src);
        Assert.Equal(src.Width, output.Width);
        Assert.Equal(src.Height, output.Height);
        Assert.Equal(src.Bands, output.Bands);
    }

    // ---- Alpha preservation ----

    [Fact]
    public void Octree_PreservesAlphaChannel()
    {
        // Build an RGBA image where alpha varies per pixel; quantize to
        // 4 colours; alpha should round-trip identically.
        var src = SixteenColors();
        var output = new VipsOctreeQuantizer { Colors = 4 }.Apply(src);
        Assert.Equal(4, output.Bands);
        // All test pixels have alpha = 255 in the palette above.
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                var pel = ReadPel(output, x, y);
                Assert.Equal(255, pel[3]);
            }
    }

    // ---- Identity if N <= unique input ----

    [Fact]
    public void Octree_LargeColorBudget_PreservesNearlyAllInput()
    {
        // 16 unique input colours, target 256 → all should survive.
        var src = SixteenColors();
        var output = new VipsOctreeQuantizer { Colors = 256 }.Apply(src);
        int unique = CountUniqueColors(output);
        // We use 16 distinct colours; with 256 budget no reduction needed.
        Assert.Equal(16, unique);
    }

    // ---- Reduction is approximation, not random ----

    [Fact]
    public void Octree_PaletteColorsDerivedFromInput()
    {
        // After quantizing a greyscale gradient, output colours should
        // be greyish (equal R/G/B per pixel since input was R==G==B).
        var src = Gradient256(2);
        var output = new VipsOctreeQuantizer { Colors = 16 }.Apply(src);
        using var reg = new VipsRegion(output);
        reg.Prepare(new VipsRect(0, 0, 256, 2));
        for (int x = 0; x < 256; x++)
        {
            var addr = reg.GetAddress(x, 0);
            // R, G, B should be approximately equal (greyscale preserved).
            // Allow small deviation since octree leaves average over input bits.
            Assert.True(Math.Abs(addr[0] - addr[1]) <= 2);
            Assert.True(Math.Abs(addr[1] - addr[2]) <= 2);
        }
    }

    // ---- Pluggable via VipsImageOps.Quantize ----

    [Fact]
    public void Octree_PluggableViaQuantizeOverload()
    {
        var src = Gradient256(2);
        var quantizer = new VipsOctreeQuantizer { Colors = 16 };
        var output = VipsImageOps.Quantize(src, quantizer);
        Assert.True(CountUniqueColors(output) <= 16);
    }

    // ---- Validation ----

    [Fact]
    public void Octree_RejectsInvalidColors()
    {
        var src = Gradient256();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new VipsOctreeQuantizer { Colors = 1 }.Apply(src));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new VipsOctreeQuantizer { Colors = 257 }.Apply(src));
    }

    [Fact]
    public void Octree_AcceptsGreyscale()
    {
        // Greyscale (1-band) input — the quantizer now supports it via
        // an equal-population histogram partition, so it should produce
        // a 1-band output with no more than `Colors` unique values.
        var src = new VipsImage
        {
            Width = 256, Height = 4, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? bb, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                        addr[x] = (byte)(reg.Valid.Left + x);
                }
                return 0;
            }
        };
        var output = new VipsOctreeQuantizer { Colors = 4 }.Apply(src);
        Assert.Equal(1, output.Bands);
        var seen = new HashSet<byte>();
        using (var reg = new VipsRegion(output))
        {
            reg.Prepare(new VipsRect(0, 0, output.Width, output.Height));
            for (int y = 0; y < output.Height; y++)
            {
                var addr = reg.GetAddress(0, y);
                for (int x = 0; x < output.Width; x++)
                    seen.Add(addr[x]);
            }
        }
        Assert.True(seen.Count <= 4, $"expected ≤ 4 unique values, got {seen.Count}");
    }

    [Fact]
    public void Octree_RejectsBadBandCount()
    {
        // 2-band (greyscale + alpha) input — still unsupported.
        var twoBand = new VipsImage
        {
            Width = 4, Height = 4, Bands = 2, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? bb, ref bool stop) => 0,
        };
        Assert.Throws<ArgumentException>(() => new VipsOctreeQuantizer().Apply(twoBand));
    }

    [Fact]
    public void Octree_RejectsNonUCharFormat()
    {
        var floatImage = new VipsImage
        {
            Width = 4, Height = 4, Bands = 3, BandFormat = VipsBandFormat.Float,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? bb, ref bool stop) => 0,
        };
        Assert.Throws<ArgumentException>(() => new VipsOctreeQuantizer().Apply(floatImage));
    }
}
