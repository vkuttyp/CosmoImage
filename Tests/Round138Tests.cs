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
/// Round 138 — JPEG decoder produces RGB instead of raw YCbCr.
/// SimpleOutputWriter emits raw component samples interleaved at
/// per-pixel offsets 0/1/2; the loader now post-processes that
/// buffer (YCbCr→RGB for default JFIF) before handing it back as
/// RGB pixels. Adobe APP14 markers steer alternate paths
/// (RGB / YCCK / CMYK).
/// </summary>
public class Round138Tests
{
    private static byte[] EncodeJpeg(byte[] rgb, int w, int h)
    {
        var settings = new MagickReadSettings
        {
            Width = (uint)w, Height = (uint)h, Format = MagickFormat.Rgb, Depth = 8,
        };
        using var img = new MagickImage();
        img.Read(rgb, settings);
        img.Format = MagickFormat.Jpeg;
        img.Quality = 95;
        return img.ToByteArray();
    }

    [Fact]
    public async Task LoadAsync_PrimaryColors_DecodeAsRgb()
    {
        // Solid 8x8 patches of pure red, green, blue laid out as a 24x8
        // strip. Magick encodes via JFIF YCbCr; our loader must convert
        // back to RGB so primaries land near (255, 0, 0) etc.
        int w = 24, h = 8;
        var rgb = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 3;
                if (x < 8)        { rgb[o] = 255; rgb[o + 1] = 0;   rgb[o + 2] = 0; }
                else if (x < 16)  { rgb[o] = 0;   rgb[o + 1] = 255; rgb[o + 2] = 0; }
                else              { rgb[o] = 0;   rgb[o + 1] = 0;   rgb[o + 2] = 255; }
            }
        var jpeg = EncodeJpeg(rgb, w, h);

        var src = new PipeVipsSource(PipeReader.Create(new MemoryStream(jpeg)));
        var img = await VipsJpegLoader.LoadAsync(src);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
        Assert.Equal(3, img.Bands);

        var got = img.PixelsLazy!.Value;
        // Center of each patch: well clear of edge ringing.
        int redIdx = (4 * w + 4) * 3;
        int greenIdx = (4 * w + 12) * 3;
        int blueIdx = (4 * w + 20) * 3;

        Assert.True(got[redIdx] > 200,     $"R patch R: {got[redIdx]}");
        Assert.True(got[redIdx + 1] < 50,  $"R patch G: {got[redIdx + 1]}");
        Assert.True(got[redIdx + 2] < 50,  $"R patch B: {got[redIdx + 2]}");

        Assert.True(got[greenIdx] < 100,     $"G patch R: {got[greenIdx]}");
        Assert.True(got[greenIdx + 1] > 200, $"G patch G: {got[greenIdx + 1]}");
        Assert.True(got[greenIdx + 2] < 50,  $"G patch B: {got[greenIdx + 2]}");

        Assert.True(got[blueIdx] < 50,       $"B patch R: {got[blueIdx]}");
        Assert.True(got[blueIdx + 1] < 100,  $"B patch G: {got[blueIdx + 1]}");
        Assert.True(got[blueIdx + 2] > 200,  $"B patch B: {got[blueIdx + 2]}");
    }

    [Fact]
    public async Task LoadAsync_Grayscale_PassesThroughUnchanged()
    {
        // 1-component JPEG: no color conversion (DetectJpegColorSpace
        // returns Grayscale), output equals decoded Y values.
        int w = 16, h = 8;
        var gray = new byte[w * h];
        for (int i = 0; i < gray.Length; i++) gray[i] = (byte)(i * 2);

        var settings = new MagickReadSettings
        {
            Width = (uint)w, Height = (uint)h, Format = MagickFormat.Gray, Depth = 8,
        };
        using var imgM = new MagickImage();
        imgM.Read(gray, settings);
        imgM.Format = MagickFormat.Jpeg;
        imgM.ColorType = ColorType.Grayscale;
        imgM.Quality = 95;
        var jpeg = imgM.ToByteArray();

        var src = new PipeVipsSource(PipeReader.Create(new MemoryStream(jpeg)));
        var img = await VipsJpegLoader.LoadAsync(src);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
        Assert.Equal(1, img.Bands);
        var got = img.PixelsLazy!.Value;
        // Grayscale JPEG is lossy — allow generous tolerance.
        for (int i = 0; i < gray.Length; i++)
            Assert.True(Math.Abs(got[i] - gray[i]) < 16, $"pixel {i}: in={gray[i]} got={got[i]}");
    }
}
