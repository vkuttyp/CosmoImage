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
/// Round 115 — tile-based TIFF support. Tiles partition the image
/// into fixed-size blocks (always padded to tileWidth × tileLength
/// per spec) instead of full-width strips. Common in geospatial /
/// biomedical imagery for partial-region random access.
/// </summary>
public class Round115Tests
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

    /// <summary>
    /// Build a tiled TIFF via Magick. The `tile-geometry` define forces
    /// libtiff to emit TileWidth/TileLength/TileOffsets/TileByteCounts
    /// instead of the strip tags.
    /// </summary>
    private static byte[] BuildTiledTiff(byte[] rgb, int w, int h,
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
        img.Settings.SetDefine(MagickFormat.Tiff, "tile-geometry", $"{tileW}x{tileH}");
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
    public void Pure_TiledUncompressedRgb_RoundTrips()
    {
        // 32×32 image with 16×16 tiles → 2×2 = 4 tiles, all aligned.
        int w = 32, h = 32;
        var px = BuildRgbPixels(w, h);
        var tiff = BuildTiledTiff(px, w, h, 16, 16, CompressionMethod.NoCompression);
        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
        Assert.Equal(3, img.Bands);
        AssertExactPixels(px, img);
    }

    [Fact]
    public void Pure_TiledLzwRgb_RoundTrips()
    {
        int w = 64, h = 32;
        var px = BuildRgbPixels(w, h);
        var tiff = BuildTiledTiff(px, w, h, 16, 16, CompressionMethod.LZW);
        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        AssertExactPixels(px, img);
    }

    [Fact]
    public void Pure_TiledDeflateRgb_RoundTrips()
    {
        int w = 48, h = 32;
        var px = BuildRgbPixels(w, h);
        var tiff = BuildTiledTiff(px, w, h, 16, 16, CompressionMethod.Zip);
        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        AssertExactPixels(px, img!);
    }

    [Fact]
    public void Pure_TiledRgbEdgeTiles_HandlesPadding()
    {
        // 40×24 image with 16×16 tiles → 3×2 grid where right column
        // (8 px) and bottom row (8 px) are partial. Pure decoder must
        // clamp tile pixel copies to image bounds.
        int w = 40, h = 24;
        var px = BuildRgbPixels(w, h);
        var tiff = BuildTiledTiff(px, w, h, 16, 16, CompressionMethod.NoCompression);
        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
        AssertExactPixels(px, img);
    }

    [Fact]
    public async Task LoadAsync_TiledTiff_TakesPureFastPath()
    {
        int w = 32, h = 16;
        var px = BuildRgbPixels(w, h);
        var tiff = BuildTiledTiff(px, w, h, 16, 16, CompressionMethod.LZW);
        var src = new PipeVipsSource(PipeReader.Create(new MemoryStream(tiff)));
        var img = await VipsTiffLoader.LoadAsync(src);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
        AssertExactPixels(px, img);
    }
}
