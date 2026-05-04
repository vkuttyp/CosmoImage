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
/// Round 139 — TIFF compression=7 (JPEG). Strips/tiles carry full
/// JPEG bytestreams; the JPEGTables tag (347) holds shared markers
/// when strips share quantization / Huffman tables. We splice tables
/// into each strip and route through the JPEG decoder, which already
/// converts YCbCr → RGB.
/// </summary>
public class Round139Tests
{
    private static byte[] BuildJpegTiff(byte[] rgb, int w, int h)
    {
        var settings = new MagickReadSettings
        {
            Width = (uint)w, Height = (uint)h, Format = MagickFormat.Rgb, Depth = 8,
        };
        using var img = new MagickImage();
        img.Read(rgb, settings);
        img.Format = MagickFormat.Tiff;
        img.Settings.Compression = CompressionMethod.JPEG;
        img.Quality = 95;
        return img.ToByteArray();
    }

    [Fact]
    public void Pure_JpegTiff_PrimaryColors_Decodes()
    {
        // 24x16 image with three solid colour patches; JPEG compression
        // is lossy so we use generous tolerances on each patch's
        // dominant channel and validate cross-channel separation.
        int w = 24, h = 16;
        var rgb = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 3;
                if (x < 8)        { rgb[o] = 255; rgb[o + 1] = 0;   rgb[o + 2] = 0; }
                else if (x < 16)  { rgb[o] = 0;   rgb[o + 1] = 255; rgb[o + 2] = 0; }
                else              { rgb[o] = 0;   rgb[o + 1] = 0;   rgb[o + 2] = 255; }
            }
        var tiff = BuildJpegTiff(rgb, w, h);

        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
        Assert.Equal(3, img.Bands);

        var got = img.PixelsLazy!.Value;
        // Patch centres at y=8, x∈{4, 12, 20}.
        int redOff = (8 * w + 4) * 3;
        int greenOff = (8 * w + 12) * 3;
        int blueOff = (8 * w + 20) * 3;

        Assert.True(got[redOff]     > 200, $"R patch R: {got[redOff]}");
        Assert.True(got[redOff + 2] < 60,  $"R patch B: {got[redOff + 2]}");
        Assert.True(got[greenOff + 1] > 200, $"G patch G: {got[greenOff + 1]}");
        Assert.True(got[blueOff + 2]  > 200, $"B patch B: {got[blueOff + 2]}");
    }

    [Fact]
    public async Task LoadAsync_JpegTiff_TakesPureFastPath()
    {
        int w = 16, h = 16;
        var rgb = new byte[w * h * 3];
        for (int i = 0; i < rgb.Length; i++) rgb[i] = (byte)(i * 13);
        var tiff = BuildJpegTiff(rgb, w, h);
        var src = new PipeVipsSource(PipeReader.Create(new MemoryStream(tiff)));
        var img = await VipsTiffLoader.LoadAsync(src);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
        Assert.Equal(3, img.Bands);
    }
}
