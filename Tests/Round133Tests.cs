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
/// Round 133 — TIFF PlanarConfiguration=2 (separated planes). Each
/// channel stored as its own contiguous strip set instead of
/// interleaved per-pixel. Common in scientific imaging and some
/// older scanner output.
/// </summary>
public class Round133Tests
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

    private static byte[] BuildPlanarTiff(byte[] rgb, int w, int h, CompressionMethod compression)
    {
        var settings = new MagickReadSettings
        {
            Width = (uint)w, Height = (uint)h, Format = MagickFormat.Rgb, Depth = 8,
        };
        using var img = new MagickImage();
        img.Read(rgb, settings);
        img.Format = MagickFormat.Tiff;
        img.Settings.Compression = compression;
        // Force libtiff to emit PlanarConfiguration=2 (separate planes).
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
    public void Pure_PlanarUncompressedRgb_RoundTrips()
    {
        int w = 16, h = 8;
        var px = BuildRgbPixels(w, h);
        var tiff = BuildPlanarTiff(px, w, h, CompressionMethod.NoCompression);
        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
        Assert.Equal(3, img.Bands);
        AssertExactPixels(px, img);
    }

    [Fact]
    public void Pure_PlanarLzwRgb_RoundTrips()
    {
        int w = 32, h = 16;
        var px = BuildRgbPixels(w, h);
        var tiff = BuildPlanarTiff(px, w, h, CompressionMethod.LZW);
        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        AssertExactPixels(px, img!);
    }

    [Fact]
    public void Pure_PlanarDeflateRgb_RoundTrips()
    {
        int w = 24, h = 12;
        var px = BuildRgbPixels(w, h);
        var tiff = BuildPlanarTiff(px, w, h, CompressionMethod.Zip);
        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        AssertExactPixels(px, img!);
    }

    [Fact]
    public async Task LoadAsync_PlanarTiff_TakesPureFastPath()
    {
        int w = 16, h = 8;
        var px = BuildRgbPixels(w, h);
        var tiff = BuildPlanarTiff(px, w, h, CompressionMethod.LZW);
        var src = new PipeVipsSource(PipeReader.Create(new MemoryStream(tiff)));
        var img = await VipsTiffLoader.LoadAsync(src);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
        AssertExactPixels(px, img);
    }
}
