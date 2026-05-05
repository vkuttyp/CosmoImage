using System;
using System.IO;
using System.IO.Compression;
using CosmoImage.Core;
using CosmoImage.Loaders;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 154 — TIFF raw-deflate fallback. Compression=8 / 32946
/// strips can hold either a standard zlib-wrapped stream (almost
/// always) or a raw deflate stream (no 2-byte header, no Adler-32
/// trailer — rare, but some pre-zlib encoders emit it). The
/// decoder tries the zlib path first and falls through to a raw
/// DeflateStream when that fails.
/// </summary>
public class Round154Tests
{
    [Fact]
    public void Pure_RawDeflateStrip_Decodes()
    {
        // 4×2 grayscale, raw-deflate compressed.
        byte[] pixels = { 10, 20, 30, 40, 50, 60, 70, 80 };
        var compressed = RawDeflate(pixels);
        var tiff = BuildMinimalTiff(compressed, originalSize: pixels.Length,
            width: 4, height: 2, compression: 8);

        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        Assert.Equal(4, img!.Width);
        Assert.Equal(2, img.Height);
        Assert.Equal(pixels, img.PixelsLazy!.Value);
    }

    [Fact]
    public void Pure_ZlibWrappedDeflateStrip_StillDecodes()
    {
        // Sanity: standard zlib-wrapped deflate path still works.
        byte[] pixels = { 100, 110, 120, 130, 140, 150, 160, 170 };
        var compressed = ZlibWrap(pixels);
        var tiff = BuildMinimalTiff(compressed, originalSize: pixels.Length,
            width: 4, height: 2, compression: 8);

        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        Assert.Equal(pixels, img!.PixelsLazy!.Value);
    }

    private static byte[] RawDeflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var dz = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            dz.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    private static byte[] ZlibWrap(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var dz = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            dz.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    private static byte[] BuildMinimalTiff(byte[] strip, int originalSize, int width, int height, int compression)
    {
        using var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);

        bw.Write((byte)'I'); bw.Write((byte)'I');
        bw.Write((ushort)42);
        const int ifdOffset = 8;
        bw.Write((uint)ifdOffset);

        const int numEntries = 8;
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
        Entry(256, 3, 1, (uint)width);
        Entry(257, 3, 1, (uint)height);
        Entry(258, 3, 1, 8);                         // BitsPerSample = 8
        Entry(259, 3, 1, (uint)compression);
        Entry(262, 3, 1, 1);                         // Photometric = BlackIsZero
        Entry(273, 4, 1, (uint)stripOffset);
        Entry(277, 3, 1, 1);                         // SamplesPerPixel = 1
        Entry(279, 4, 1, (uint)strip.Length);

        bw.Write((uint)0);  // next IFD = none
        bw.Write(strip);
        return ms.ToArray();
    }
}
