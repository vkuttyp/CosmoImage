using System;
using System.IO;
using System.IO.Pipelines;
using System.Text;
using System.Threading.Tasks;
using CosmoImage.Core;
using CosmoImage.Loaders;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 145 — PNM PAM (P7) and 16-bit-per-sample variants. Both
/// previously routed through Magick; this round adds them to the pure
/// fast path. PAM has line-oriented WIDTH / HEIGHT / DEPTH / MAXVAL /
/// TUPLTYPE / ENDHDR header. 16-bit samples (maxval > 255) are
/// big-endian per spec and output as <see cref="VipsBandFormat.UShort"/>.
/// </summary>
public class Round145Tests
{
    private static IVipsSource Source(byte[] bytes)
        => new PipeVipsSource(PipeReader.Create(new MemoryStream(bytes)));

    private static byte[] Concat(params byte[][] parts)
    {
        int total = 0;
        foreach (var p in parts) total += p.Length;
        var dst = new byte[total];
        int o = 0;
        foreach (var p in parts) { Buffer.BlockCopy(p, 0, dst, o, p.Length); o += p.Length; }
        return dst;
    }

    [Fact]
    public async Task P5_16bit_Grayscale_Decodes()
    {
        // 4×2 grayscale, maxval 65535 — direct big-endian samples.
        var hdr = Encoding.ASCII.GetBytes("P5\n4 2\n65535\n");
        ushort[] samples = { 0x0000, 0x4000, 0x8000, 0xFFFF, 0x1111, 0x2222, 0x3333, 0x4444 };
        var data = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            data[i * 2]     = (byte)(samples[i] >> 8);   // big-endian
            data[i * 2 + 1] = (byte)(samples[i] & 0xFF);
        }
        var bytes = Concat(hdr, data);

        var img = await VipsPnmLoader.LoadAsync(Source(bytes));
        Assert.NotNull(img);
        Assert.Equal(4, img!.Width);
        Assert.Equal(2, img.Height);
        Assert.Equal(1, img.Bands);
        Assert.Equal(VipsBandFormat.UShort, img.BandFormat);

        var got = img.PixelsLazy!.Value;
        for (int i = 0; i < samples.Length; i++)
        {
            // Output is host LE.
            ushort v = (ushort)(got[i * 2] | (got[i * 2 + 1] << 8));
            Assert.Equal(samples[i], v);
        }
    }

    [Fact]
    public async Task P6_16bit_Rgb_Decodes()
    {
        // 2×1 RGB, maxval 1000 → samples rescale to 0..65535.
        var hdr = Encoding.ASCII.GetBytes("P6\n2 1\n1000\n");
        ushort[] samples = { 0, 500, 1000, 250, 750, 1000 };
        var data = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            data[i * 2]     = (byte)(samples[i] >> 8);
            data[i * 2 + 1] = (byte)(samples[i] & 0xFF);
        }
        var bytes = Concat(hdr, data);

        var img = await VipsPnmLoader.LoadAsync(Source(bytes));
        Assert.NotNull(img);
        Assert.Equal(3, img!.Bands);
        Assert.Equal(VipsBandFormat.UShort, img.BandFormat);

        var got = img.PixelsLazy!.Value;
        for (int i = 0; i < samples.Length; i++)
        {
            ushort v = (ushort)(got[i * 2] | (got[i * 2 + 1] << 8));
            ushort expected = (ushort)Math.Clamp((long)samples[i] * 65535 / 1000, 0, 65535);
            Assert.Equal(expected, v);
        }
    }

    [Fact]
    public async Task P7_Pam_Rgb_Decodes()
    {
        // Standard PAM RGB, maxval 255.
        var hdr = Encoding.ASCII.GetBytes(
            "P7\nWIDTH 3\nHEIGHT 1\nDEPTH 3\nMAXVAL 255\nTUPLTYPE RGB\nENDHDR\n");
        var data = new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255 };
        var bytes = Concat(hdr, data);

        var img = await VipsPnmLoader.LoadAsync(Source(bytes));
        Assert.NotNull(img);
        Assert.Equal(3, img!.Width);
        Assert.Equal(1, img.Height);
        Assert.Equal(3, img.Bands);
        Assert.Equal(VipsBandFormat.UChar, img.BandFormat);
        Assert.Equal(VipsInterpretation.RGB, img.Interpretation);
        Assert.Equal(data, img.PixelsLazy!.Value);
    }

    [Fact]
    public async Task P7_Pam_Rgba_Decodes()
    {
        // 4-band PAM (DEPTH 4), TUPLTYPE RGB_ALPHA.
        var hdr = Encoding.ASCII.GetBytes(
            "P7\nWIDTH 2\nHEIGHT 1\nDEPTH 4\nMAXVAL 255\nTUPLTYPE RGB_ALPHA\nENDHDR\n");
        var data = new byte[] { 10, 20, 30, 40, 50, 60, 70, 80 };
        var bytes = Concat(hdr, data);

        var img = await VipsPnmLoader.LoadAsync(Source(bytes));
        Assert.NotNull(img);
        Assert.Equal(4, img!.Bands);
        Assert.Equal(data, img.PixelsLazy!.Value);
    }

    [Fact]
    public async Task P7_Pam_Grayscale_Decodes()
    {
        var hdr = Encoding.ASCII.GetBytes(
            "P7\nWIDTH 4\nHEIGHT 1\nDEPTH 1\nMAXVAL 255\nTUPLTYPE GRAYSCALE\nENDHDR\n");
        var data = new byte[] { 0, 85, 170, 255 };
        var bytes = Concat(hdr, data);

        var img = await VipsPnmLoader.LoadAsync(Source(bytes));
        Assert.NotNull(img);
        Assert.Equal(1, img!.Bands);
        Assert.Equal(VipsInterpretation.BW, img.Interpretation);
        Assert.Equal(data, img.PixelsLazy!.Value);
    }

    [Fact]
    public async Task P7_Pam_16bit_Rgb_Decodes()
    {
        // 16-bit PAM RGB.
        var hdr = Encoding.ASCII.GetBytes(
            "P7\nWIDTH 2\nHEIGHT 1\nDEPTH 3\nMAXVAL 65535\nTUPLTYPE RGB\nENDHDR\n");
        ushort[] samples = { 0xFFFF, 0x0000, 0x0000, 0x0000, 0xFFFF, 0x0000 };
        var data = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            data[i * 2]     = (byte)(samples[i] >> 8);
            data[i * 2 + 1] = (byte)(samples[i] & 0xFF);
        }
        var bytes = Concat(hdr, data);

        var img = await VipsPnmLoader.LoadAsync(Source(bytes));
        Assert.NotNull(img);
        Assert.Equal(VipsBandFormat.UShort, img.BandFormat);
        var got = img!.PixelsLazy!.Value;
        for (int i = 0; i < samples.Length; i++)
        {
            ushort v = (ushort)(got[i * 2] | (got[i * 2 + 1] << 8));
            Assert.Equal(samples[i], v);
        }
    }

    [Fact]
    public async Task P2_16bit_AsciiGrayscale_Decodes()
    {
        // 16-bit ASCII PGM. Same rescale behavior, but values arrive
        // as text tokens.
        var bytes = Encoding.ASCII.GetBytes("P2\n4 1\n4095\n0 1024 2048 4095\n");

        var img = await VipsPnmLoader.LoadAsync(Source(bytes));
        Assert.NotNull(img);
        Assert.Equal(VipsBandFormat.UShort, img!.BandFormat);
        var got = img.PixelsLazy!.Value;
        // Rescale 0..4095 → 0..65535. (n * 65535) / 4095.
        ushort[] expected = { 0, (ushort)(1024L * 65535 / 4095), (ushort)(2048L * 65535 / 4095), 65535 };
        for (int i = 0; i < expected.Length; i++)
        {
            ushort v = (ushort)(got[i * 2] | (got[i * 2 + 1] << 8));
            Assert.Equal(expected[i], v);
        }
    }
}
