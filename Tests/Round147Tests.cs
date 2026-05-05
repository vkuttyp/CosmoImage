using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Core;
using CosmoImage.Loaders;
using ImageMagick;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 147 — TIFF tiled+planar=2 combo and FillOrder=2 acceptance.
/// Both were documented Magick fallbacks: the tile/plane combo was
/// deferred from Round 133, and FillOrder=2 was rejected even though
/// it's a no-op for the bps≥8 data we support.
/// </summary>
public class Round147Tests
{
    private static byte[] BuildRgbPixels(int w, int h)
    {
        var px = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 3;
                px[o]     = (byte)((x * 5 + y * 11) & 0xFF);
                px[o + 1] = (byte)((x * 7 + y * 3) & 0xFF);
                px[o + 2] = (byte)((x ^ y) * 13 & 0xFF);
            }
        return px;
    }

    private static byte[] BuildTiledPlanarTiff(byte[] rgb, int w, int h,
        int tileW, int tileH, CompressionMethod compression)
    {
        var settings = new MagickReadSettings
        {
            Width = (uint)w, Height = (uint)h, Format = MagickFormat.Rgb, Depth = 8,
        };
        using var img = new MagickImage();
        img.Read(rgb, settings);
        img.Format = MagickFormat.Tiff;
        img.Settings.Compression = compression;
        // Force tiled storage with the requested tile dimensions, plus
        // separate (planar=2) channels.
        img.Settings.SetDefine("tiff:tile-geometry", $"{tileW}x{tileH}");
        img.Settings.SetDefine("tiff:planar-configuration", "separate");
        return img.ToByteArray();
    }

    private static void AssertExactPixels(byte[] expected, VipsImage img)
    {
        var got = img.PixelsLazy!.Value;
        Assert.Equal(expected.Length, got.Length);
        for (int i = 0; i < expected.Length; i++)
            if (expected[i] != got[i])
                Assert.Fail($"byte {i}: expected {expected[i]:X2} got {got[i]:X2}");
    }

    [Fact]
    public void Pure_TiledPlanarUncompressed_RoundTrips()
    {
        int w = 16, h = 16;
        var px = BuildRgbPixels(w, h);
        var tiff = BuildTiledPlanarTiff(px, w, h, tileW: 16, tileH: 16,
            CompressionMethod.NoCompression);

        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
        Assert.Equal(3, img.Bands);
        AssertExactPixels(px, img);
    }

    [Fact]
    public void Pure_TiledPlanarLzw_RoundTrips()
    {
        int w = 32, h = 32;
        var px = BuildRgbPixels(w, h);
        var tiff = BuildTiledPlanarTiff(px, w, h, tileW: 16, tileH: 16,
            CompressionMethod.LZW);

        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        AssertExactPixels(px, img!);
    }

    [Fact]
    public async Task LoadAsync_TiledPlanarTiff_TakesPureFastPath()
    {
        int w = 32, h = 16;
        var px = BuildRgbPixels(w, h);
        var tiff = BuildTiledPlanarTiff(px, w, h, tileW: 16, tileH: 16,
            CompressionMethod.LZW);
        var src = new PipeVipsSource(PipeReader.Create(new MemoryStream(tiff)));
        var img = await VipsTiffLoader.LoadAsync(src);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
        AssertExactPixels(px, img);
    }

    [Fact]
    public void Pure_FillOrder2_AcceptedAsNoOpForByteAlignedData()
    {
        // Hand-build a minimal 4×2 8-bit grayscale TIFF with FillOrder=2.
        // For bps=8, FillOrder is a no-op — pixels should round-trip
        // exactly through the pure decoder.
        byte[] pixels = { 10, 20, 30, 40, 50, 60, 70, 80 };
        var tiff = BuildMinimalGrayscaleTiff(pixels, width: 4, height: 2,
            fillOrder: 2);
        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        Assert.Equal(4, img!.Width);
        Assert.Equal(2, img.Height);
        Assert.Equal(1, img.Bands);
        var got = img.PixelsLazy!.Value;
        for (int i = 0; i < pixels.Length; i++)
            Assert.Equal(pixels[i], got[i]);
    }

    /// <summary>
    /// Hand-build a tiny TIFF: header (II + magic + IFD0 offset) + IFD
    /// with the standard 8-bit grayscale tag set + the requested
    /// FillOrder + raw pixel strip.
    /// </summary>
    private static byte[] BuildMinimalGrayscaleTiff(byte[] pixels, int width, int height, int fillOrder)
    {
        using var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);

        // Pixel data first (so we know its offset).
        bw.Write((byte)'I'); bw.Write((byte)'I');  // little-endian
        bw.Write((ushort)42);                       // TIFF magic

        int ifdOffset = 8;  // IFD immediately follows the 8-byte header
        bw.Write((uint)ifdOffset);

        // 9 IFD entries: ImageWidth, ImageLength, BitsPerSample, Compression,
        // Photometric, FillOrder, StripOffsets, SamplesPerPixel, StripByteCounts.
        const int numEntries = 9;
        bw.Write((ushort)numEntries);

        int entrySize = 12;
        int afterIfd = ifdOffset + 2 + numEntries * entrySize + 4;  // +4 for next-IFD ptr
        int stripOffset = afterIfd;

        void Entry(ushort tag, ushort type, uint count, uint value)
        {
            bw.Write(tag);
            bw.Write(type);
            bw.Write(count);
            bw.Write(value);
        }
        // Type 3 = SHORT (2 bytes), 4 = LONG (4 bytes).
        Entry(256, 3, 1, (uint)width);
        Entry(257, 3, 1, (uint)height);
        Entry(258, 3, 1, 8);                         // BitsPerSample = 8
        Entry(259, 3, 1, 1);                         // Compression = 1 (none)
        Entry(262, 3, 1, 1);                         // Photometric = 1 (BlackIsZero)
        Entry(266, 3, 1, (uint)fillOrder);           // FillOrder
        Entry(273, 4, 1, (uint)stripOffset);         // StripOffsets
        Entry(277, 3, 1, 1);                         // SamplesPerPixel = 1
        Entry(279, 4, 1, (uint)pixels.Length);       // StripByteCounts

        bw.Write((uint)0);  // next IFD = none
        bw.Write(pixels);

        return ms.ToArray();
    }
}
