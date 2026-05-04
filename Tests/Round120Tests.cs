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
/// Round 120 — VP8X-wrapped VP8L WebPs (lossless image with embedded
/// ICC / EXIF / XMP metadata). The pure decoder now walks past the
/// VP8X header + metadata chunks to find and decode the inner VP8L
/// chunk, instead of bailing the moment it sees VP8X.
/// </summary>
public class Round120Tests
{
    private static byte[] BuildRgbaPixels(int w, int h)
    {
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

    /// <summary>
    /// Build a lossless WebP whose ICC profile metadata forces Magick to
    /// emit a VP8X wrapper instead of a bare VP8L file.
    /// </summary>
    private static byte[] BuildLosslessWebpWithIcc(byte[] rgba, int w, int h)
    {
        var settings = new MagickReadSettings { Width = (uint)w, Height = (uint)h, Format = MagickFormat.Rgba, Depth = 8 };
        using var img = new MagickImage();
        img.Read(rgba, settings);
        img.SetProfile(new ColorProfile(ColorProfiles.SRGB.ToByteArray()));
        img.Format = MagickFormat.WebP;
        img.Settings.SetDefine(MagickFormat.WebP, "lossless", "true");
        img.Quality = 100;
        return img.ToByteArray();
    }

    [Fact]
    public void Pure_VP8XWrappedVP8L_DecodesPixels()
    {
        int w = 16, h = 8;
        var px = BuildRgbaPixels(w, h);
        var webp = BuildLosslessWebpWithIcc(px, w, h);

        // Sanity: must contain a VP8X chunk (0x58385056 LE = 'VP8X').
        // Walk the RIFF chunks looking for it.
        bool hasVp8x = false;
        int p = 12;
        while (p + 8 <= webp.Length)
        {
            uint fourcc = BitConverter.ToUInt32(webp, p);
            int len = BitConverter.ToInt32(webp, p + 4);
            if (fourcc == 0x58385056) { hasVp8x = true; break; }
            p += 8 + len + (len & 1);
        }
        Assert.True(hasVp8x, "expected Magick to wrap the lossless WebP in VP8X when an ICC profile is attached");

        var img = PureWebpLossless.TryDecode(webp);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
        Assert.Equal(4, img.Bands);
        var got = img.PixelsLazy!.Value;
        for (int i = 0; i < px.Length; i++) Assert.Equal(px[i], got[i]);
    }

    [Fact]
    public async Task LoadAsync_VP8XWrappedVP8L_TakesPureFastPathAndKeepsIcc()
    {
        int w = 16, h = 8;
        var px = BuildRgbaPixels(w, h);
        var webp = BuildLosslessWebpWithIcc(px, w, h);
        var src = new PipeVipsSource(PipeReader.Create(new MemoryStream(webp)));
        var img = await VipsWebpLoader.LoadAsync(src);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(4, img.Bands);
        // The Magick metadata extraction (AttachWebpMetadata) should have
        // populated the ICC profile blob even on the pure fast path.
        Assert.True(img.MetadataBlobs.ContainsKey("icc"));
        Assert.NotEmpty(img.MetadataBlobs["icc"]);
    }
}
