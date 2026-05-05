using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Loaders;
using CosmoImage.Savers;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 176 — 16-bit PGM/PPM saver. Previously UShort inputs got cast
/// down to UChar before write; the spec defines binary PNM with
/// <c>maxval=65535</c> and big-endian 2-byte samples, which we now emit
/// natively. Tests round-trip 16-bit images through Save → Load and
/// verify pixel-exact recovery (or close to it after Y-luminance for
/// the multi-band → PGM path).
/// </summary>
public class Round176Tests
{
    private static VipsImage MakeUShort(int w, int h, int bands, Func<int, int, int, ushort> pixel)
        => new VipsImage
        {
            Width = w, Height = h, Bands = bands, BandFormat = VipsBandFormat.UShort,
            Interpretation = bands == 1 ? VipsInterpretation.BW : VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) =>
            {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        for (int c = 0; c < bands; c++)
                        {
                            ushort v = pixel(reg.Valid.Left + x, reg.Valid.Top + y, c);
                            addr[(x * bands + c) * 2 + 0] = (byte)v;
                            addr[(x * bands + c) * 2 + 1] = (byte)(v >> 8);
                        }
                    }
                }
                return 0;
            }
        };

    private static async Task<byte[]> SaveAsync(VipsImage img, VipsPnmVariant variant)
    {
        using var ms = new MemoryStream();
        var writer = PipeWriter.Create(ms);
        await VipsPnmSaver.SaveAsync(img, writer, variant);
        return ms.ToArray();
    }

    private static async Task<VipsImage> LoadAsync(byte[] bytes)
    {
        var src = new PipeVipsSource(PipeReader.Create(new MemoryStream(bytes)));
        var img = await VipsPnmLoader.LoadAsync(src);
        Assert.NotNull(img);
        return img!;
    }

    private static ushort ReadU16(VipsImage img, int x, int y, int band)
    {
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, img.Width, img.Height));
        var addr = reg.GetAddress(x, y);
        int o = (band * 2);
        // Calculate the correct offset accounting for x position.
        int pelOff = x * img.Bands * 2 + band * 2;
        var line = reg.GetAddress(0, y);
        return (ushort)(line[pelOff] | (line[pelOff + 1] << 8));
    }

    [Fact]
    public async Task Pgm16_Greyscale_RoundTripsExactly()
    {
        // Distinct value per pixel covering the full UShort range so any
        // narrowing or byte-order bug shows up.
        var src = MakeUShort(8, 4, 1, (x, y, c) => (ushort)((y * 8 + x) * 2048));
        var bytes = await SaveAsync(src, VipsPnmVariant.Pgm);

        // Verify the header tagged maxval=65535.
        string header = System.Text.Encoding.ASCII.GetString(bytes, 0, 32);
        Assert.StartsWith("P5\n8 4\n65535\n", header);

        var loaded = await LoadAsync(bytes);
        Assert.Equal(VipsBandFormat.UShort, loaded.BandFormat);
        Assert.Equal(8, loaded.Width);
        Assert.Equal(4, loaded.Height);

        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 8; x++)
            {
                ushort expected = (ushort)((y * 8 + x) * 2048);
                Assert.Equal(expected, ReadU16(loaded, x, y, 0));
            }
    }

    [Fact]
    public async Task Ppm16_Rgb_RoundTripsExactly()
    {
        // Distinct (r, g, b) per pixel, all spanning UShort range.
        var src = MakeUShort(4, 3, 3, (x, y, c) => (ushort)((x * 1000 + y * 100 + c * 10000) % 65535));
        var bytes = await SaveAsync(src, VipsPnmVariant.Ppm);

        string header = System.Text.Encoding.ASCII.GetString(bytes, 0, 32);
        Assert.StartsWith("P6\n4 3\n65535\n", header);

        var loaded = await LoadAsync(bytes);
        Assert.Equal(VipsBandFormat.UShort, loaded.BandFormat);
        Assert.Equal(3, loaded.Bands);

        for (int y = 0; y < 3; y++)
            for (int x = 0; x < 4; x++)
                for (int c = 0; c < 3; c++)
                {
                    ushort expected = (ushort)((x * 1000 + y * 100 + c * 10000) % 65535);
                    Assert.Equal(expected, ReadU16(loaded, x, y, c));
                }
    }

    [Fact]
    public async Task Pgm16_FromRgbInput_AppliesLuminanceWeighting()
    {
        // Pure red input (R=65535, G=0, B=0). Rec.709 luminance
        // = 0.2126 · 65535 ≈ 13935.
        var src = MakeUShort(2, 2, 3, (x, y, c) => c == 0 ? (ushort)65535 : (ushort)0);
        var bytes = await SaveAsync(src, VipsPnmVariant.Pgm);
        var loaded = await LoadAsync(bytes);

        Assert.Equal(VipsBandFormat.UShort, loaded.BandFormat);
        Assert.Equal(1, loaded.Bands);
        ushort luminance = ReadU16(loaded, 0, 0, 0);
        // 0.2126 · 65535 ≈ 13934. Allow ±2 for floating-point rounding.
        Assert.InRange(luminance, (ushort)13932, (ushort)13937);
    }

    [Fact]
    public async Task UCharInput_StillEmits8Bit()
    {
        // UChar input keeps the existing 8-bit P5/P6 path with maxval=255 —
        // verify we didn't regress that.
        var src = new VipsImage
        {
            Width = 4, Height = 2, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) =>
            {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++) addr[x] = (byte)((y * 4 + x) * 30);
                }
                return 0;
            }
        };
        var bytes = await SaveAsync(src, VipsPnmVariant.Pgm);
        string header = System.Text.Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 32));
        Assert.StartsWith("P5\n4 2\n255\n", header);
    }
}
