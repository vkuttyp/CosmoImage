using System;
using System.IO;
using CosmoImage.Core;
using CosmoImage.Loaders;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 152 — TIFF SampleFormat=2 (two's-complement signed
/// integers). Common in scientific / medical imaging (signed-16
/// echo-tomography, signed-32 displacement fields). Previously
/// rejected because the decoder only accepted SampleFormat 1
/// (unsigned int) and 3 (IEEE float).
/// </summary>
public class Round152Tests
{
    [Fact]
    public void Pure_Signed8_RoundTrips()
    {
        sbyte[] vals = { -128, -64, -1, 0, 1, 64, 127, 100 };
        var bytes = new byte[vals.Length];
        for (int i = 0; i < vals.Length; i++) bytes[i] = (byte)vals[i];
        var tiff = BuildMinimalIntTiff(bytes, 4, 2, bps: 8, sampleFormat: 2);

        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        Assert.Equal(VipsBandFormat.Char, img!.BandFormat);
        Assert.Equal(bytes, img.PixelsLazy!.Value);
    }

    [Fact]
    public void Pure_Signed16_RoundTrips()
    {
        short[] vals = { short.MinValue, -1000, 0, 1000, short.MaxValue, -32000, 12345, -54 };
        var bytes = new byte[vals.Length * 2];
        for (int i = 0; i < vals.Length; i++)
        {
            // Output is host LE.
            bytes[i * 2]     = (byte)vals[i];
            bytes[i * 2 + 1] = (byte)(vals[i] >> 8);
        }
        var tiff = BuildMinimalIntTiff(bytes, 4, 2, bps: 16, sampleFormat: 2);

        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        Assert.Equal(VipsBandFormat.Short, img!.BandFormat);
        Assert.Equal(bytes, img.PixelsLazy!.Value);
    }

    [Fact]
    public void Pure_Signed32_RoundTrips()
    {
        int[] vals = { int.MinValue, -1, 0, 1, int.MaxValue, -1000000, 12345678, -54321 };
        var bytes = new byte[vals.Length * 4];
        for (int i = 0; i < vals.Length; i++)
        {
            bytes[i * 4]     = (byte)vals[i];
            bytes[i * 4 + 1] = (byte)(vals[i] >> 8);
            bytes[i * 4 + 2] = (byte)(vals[i] >> 16);
            bytes[i * 4 + 3] = (byte)(vals[i] >> 24);
        }
        var tiff = BuildMinimalIntTiff(bytes, 4, 2, bps: 32, sampleFormat: 2);

        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        Assert.Equal(VipsBandFormat.Int, img!.BandFormat);
        Assert.Equal(bytes, img.PixelsLazy!.Value);
    }

    [Fact]
    public void Pure_Unsigned32_RoundTrips()
    {
        // sampleFormat=1, bps=32 — UInt (was a latent gap; covered by
        // the same code change because the band-format mapping was
        // expanded to include (1, 32) → UInt).
        uint[] vals = { 0, 0xDEADBEEF, uint.MaxValue, 1234567 };
        var bytes = new byte[vals.Length * 4];
        for (int i = 0; i < vals.Length; i++)
        {
            bytes[i * 4]     = (byte)vals[i];
            bytes[i * 4 + 1] = (byte)(vals[i] >> 8);
            bytes[i * 4 + 2] = (byte)(vals[i] >> 16);
            bytes[i * 4 + 3] = (byte)(vals[i] >> 24);
        }
        var tiff = BuildMinimalIntTiff(bytes, 2, 2, bps: 32, sampleFormat: 1);

        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        Assert.Equal(VipsBandFormat.UInt, img!.BandFormat);
        Assert.Equal(bytes, img.PixelsLazy!.Value);
    }

    /// <summary>
    /// Hand-build a single-band integer TIFF: header + IFD + raw strip.
    /// Sets ImageWidth/ImageLength, BitsPerSample, Compression=1 (none),
    /// Photometric=1 (BlackIsZero), StripOffsets, SamplesPerPixel=1,
    /// StripByteCounts, SampleFormat.
    /// </summary>
    private static byte[] BuildMinimalIntTiff(byte[] pixels, int width, int height, int bps, int sampleFormat)
    {
        using var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);

        bw.Write((byte)'I'); bw.Write((byte)'I');
        bw.Write((ushort)42);
        const int ifdOffset = 8;
        bw.Write((uint)ifdOffset);

        const int numEntries = 9;
        bw.Write((ushort)numEntries);

        int entrySize = 12;
        int afterIfd = ifdOffset + 2 + numEntries * entrySize + 4;
        int stripOffset = afterIfd;

        void Entry(ushort tag, ushort type, uint count, uint value)
        {
            bw.Write(tag);
            bw.Write(type);
            bw.Write(count);
            bw.Write(value);
        }
        // type 3 = SHORT, 4 = LONG.
        Entry(256, 3, 1, (uint)width);
        Entry(257, 3, 1, (uint)height);
        Entry(258, 3, 1, (uint)bps);
        Entry(259, 3, 1, 1);                         // Compression = none
        Entry(262, 3, 1, 1);                         // Photometric = BlackIsZero
        Entry(273, 4, 1, (uint)stripOffset);
        Entry(277, 3, 1, 1);                         // SamplesPerPixel = 1
        Entry(279, 4, 1, (uint)pixels.Length);
        Entry(339, 3, 1, (uint)sampleFormat);        // SampleFormat

        bw.Write((uint)0);  // next IFD = none
        bw.Write(pixels);
        return ms.ToArray();
    }
}
