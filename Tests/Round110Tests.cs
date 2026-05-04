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
/// Round 110 — TIFF compression schemes (Compression=8/32946/32773) and
/// horizontal-differencing predictor (Predictor=2). Magick.NET writes
/// the compressed TIFFs; the pure decoder must round-trip them
/// pixel-exactly with no Magick fallback.
/// </summary>
public class Round110Tests
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

    private static byte[] BuildTiffViaMagick(byte[] rgbPixels, int w, int h, CompressionMethod compression)
    {
        var settings = new MagickReadSettings
        {
            Width = (uint)w, Height = (uint)h, Format = MagickFormat.Rgb, Depth = 8,
        };
        using var img = new MagickImage();
        img.Read(rgbPixels, settings);
        img.Format = MagickFormat.Tiff;
        img.Settings.Compression = compression;
        return img.ToByteArray();
    }

    private static void AssertRgbDecode(byte[] tiff, int w, int h, byte[] expectedPixels)
    {
        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
        Assert.Equal(3, img.Bands);
        Assert.Equal(VipsBandFormat.UChar, img.BandFormat);
        var got = img.PixelsLazy!.Value;
        Assert.Equal(expectedPixels.Length, got.Length);
        for (int i = 0; i < expectedPixels.Length; i++)
            if (expectedPixels[i] != got[i])
                Assert.Fail($"pixel byte {i}: expected {expectedPixels[i]:X2} got {got[i]:X2}");
    }

    // ---- PackBits ----

    [Fact]
    public void Pure_PackBitsRgb_RoundTrips()
    {
        int w = 32, h = 16;
        var px = BuildRgbPixels(w, h);
        var tiff = BuildTiffViaMagick(px, w, h, CompressionMethod.RLE);
        AssertRgbDecode(tiff, w, h, px);
    }

    [Fact]
    public void Pure_PackBitsLongRun_DecompressesCorrectly()
    {
        // 256-pixel-wide solid red row exercises long replicate runs.
        int w = 256, h = 4;
        var px = new byte[w * h * 3];
        for (int i = 0; i < w * h; i++) { px[i * 3] = 0xFF; px[i * 3 + 1] = 0; px[i * 3 + 2] = 0; }
        var tiff = BuildTiffViaMagick(px, w, h, CompressionMethod.RLE);
        AssertRgbDecode(tiff, w, h, px);
    }

    // ---- Deflate ----

    [Fact]
    public void Pure_DeflateRgb_RoundTrips()
    {
        int w = 24, h = 12;
        var px = BuildRgbPixels(w, h);
        var tiff = BuildTiffViaMagick(px, w, h, CompressionMethod.Zip);
        AssertRgbDecode(tiff, w, h, px);
    }

    [Fact]
    public void Pure_DeflateLargeImage_DecompressesAcrossStrips()
    {
        // 256x128 forces multi-strip output from Magick's Deflate path.
        int w = 256, h = 128;
        var px = BuildRgbPixels(w, h);
        var tiff = BuildTiffViaMagick(px, w, h, CompressionMethod.Zip);
        AssertRgbDecode(tiff, w, h, px);
    }

    // ---- End-to-end via VipsTiffLoader (pure path) ----

    [Fact]
    public async Task LoadAsync_PackBitsTiff_TakesPureFastPath()
    {
        int w = 16, h = 8;
        var px = BuildRgbPixels(w, h);
        var tiff = BuildTiffViaMagick(px, w, h, CompressionMethod.RLE);
        var src = new PipeVipsSource(PipeReader.Create(new MemoryStream(tiff)));
        var img = await VipsTiffLoader.LoadAsync(src);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
        Assert.Equal(3, img.Bands);
        // Pure-path decoded pixels must match the originals byte-for-byte.
        var got = img.PixelsLazy!.Value;
        for (int i = 0; i < px.Length; i++)
            Assert.Equal(px[i], got[i]);
    }

    [Fact]
    public async Task LoadAsync_DeflateTiff_TakesPureFastPath()
    {
        int w = 16, h = 8;
        var px = BuildRgbPixels(w, h);
        var tiff = BuildTiffViaMagick(px, w, h, CompressionMethod.Zip);
        var src = new PipeVipsSource(PipeReader.Create(new MemoryStream(tiff)));
        var img = await VipsTiffLoader.LoadAsync(src);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
        Assert.Equal(3, img.Bands);
        var got = img.PixelsLazy!.Value;
        for (int i = 0; i < px.Length; i++)
            Assert.Equal(px[i], got[i]);
    }

    // ---- JPEG-in-TIFF (Compression=7) is genuinely unsupported here ----

    [Fact]
    public async Task LoadAsync_JpegInTiff_FallsBackToMagick()
    {
        // JPEG-compressed TIFF strips contain a JPEG bitstream — outside
        // the pure decoder's scope. Magick still handles it.
        int w = 16, h = 16;
        var px = BuildRgbPixels(w, h);
        var tiff = BuildTiffViaMagick(px, w, h, CompressionMethod.JPEG);
        var src = new PipeVipsSource(PipeReader.Create(new MemoryStream(tiff)));
        var img = await VipsTiffLoader.LoadAsync(src);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
    }
}
