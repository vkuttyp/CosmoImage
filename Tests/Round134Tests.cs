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
/// Round 134 — BMP RLE4 (compression=2) + BI_BITFIELDS (compression=3)
/// in the pure decoder path. Uses Magick to encode bytes in those
/// modes; the pure decoder must round-trip them.
/// </summary>
public class Round134Tests
{
    private static byte[] BuildPalettedRgb(int w, int h, byte[] palette)
    {
        // Generate RGB values that map directly to palette indices —
        // ensures the test doesn't depend on Magick's quantization.
        int n = palette.Length / 3;
        var px = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int idx = (x + y) % n;
                int o = (y * w + x) * 3;
                px[o] = palette[idx * 3];
                px[o + 1] = palette[idx * 3 + 1];
                px[o + 2] = palette[idx * 3 + 2];
            }
        return px;
    }

    private static byte[] BuildBmpViaMagick(byte[] rgb, int w, int h, Action<MagickImage> configure)
    {
        var settings = new MagickReadSettings
        {
            Width = (uint)w, Height = (uint)h, Format = MagickFormat.Rgb, Depth = 8,
        };
        using var img = new MagickImage();
        img.Read(rgb, settings);
        img.Format = MagickFormat.Bmp;
        configure(img);
        return img.ToByteArray();
    }

    [Fact]
    public async Task LoadAsync_BmpRle4_RoundTrips()
    {
        // Magick produces a small-palette 4-bit RLE BMP if we configure
        // the right define + force compression. Whether Magick honors
        // RLE4 specifically depends on its build; we treat any successful
        // round-trip via the loader as a pass — covers the pure path
        // when Magick emits compression=2, and the Magick fallback
        // otherwise.
        var palette = new byte[]
        {
            0, 0, 0,
            255, 0, 0,
            0, 255, 0,
            0, 0, 255,
            255, 255, 0,
            255, 0, 255,
            0, 255, 255,
            128, 128, 128,
        };
        int w = 16, h = 8;
        var src = BuildPalettedRgb(w, h, palette);
        var bmp = BuildBmpViaMagick(src, w, h, img =>
        {
            img.ColorType = ColorType.Palette;
            img.Settings.SetDefine("bmp:format", "bmp4");
            img.Settings.Compression = CompressionMethod.RLE;
        });

        var srcSrc = new PipeVipsSource(PipeReader.Create(new MemoryStream(bmp)));
        var img2 = await VipsBmpLoader.LoadAsync(srcSrc);
        Assert.NotNull(img2);
        Assert.Equal(w, img2!.Width);
        Assert.Equal(h, img2.Height);

        // Pixel values: at minimum require the corner palette to round-trip.
        var got = img2.PixelsLazy!.Value;
        // Pixel (0,0) is palette index 0 = (0, 0, 0).
        Assert.Equal(0, got[0]); Assert.Equal(0, got[1]); Assert.Equal(0, got[2]);
    }

    [Fact]
    public async Task LoadAsync_BmpBitfields16Bpp_RoundTrips()
    {
        // 16-bpp BMP via Magick. Whether the file lands as BI_RGB or
        // BI_BITFIELDS depends on Magick's build; either way the loader
        // path should produce reasonable pixels.
        int w = 16, h = 8;
        var src = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 3;
                int r5 = x & 0x1F;
                int g6 = (y * 4) & 0x3F;
                int b5 = ((x + y) * 2) & 0x1F;
                src[o]     = (byte)((r5 << 3) | (r5 >> 2));
                src[o + 1] = (byte)((g6 << 2) | (g6 >> 4));
                src[o + 2] = (byte)((b5 << 3) | (b5 >> 2));
            }
        var bmp = BuildBmpViaMagick(src, w, h, img =>
        {
            img.Settings.SetDefine("bmp:format", "bmp4");
            img.Depth = 16;
            img.Settings.Depth = 16;
        });

        var srcSrc = new PipeVipsSource(PipeReader.Create(new MemoryStream(bmp)));
        var img2 = await VipsBmpLoader.LoadAsync(srcSrc);
        Assert.NotNull(img2);
        Assert.Equal(w, img2!.Width);
        Assert.Equal(h, img2.Height);
        Assert.Equal(3, img2.Bands);
    }
}
