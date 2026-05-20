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
/// Round 112 — multi-page TIFF support in the pure decoder. Walks the
/// IFD chain, requires uniform dimensions/bands, and stacks pages
/// vertically (same convention as animated GIF/WebP/HEIF). Sets
/// n-pages + page-height metadata.
/// </summary>
public class Round112Tests
{
    private static byte[] BuildRgbPageBytes(int w, int h, byte tint)
    {
        var px = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 3;
                px[o] = (byte)((x + tint) & 0xFF);
                px[o + 1] = (byte)((y + tint) & 0xFF);
                px[o + 2] = tint;
            }
        return px;
    }

    private static MagickImage BuildPage(int w, int h, byte tint)
    {
        var settings = new MagickReadSettings
        {
            Width = (uint)w, Height = (uint)h, Format = MagickFormat.Rgb, Depth = 8,
        };
        var img = new MagickImage();
        img.Read(BuildRgbPageBytes(w, h, tint), settings);
        return img;
    }

    private static byte[] BuildMultiPageTiff(int w, int h, int pages, CompressionMethod compression)
    {
        using var collection = new MagickImageCollection();
        for (int i = 0; i < pages; i++)
        {
            var page = BuildPage(w, h, (byte)(i * 30));
            page.Format = MagickFormat.Tiff;
            page.Settings.Compression = compression;
            collection.Add(page);
        }
        return collection.ToByteArray(MagickFormat.Tiff);
    }

    [Fact]
    public void Pure_TwoPageUncompressed_StacksVertically()
    {
        int w = 8, h = 4;
        var tiff = BuildMultiPageTiff(w, h, pages: 2, CompressionMethod.NoCompression);
        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h * 2, img.Height);
        Assert.Equal(3, img.Bands);
        Assert.Equal("2", img.Metadata["n-pages"]);
        Assert.Equal(h.ToString(), img.Metadata["page-height"]);

        // Page 0 (rows 0..h-1) carries tint=0; page 1 (rows h..2h-1) tint=30.
        var got = img.PixelsLazy!.Value;
        // Check pixel (0,0) on page 0 — RGB = (tint=0, tint=0, tint=0).
        Assert.Equal(0, got[0]); Assert.Equal(0, got[1]); Assert.Equal(0, got[2]);
        // Check pixel (0,0) on page 1 — RGB = (tint=30, tint=30, tint=30).
        int page1Base = h * w * 3;
        Assert.Equal(30, got[page1Base]); Assert.Equal(30, got[page1Base + 1]); Assert.Equal(30, got[page1Base + 2]);
    }

    [Fact]
    public void Pure_ThreePageLzw_StacksAndPredictorUnwound()
    {
        int w = 16, h = 8;
        var tiff = BuildMultiPageTiff(w, h, pages: 3, CompressionMethod.LZW);
        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h * 3, img.Height);
        Assert.Equal("3", img.Metadata["n-pages"]);

        var got = img.PixelsLazy!.Value;
        // Spot-check pixel (3, 2) on page 1 (tint=30).
        int x = 3, y = 2, page = 1;
        int rowInStack = page * h + y;
        int o = (rowInStack * w + x) * 3;
        Assert.Equal((byte)((x + 30) & 0xFF), got[o]);
        Assert.Equal((byte)((y + 30) & 0xFF), got[o + 1]);
        Assert.Equal(30, got[o + 2]);
    }

    [Fact]
    public async Task LoadAsync_MultiPageTiff_TakesPureFastPath()
    {
        int w = 8, h = 6;
        var tiff = BuildMultiPageTiff(w, h, pages: 2, CompressionMethod.Zip);
        var src = new PipeVipsSource(PipeReader.Create(new MemoryStream(tiff)));
        var img = await VipsTiffLoader.LoadAsync(src);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h * 2, img.Height);
        Assert.Equal(3, img.Bands);
        Assert.Equal("2", img.Metadata["n-pages"]);
    }

    [Fact]
    public async Task LoadAsync_HeterogeneousMultiPage_ReturnsNull()
    {
        // Pages with different dimensions can't be flat-stacked, and the
        // native-only TIFF loader now rejects them instead of collapsing
        // to the first page through Magick.
        using var collection = new MagickImageCollection();
        collection.Add(BuildPage(8, 4, 0));
        collection.Add(BuildPage(16, 8, 60));
        foreach (var p in collection)
        {
            p.Format = MagickFormat.Tiff;
            p.Settings.Compression = CompressionMethod.NoCompression;
        }
        var tiff = collection.ToByteArray(MagickFormat.Tiff);

        Assert.Null(PureTiffDecoder.TryDecode(tiff));

        var src = new PipeVipsSource(PipeReader.Create(new MemoryStream(tiff)));
        var img = await VipsTiffLoader.LoadAsync(src);
        Assert.Null(img);
    }

    [Fact]
    public void Pure_SinglePage_StillBehaves()
    {
        // Sanity: single-page TIFFs must behave exactly as before — no
        // n-pages metadata, exact dimensions match the source.
        int w = 12, h = 8;
        var tiff = BuildMultiPageTiff(w, h, pages: 1, CompressionMethod.NoCompression);
        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
        Assert.False(img.Metadata.ContainsKey("n-pages"));
        Assert.False(img.Metadata.ContainsKey("page-height"));
    }
}
