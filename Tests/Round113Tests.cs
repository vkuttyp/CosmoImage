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
/// Round 113 — pure-managed WebP VP8L (lossless) decoder. Magick
/// produces lossless WebPs via the libwebp encoder at quality 100;
/// the pure decoder must round-trip them pixel-exactly.
/// </summary>
public class Round113Tests
{
    private static byte[] BuildRgbaPixels(int w, int h)
    {
        // Alpha must stay > 0 throughout: libwebp's "lossless" encoder
        // legitimately drops RGB for fully-transparent pixels (the
        // image looks identical, but the raw bytes change), which
        // would defeat exact round-trip checks.
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                px[o] = (byte)((x * 7) & 0xFF);
                px[o + 1] = (byte)((y * 11) & 0xFF);
                px[o + 2] = (byte)(((x + y) * 13) & 0xFF);
                px[o + 3] = (byte)(0x80 | (x ^ (y * 17)) & 0x7F);
            }
        return px;
    }

    private static byte[] BuildLosslessWebp(byte[] rgba, int w, int h)
    {
        var settings = new MagickReadSettings
        {
            Width = (uint)w, Height = (uint)h, Format = MagickFormat.Rgba, Depth = 8,
        };
        using var img = new MagickImage();
        img.Read(rgba, settings);
        img.Format = MagickFormat.WebP;
        img.Settings.SetDefine(MagickFormat.WebP, "lossless", "true");
        img.Quality = 100;
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
    public void Pure_LosslessRgbaSmall_RoundTrips()
    {
        int w = 16, h = 8;
        var px = BuildRgbaPixels(w, h);
        var webp = BuildLosslessWebp(px, w, h);
        var img = PureWebpLossless.TryDecode(webp);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
        Assert.Equal(4, img.Bands);
        AssertExactPixels(px, img);
    }

    [Fact]
    public void Pure_LosslessSolidColor_RoundTrips()
    {
        int w = 8, h = 8;
        var px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            px[i * 4] = 0x42; px[i * 4 + 1] = 0x84; px[i * 4 + 2] = 0xC0; px[i * 4 + 3] = 0xFF;
        }
        var webp = BuildLosslessWebp(px, w, h);
        var img = PureWebpLossless.TryDecode(webp);
        Assert.NotNull(img);
        AssertExactPixels(px, img!);
    }

    [Fact]
    public void Pure_LosslessLargerImage_RoundTrips()
    {
        int w = 64, h = 32;
        var px = BuildRgbaPixels(w, h);
        var webp = BuildLosslessWebp(px, w, h);
        var img = PureWebpLossless.TryDecode(webp);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        AssertExactPixels(px, img);
    }

    [Fact]
    public void Pure_LossyWebp_ReturnsNull()
    {
        // Lossy WebP (VP8) → pure decoder returns null; caller falls back.
        int w = 16, h = 8;
        var px = BuildRgbaPixels(w, h);
        var settings = new MagickReadSettings { Width = (uint)w, Height = (uint)h, Format = MagickFormat.Rgba, Depth = 8 };
        using var mi = new MagickImage();
        mi.Read(px, settings);
        mi.Format = MagickFormat.WebP;
        mi.Quality = 90;  // lossy
        var webp = mi.ToByteArray();
        Assert.Null(PureWebpLossless.TryDecode(webp));
    }

    [Fact]
    public async Task LoadAsync_LosslessWebp_TakesPureFastPath()
    {
        int w = 32, h = 16;
        var px = BuildRgbaPixels(w, h);
        var webp = BuildLosslessWebp(px, w, h);
        var src = new PipeVipsSource(PipeReader.Create(new MemoryStream(webp)));
        var img = await VipsWebpLoader.LoadAsync(src);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
        Assert.Equal(4, img.Bands);
        AssertExactPixels(px, img);
    }

    [Fact]
    public async Task LoadAsync_LossyWebp_FallsBackToMagick()
    {
        int w = 16, h = 8;
        var px = BuildRgbaPixels(w, h);
        var settings = new MagickReadSettings { Width = (uint)w, Height = (uint)h, Format = MagickFormat.Rgba, Depth = 8 };
        using var mi = new MagickImage();
        mi.Read(px, settings);
        mi.Format = MagickFormat.WebP;
        mi.Quality = 90;
        var webp = mi.ToByteArray();
        var src = new PipeVipsSource(PipeReader.Create(new MemoryStream(webp)));
        var img = await VipsWebpLoader.LoadAsync(src);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
    }
}
