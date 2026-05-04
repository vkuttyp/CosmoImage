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
/// Round 111 — TIFF LZW (Compression=5). Magick produces TIFFs using
/// libtiff's early-change LZW variant; the pure decoder must
/// round-trip them pixel-exactly.
/// </summary>
public class Round111Tests
{
    private static byte[] BuildRgbPixels(int w, int h)
    {
        var px = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 3;
                px[o] = (byte)((x * 7) & 0xFF);
                px[o + 1] = (byte)((y * 11) & 0xFF);
                px[o + 2] = (byte)(((x + y) * 13) & 0xFF);
            }
        return px;
    }

    private static byte[] BuildLzwTiff(byte[] rgb, int w, int h)
    {
        var settings = new MagickReadSettings
        {
            Width = (uint)w, Height = (uint)h, Format = MagickFormat.Rgb, Depth = 8,
        };
        using var img = new MagickImage();
        img.Read(rgb, settings);
        img.Format = MagickFormat.Tiff;
        img.Settings.Compression = CompressionMethod.LZW;
        return img.ToByteArray();
    }

    private static void AssertExactPixels(byte[] expected, VipsImage img)
    {
        var got = img.PixelsLazy!.Value;
        Assert.Equal(expected.Length, got.Length);
        for (int i = 0; i < expected.Length; i++)
            if (expected[i] != got[i])
                Assert.Fail($"pixel byte {i}: expected {expected[i]:X2} got {got[i]:X2}");
    }

    [Fact]
    public void Pure_LzwRgbSmall_RoundTrips()
    {
        int w = 16, h = 8;
        var px = BuildRgbPixels(w, h);
        var tiff = BuildLzwTiff(px, w, h);
        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
        Assert.Equal(3, img.Bands);
        AssertExactPixels(px, img);
    }

    [Fact]
    public void Pure_LzwRgbMedium_RoundTrips()
    {
        // 64x64 = 12k pixels. Forces LZW dictionary to grow past
        // 9-bit width into 10/11-bit territory.
        int w = 64, h = 64;
        var px = BuildRgbPixels(w, h);
        var tiff = BuildLzwTiff(px, w, h);
        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        AssertExactPixels(px, img!);
    }

    [Fact]
    public void Pure_LzwRgbLarge_DriverDictGrowthAndStripBoundaries()
    {
        // 256x256 typically lands across multiple strips and exercises
        // 12-bit dictionary saturation on real data.
        int w = 256, h = 256;
        var px = BuildRgbPixels(w, h);
        var tiff = BuildLzwTiff(px, w, h);
        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        AssertExactPixels(px, img!);
    }

    [Fact]
    public void Pure_LzwSolidColor_LongRunCompresses()
    {
        // Pathological case: highly compressible solid-color image
        // exercises long match runs and dictionary repeated reuse.
        int w = 128, h = 32;
        var px = new byte[w * h * 3];
        for (int i = 0; i < w * h; i++) { px[i * 3] = 0x42; px[i * 3 + 1] = 0x84; px[i * 3 + 2] = 0xC0; }
        var tiff = BuildLzwTiff(px, w, h);
        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        AssertExactPixels(px, img!);
    }

    [Fact]
    public void Pure_LzwGrayscale_RoundTrips()
    {
        int w = 64, h = 32;
        var px = new byte[w * h];
        for (int i = 0; i < px.Length; i++) px[i] = (byte)((i * 5) & 0xFF);
        var settings = new MagickReadSettings { Width = (uint)w, Height = (uint)h, Format = MagickFormat.Gray, Depth = 8 };
        using var mi = new MagickImage();
        mi.Read(px, settings);
        mi.Format = MagickFormat.Tiff;
        mi.Settings.Compression = CompressionMethod.LZW;
        var tiff = mi.ToByteArray();

        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        Assert.Equal(1, img!.Bands);
        Assert.Equal(VipsInterpretation.BW, img.Interpretation);
        AssertExactPixels(px, img);
    }

    [Fact]
    public async Task LoadAsync_LzwTiff_TakesPureFastPath()
    {
        int w = 32, h = 16;
        var px = BuildRgbPixels(w, h);
        var tiff = BuildLzwTiff(px, w, h);
        var src = new PipeVipsSource(PipeReader.Create(new MemoryStream(tiff)));
        var img = await VipsTiffLoader.LoadAsync(src);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
        Assert.Equal(3, img.Bands);
        AssertExactPixels(px, img);
    }
}
