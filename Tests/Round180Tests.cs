using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Loaders;
using CosmoImage.Savers;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 180 — pure-C# PAM (P7) saver. Closes the last loader-side
/// Magick fallback in PNM. The PAM loader was already pure-C# (round
/// 145, <see cref="VipsPnmLoader.LoadAsync"/> calls the in-file
/// <c>ParsePam</c>); this round makes the saver match.
/// Round-trip tests verify pixel-exact recovery across band counts
/// (1/2/3/4) and bit depths (UChar / UShort).
/// </summary>
public class Round180Tests
{
    private static VipsImage MakeUChar(int w, int h, int bands, Func<int, int, int, byte> pixel)
        => new VipsImage
        {
            Width = w, Height = h, Bands = bands, BandFormat = VipsBandFormat.UChar,
            Interpretation = bands == 1 || bands == 2 ? VipsInterpretation.BW : VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) =>
            {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                        for (int c = 0; c < bands; c++)
                            addr[x * bands + c] = pixel(reg.Valid.Left + x, reg.Valid.Top + y, c);
                }
                return 0;
            }
        };

    private static VipsImage MakeUShort(int w, int h, int bands, Func<int, int, int, ushort> pixel)
        => new VipsImage
        {
            Width = w, Height = h, Bands = bands, BandFormat = VipsBandFormat.UShort,
            Interpretation = bands == 1 || bands == 2 ? VipsInterpretation.BW : VipsInterpretation.RGB,
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

    private static byte[] ReadAll(VipsImage img)
    {
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, img.Width, img.Height));
        int rowBytes = img.Width * img.Bands * (img.BandFormat == VipsBandFormat.UShort ? 2 : 1);
        var bytes = new byte[rowBytes * img.Height];
        for (int y = 0; y < img.Height; y++)
            reg.GetAddress(0, y).Slice(0, rowBytes).CopyTo(bytes.AsSpan(y * rowBytes, rowBytes));
        return bytes;
    }

    private static async Task<byte[]> SaveAsync(VipsImage img)
    {
        using var ms = new MemoryStream();
        var writer = PipeWriter.Create(ms);
        await VipsPnmSaver.SaveAsync(img, writer, VipsPnmVariant.Pam);
        return ms.ToArray();
    }

    private static async Task<VipsImage> LoadAsync(byte[] bytes)
    {
        var src = new PipeVipsSource(PipeReader.Create(new MemoryStream(bytes)));
        var img = await VipsPnmLoader.LoadAsync(src);
        Assert.NotNull(img);
        return img!;
    }

    [Fact]
    public async Task Pam_Header_StartsWithP7AndHasEndhdr()
    {
        var src = MakeUChar(2, 2, 3, (x, y, c) => 100);
        var bytes = await SaveAsync(src);
        string header = System.Text.Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 96));
        Assert.StartsWith("P7\n", header);
        Assert.Contains("WIDTH 2\n", header);
        Assert.Contains("HEIGHT 2\n", header);
        Assert.Contains("DEPTH 3\n", header);
        Assert.Contains("MAXVAL 255\n", header);
        Assert.Contains("TUPLTYPE RGB\n", header);
        Assert.Contains("ENDHDR\n", header);
    }

    [Theory]
    [InlineData(1)]   // greyscale
    [InlineData(2)]   // greyscale + alpha
    [InlineData(3)]   // RGB
    [InlineData(4)]   // RGB + alpha
    public async Task Pam_UChar_RoundTripsAcrossBandCounts(int bands)
    {
        var src = MakeUChar(5, 4, bands, (x, y, c) => (byte)((x * 13 + y * 7 + c * 31) & 0xFF));
        var bytes = await SaveAsync(src);
        var loaded = await LoadAsync(bytes);

        Assert.Equal(VipsBandFormat.UChar, loaded.BandFormat);
        Assert.Equal(5, loaded.Width);
        Assert.Equal(4, loaded.Height);
        Assert.Equal(bands, loaded.Bands);
        Assert.Equal(ReadAll(src), ReadAll(loaded));
    }

    [Fact]
    public async Task Pam_UShort_16Bit_RoundTripsExactly()
    {
        // RGB+alpha 16-bit: every channel hits both byte halves so
        // any byte-order bug shows up.
        var src = MakeUShort(4, 3, 4, (x, y, c) => (ushort)((x * 1000 + y * 100 + c * 10000) % 65535));
        var bytes = await SaveAsync(src);

        // Verify maxval reflects 16-bit.
        string header = System.Text.Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 96));
        Assert.Contains("MAXVAL 65535\n", header);
        Assert.Contains("DEPTH 4\n", header);
        Assert.Contains("TUPLTYPE RGB_ALPHA\n", header);

        var loaded = await LoadAsync(bytes);
        Assert.Equal(VipsBandFormat.UShort, loaded.BandFormat);
        Assert.Equal(4, loaded.Bands);
        Assert.Equal(ReadAll(src), ReadAll(loaded));
    }

    [Fact]
    public async Task Pam_Auto_AlphaInputs_GoToPam()
    {
        // The Auto variant routes 4-band (RGBA) input to PAM since
        // PPM has no alpha. Verify that path now stays pure-C#.
        var src = MakeUChar(3, 3, 4, (x, y, c) => (byte)((x * 17 + y * 5 + c * 11) & 0xFF));
        using var ms = new MemoryStream();
        var writer = PipeWriter.Create(ms);
        await VipsPnmSaver.SaveAsync(src, writer); // Auto → Pam for 4 bands
        var bytes = ms.ToArray();

        string header = System.Text.Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 64));
        Assert.StartsWith("P7\n", header);

        var loaded = await LoadAsync(bytes);
        Assert.Equal(4, loaded.Bands);
        Assert.Equal(ReadAll(src), ReadAll(loaded));
    }
}
