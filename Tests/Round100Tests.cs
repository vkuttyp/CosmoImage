using System;
using System.Collections.Generic;
using System.Linq;
using CosmoImage.Operations.Misc;
using Xunit;

namespace CosmoImage.Tests;

public class Round100Tests
{
    /// <summary>RGB image with a smooth horizontal greyscale gradient.</summary>
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

    /// <summary>Solid RGB image of the given colour.</summary>
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

    // ---- Output uses palette colours ----

    [Fact]
    public void Palette_OutputColorsAllFromPalette()
    {
        var palette = new List<byte[]> {
            new byte[] { 0, 0, 0 },
            new byte[] { 255, 255, 255 },
            new byte[] { 128, 128, 128 },
        };
        var quantizer = new VipsPaletteQuantizer(palette);
        var output = quantizer.Apply(Gradient256(2));

        var paletteSet = new HashSet<(int, int, int)>(
            palette.Select(p => ((int)p[0], (int)p[1], (int)p[2])));

        using var reg = new VipsRegion(output);
        reg.Prepare(new VipsRect(0, 0, 256, 2));
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 256; x++)
            {
                var addr = reg.GetAddress(x, y);
                Assert.Contains(((int)addr[0], (int)addr[1], (int)addr[2]), paletteSet);
            }
    }

    // ---- Nearest-entry mapping ----

    [Fact]
    public void Palette_PicksClosestEntry()
    {
        // Palette = pure black / pure red. Pure red input → pure red output.
        var palette = new List<byte[]> {
            new byte[] { 0, 0, 0 },
            new byte[] { 255, 0, 0 },
        };
        var output = new VipsPaletteQuantizer(palette).Apply(Solid(4, 4, 200, 30, 30));
        // (200, 30, 30) is closer to (255, 0, 0) than (0, 0, 0).
        var pel = ReadPel(output, 0, 0);
        Assert.Equal(255, pel[0]);
        Assert.Equal(0, pel[1]);
        Assert.Equal(0, pel[2]);
    }

    [Fact]
    public void Palette_ExactMatchPreserved()
    {
        var palette = new List<byte[]> {
            new byte[] { 0, 0, 0 },
            new byte[] { 100, 50, 200 },
            new byte[] { 255, 255, 255 },
        };
        var output = new VipsPaletteQuantizer(palette).Apply(Solid(4, 4, 100, 50, 200));
        var pel = ReadPel(output, 0, 0);
        Assert.Equal(100, pel[0]);
        Assert.Equal(50, pel[1]);
        Assert.Equal(200, pel[2]);
    }

    // ---- Built-in WebSafe palette ----

    [Fact]
    public void WebSafe_Has216Colors()
    {
        Assert.Equal(216, VipsPaletteQuantizer.WebSafe.Palette.Count);
    }

    [Fact]
    public void WebSafe_AllChannelsAreFromSafeLevels()
    {
        var safeLevels = new HashSet<byte> { 0, 51, 102, 153, 204, 255 };
        foreach (var entry in VipsPaletteQuantizer.WebSafe.Palette)
        {
            Assert.Contains(entry[0], safeLevels);
            Assert.Contains(entry[1], safeLevels);
            Assert.Contains(entry[2], safeLevels);
        }
    }

    [Fact]
    public void WebSafe_QuantizesGradient()
    {
        var output = VipsPaletteQuantizer.WebSafe.Apply(Gradient256(2));
        // Greyscale gradient (R=G=B) → quantized to greys at safe levels.
        // Distinct colours should be ≤ 6 (one per level along the diagonal).
        int unique = CountUniqueColors(output);
        Assert.True(unique <= 6, $"greyscale via WebSafe should map to ≤ 6 levels, got {unique}");
    }

    // ---- Alpha preservation ----

    [Fact]
    public void Palette_PreservesAlpha()
    {
        var palette = new List<byte[]> {
            new byte[] { 0, 0, 0 }, new byte[] { 255, 255, 255 },
        };
        var src = Solid(4, 4, 100, 100, 100, bands: 4);
        var output = new VipsPaletteQuantizer(palette).Apply(src);
        Assert.Equal(4, output.Bands);
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
                Assert.Equal(255, ReadPel(output, x, y)[3]);
    }

    // ---- Validation ----

    [Fact]
    public void Palette_RejectsEmpty()
    {
        Assert.Throws<ArgumentException>(() =>
            new VipsPaletteQuantizer(new List<byte[]>()));
    }

    [Fact]
    public void Palette_RejectsTooLarge()
    {
        var huge = new List<byte[]>(257);
        for (int i = 0; i < 257; i++) huge.Add(new byte[] { 0, 0, 0 });
        Assert.Throws<ArgumentException>(() => new VipsPaletteQuantizer(huge));
    }

    [Fact]
    public void Palette_RejectsBadEntryShape()
    {
        Assert.Throws<ArgumentException>(() => new VipsPaletteQuantizer(
            new List<byte[]> { new byte[] { 1, 2 } }));   // 2 channels, expected 3
        Assert.Throws<ArgumentException>(() => new VipsPaletteQuantizer(
            new List<byte[]> { new byte[] { 1, 2, 3, 4 } }));  // 4 channels
    }

    [Fact]
    public void Palette_RejectsNullArg()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new VipsPaletteQuantizer((IReadOnlyList<byte[]>)null!));
    }

    [Fact]
    public void Palette_RejectsBadBandCount()
    {
        var oneBand = new VipsImage
        {
            Width = 4, Height = 4, Bands = 1, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? bb, ref bool stop) => 0,
        };
        var palette = new List<byte[]> { new byte[] { 0, 0, 0 } };
        Assert.Throws<ArgumentException>(() => new VipsPaletteQuantizer(palette).Apply(oneBand));
    }

    // ---- Pluggable via VipsImageOps.Quantize ----

    [Fact]
    public void Palette_PluggableViaQuantizeOverload()
    {
        var output = VipsImageOps.Quantize(Solid(4, 4, 100, 50, 200),
            VipsPaletteQuantizer.WebSafe);
        // (100, 50, 200) → web-safe nearest is (102, 51, 204).
        var pel = ReadPel(output, 0, 0);
        Assert.Equal(102, pel[0]);
        Assert.Equal(51, pel[1]);
        Assert.Equal(204, pel[2]);
    }
}
