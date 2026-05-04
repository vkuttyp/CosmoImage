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
/// Round 118 — CMYK TIFFs (Photometric=5). Standard for prepress
/// pipelines. The decoder produces 4-band CMYK pixels with
/// VipsInterpretation.CMYK; downstream conversion to RGB is the
/// caller's responsibility.
/// </summary>
public class Round118Tests
{
    private static byte[] BuildCmykPixels(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                px[o] = (byte)((x * 7) & 0xFF);            // C
                px[o + 1] = (byte)((y * 11) & 0xFF);       // M
                px[o + 2] = (byte)(((x + y) * 13) & 0xFF); // Y
                px[o + 3] = (byte)((x ^ y * 5) & 0xFF);    // K
            }
        return px;
    }

    private static byte[] BuildCmykTiff(byte[] cmyk, int w, int h, CompressionMethod compression)
    {
        var settings = new MagickReadSettings
        {
            Width = (uint)w, Height = (uint)h, Format = MagickFormat.Cmyk, Depth = 8,
        };
        using var img = new MagickImage();
        img.Read(cmyk, settings);
        img.ColorSpace = ColorSpace.CMYK;
        img.Format = MagickFormat.Tiff;
        img.Settings.Compression = compression;
        return img.ToByteArray();
    }

    [Fact]
    public void Pure_CmykUncompressed_RoundTrips()
    {
        int w = 16, h = 8;
        var px = BuildCmykPixels(w, h);
        var tiff = BuildCmykTiff(px, w, h, CompressionMethod.NoCompression);
        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
        Assert.Equal(4, img.Bands);
        Assert.Equal(VipsInterpretation.CMYK, img.Interpretation);

        var got = img.PixelsLazy!.Value;
        Assert.Equal(px.Length, got.Length);
        for (int i = 0; i < px.Length; i++) Assert.Equal(px[i], got[i]);
    }

    [Fact]
    public void Pure_CmykLzw_RoundTrips()
    {
        int w = 32, h = 16;
        var px = BuildCmykPixels(w, h);
        var tiff = BuildCmykTiff(px, w, h, CompressionMethod.LZW);
        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        Assert.Equal(VipsInterpretation.CMYK, img!.Interpretation);
        var got = img.PixelsLazy!.Value;
        for (int i = 0; i < px.Length; i++) Assert.Equal(px[i], got[i]);
    }

    [Fact]
    public async Task LoadAsync_CmykTiff_TakesPureFastPath()
    {
        int w = 16, h = 8;
        var px = BuildCmykPixels(w, h);
        var tiff = BuildCmykTiff(px, w, h, CompressionMethod.LZW);
        var src = new PipeVipsSource(PipeReader.Create(new MemoryStream(tiff)));
        var img = await VipsTiffLoader.LoadAsync(src);
        Assert.NotNull(img);
        Assert.Equal(4, img!.Bands);
        Assert.Equal(VipsInterpretation.CMYK, img.Interpretation);
    }
}
