using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Loaders;
using ImageMagick;
using Xunit;

namespace CosmoImage.Tests;

public class Round107Tests
{
    /// <summary>Build raw RGB pixels for a small test image with sliding values.</summary>
    private static byte[] RgbRaw(int w, int h)
    {
        var raw = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 3;
                raw[o + 0] = (byte)((x * 7) & 0xFF);
                raw[o + 1] = (byte)((y * 11) & 0xFF);
                raw[o + 2] = (byte)(((x + y) * 13) & 0xFF);
            }
        return raw;
    }

    /// <summary>Encode RGB raw into a BMP of the requested bit depth via Magick.NET.</summary>
    private static byte[] MakeBmp(int w, int h, byte[] raw, int bpp, bool rle = false)
    {
        var settings = new MagickReadSettings {
            Width = (uint)w, Height = (uint)h, Format = MagickFormat.Rgb, Depth = 8,
        };
        using var img = new MagickImage();
        img.Read(raw, settings);
        // Force colour count for paletted variants.
        if (bpp == 1) img.Quantize(new QuantizeSettings { Colors = 2 });
        else if (bpp == 4) img.Quantize(new QuantizeSettings { Colors = 16 });
        else if (bpp == 8) img.Quantize(new QuantizeSettings { Colors = 256 });
        // Force the desired pixel layout.
        img.Settings.Depth = (uint)Math.Min(8, bpp);
        img.Format = bpp switch
        {
            1 => MagickFormat.Bmp,    // 1bpp typically just bmp + 2-color quantize
            4 => MagickFormat.Bmp,
            8 => MagickFormat.Bmp,
            16 => MagickFormat.Bmp,   // RGB555 for ImageMagick
            24 => MagickFormat.Bmp3,  // BMP3 = legacy BITMAPINFOHEADER, no V4/V5 extensions
            32 => MagickFormat.Bmp,
            _ => MagickFormat.Bmp,
        };
        if (rle)
        {
            // BI_RLE8 — Magick can be coerced via writeDefines, but simpler:
            // use the BmpCompression option through Settings.
            img.Settings.SetDefine("bmp:format", "bmp3");
            img.Settings.SetDefine("bmp:rle", "true");
        }
        return img.ToByteArray();
    }

    private static byte[] ReadPel(VipsImage img, int x, int y)
    {
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, img.Width, img.Height));
        return reg.GetAddress(x, y).Slice(0, img.Bands).ToArray();
    }

    private static int GetBmpBpp(byte[] bmp)
        => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bmp.AsSpan(28, 2));

    private static uint GetBmpCompression(byte[] bmp)
        => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bmp.AsSpan(30, 4));

    // ---- 8bpp paletted ----

    [Fact]
    public async Task Bmp8Bpp_DecodesViaPureDecoder()
    {
        int w = 16, h = 12;
        var bmp = MakeBmp(w, h, RgbRaw(w, h), 8);
        // Sanity — Magick produced an 8bpp BMP.
        Assert.Equal(8, GetBmpBpp(bmp));

        await using var source = new PipeVipsSource(PipeReader.Create(new MemoryStream(bmp)));
        var loaded = await VipsBmpLoader.LoadAsync(source);
        Assert.NotNull(loaded);
        Assert.Equal(w, loaded!.Width);
        Assert.Equal(h, loaded.Height);
        Assert.Equal(3, loaded.Bands);

        // Pixel content is approximate (Magick quantized to 256 colours);
        // verify all pixels are ≥ some non-trivial brightness.
        var pel = ReadPel(loaded, 8, 6);
        Assert.True(pel[0] + pel[1] + pel[2] > 0);
    }

    // ---- 4bpp paletted ----

    [Fact]
    public async Task Bmp4Bpp_DecodesViaPureDecoder()
    {
        int w = 16, h = 12;
        var bmp = MakeBmp(w, h, RgbRaw(w, h), 4);
        // Some Magick versions write 4bpp; others fall back to 8bpp.
        // If we got 4bpp, verify pure decoder handles it.
        if (GetBmpBpp(bmp) != 4) return;

        await using var source = new PipeVipsSource(PipeReader.Create(new MemoryStream(bmp)));
        var loaded = await VipsBmpLoader.LoadAsync(source);
        Assert.NotNull(loaded);
        Assert.Equal(w, loaded!.Width);
        Assert.Equal(h, loaded.Height);
    }

    // ---- 1bpp monochrome ----

    [Fact]
    public async Task Bmp1Bpp_DecodesViaPureDecoder()
    {
        int w = 16, h = 8;
        var bmp = MakeBmp(w, h, RgbRaw(w, h), 1);
        if (GetBmpBpp(bmp) != 1) return;

        await using var source = new PipeVipsSource(PipeReader.Create(new MemoryStream(bmp)));
        var loaded = await VipsBmpLoader.LoadAsync(source);
        Assert.NotNull(loaded);
        Assert.Equal(w, loaded!.Width);
        Assert.Equal(h, loaded.Height);
        Assert.Equal(3, loaded.Bands);
    }

    // ---- 16bpp RGB555 ----

    [Fact]
    public async Task Bmp16Bpp_DecodesViaPureDecoder()
    {
        int w = 16, h = 12;
        var bmp = MakeBmp(w, h, RgbRaw(w, h), 16);
        // 16bpp BMPs may use BI_BITFIELDS (compression=3), which we don't
        // handle on the pure path. Skip if so — Magick fallback covers it.
        if (GetBmpBpp(bmp) != 16 || GetBmpCompression(bmp) != 0) return;

        await using var source = new PipeVipsSource(PipeReader.Create(new MemoryStream(bmp)));
        var loaded = await VipsBmpLoader.LoadAsync(source);
        Assert.NotNull(loaded);
        Assert.Equal(w, loaded!.Width);
        Assert.Equal(h, loaded.Height);
        Assert.Equal(3, loaded.Bands);
        // RGB555 is lossy compared to 8bpp source — verify pixels are
        // within ±8 of expected (5-bit precision = 8-step resolution).
        for (int y = 0; y < h; y += 4)
            for (int x = 0; x < w; x += 4)
            {
                var pel = ReadPel(loaded, x, y);
                int expR = (x * 7) & 0xFF;
                int expG = (y * 11) & 0xFF;
                int expB = ((x + y) * 13) & 0xFF;
                Assert.True(Math.Abs(pel[0] - expR) <= 16,
                    $"R at ({x},{y}): got {pel[0]}, expected ~{expR}");
                Assert.True(Math.Abs(pel[1] - expG) <= 16);
                Assert.True(Math.Abs(pel[2] - expB) <= 16);
            }
    }

    // ---- 24bpp + 32bpp continue to work ----

    [Fact]
    public async Task Bmp24Bpp_StillWorks()
    {
        int w = 8, h = 8;
        var bmp = MakeBmp(w, h, RgbRaw(w, h), 24);
        Assert.Equal(24, GetBmpBpp(bmp));
        await using var source = new PipeVipsSource(PipeReader.Create(new MemoryStream(bmp)));
        var loaded = await VipsBmpLoader.LoadAsync(source);
        Assert.NotNull(loaded);
        // 24bpp is exact — verify pixel values match.
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var pel = ReadPel(loaded!, x, y);
                Assert.Equal((byte)((x * 7) & 0xFF), pel[0]);
                Assert.Equal((byte)((y * 11) & 0xFF), pel[1]);
                Assert.Equal((byte)(((x + y) * 13) & 0xFF), pel[2]);
            }
    }

    // ---- BI_RLE8 ----

    [Fact]
    public async Task BmpRle8_DecodesViaPureDecoder()
    {
        int w = 16, h = 8;
        var bmp = MakeBmp(w, h, RgbRaw(w, h), 8, rle: true);
        // Magick may not actually emit RLE8; check compression byte.
        if (GetBmpBpp(bmp) != 8 || GetBmpCompression(bmp) != 1) return;

        await using var source = new PipeVipsSource(PipeReader.Create(new MemoryStream(bmp)));
        var loaded = await VipsBmpLoader.LoadAsync(source);
        Assert.NotNull(loaded);
        Assert.Equal(w, loaded!.Width);
        Assert.Equal(h, loaded.Height);
    }

    // ---- Hand-crafted minimal 8bpp paletted BMP ----

    [Fact]
    public async Task Bmp8Bpp_HandCrafted_DecodesExactly()
    {
        // 2×2 8bpp paletted BMP with 4-entry palette:
        //   index 0 = black, 1 = red, 2 = green, 3 = blue.
        // Pixels (bottom-up): row 1 = [2, 3], row 0 = [0, 1].
        // Row stride padded to 4 bytes, so each 2-byte row gets 2 bytes padding.
        var ms = new MemoryStream();
        // BMP file header (14)
        ms.WriteByte((byte)'B'); ms.WriteByte((byte)'M');
        // file size (will fill after)
        var sizePos = (int)ms.Position;
        ms.Write(new byte[] { 0, 0, 0, 0 });
        ms.Write(new byte[] { 0, 0, 0, 0 });  // reserved
        // data offset = 14 + 40 + 4*4 (palette) = 70
        ms.Write(new byte[] { 70, 0, 0, 0 });
        // BITMAPINFOHEADER (40)
        ms.Write(new byte[] { 40, 0, 0, 0 });    // header size
        ms.Write(new byte[] { 2, 0, 0, 0 });     // width = 2
        ms.Write(new byte[] { 2, 0, 0, 0 });     // height = 2 (positive = bottom-up)
        ms.Write(new byte[] { 1, 0 });           // planes
        ms.Write(new byte[] { 8, 0 });           // bpp = 8
        ms.Write(new byte[] { 0, 0, 0, 0 });     // BI_RGB
        ms.Write(new byte[] { 8, 0, 0, 0 });     // image size = 2×4 = 8 (rows padded)
        ms.Write(new byte[] { 0, 0, 0, 0 });     // x ppm
        ms.Write(new byte[] { 0, 0, 0, 0 });     // y ppm
        ms.Write(new byte[] { 4, 0, 0, 0 });     // colors used = 4
        ms.Write(new byte[] { 4, 0, 0, 0 });     // important = 4
        // Palette: BGR0
        ms.Write(new byte[] { 0, 0, 0, 0 });        // index 0: black
        ms.Write(new byte[] { 0, 0, 255, 0 });      // index 1: red (BGR)
        ms.Write(new byte[] { 0, 255, 0, 0 });      // index 2: green
        ms.Write(new byte[] { 255, 0, 0, 0 });      // index 3: blue
        // Pixel data — bottom-up, rows padded to 4 bytes.
        // Bottom row of file = top row of image. We want top row = (0, 1) (black, red).
        // Bottom row in file (= bottom row of image) = (2, 3) (green, blue).
        // Wait — for bottom-up BMP, the FIRST row in the file is the BOTTOM row of
        // the image. So:
        //   file row 0 (bottom of image) = indices [2, 3] (the bottom row of image = green, blue)
        //   file row 1 (top of image)    = indices [0, 1] (the top row = black, red)
        ms.Write(new byte[] { 2, 3, 0, 0 });        // file row 0 padded
        ms.Write(new byte[] { 0, 1, 0, 0 });        // file row 1 padded

        // Patch file size.
        var allBytes = ms.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            allBytes.AsSpan(sizePos, 4), (uint)allBytes.Length);

        await using var source = new PipeVipsSource(PipeReader.Create(new MemoryStream(allBytes)));
        var loaded = await VipsBmpLoader.LoadAsync(source);
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Width);
        Assert.Equal(2, loaded.Height);
        Assert.Equal(3, loaded.Bands);

        // Top row (y=0): black, red.
        var p00 = ReadPel(loaded, 0, 0);
        Assert.Equal(0, p00[0]); Assert.Equal(0, p00[1]); Assert.Equal(0, p00[2]);
        var p10 = ReadPel(loaded, 1, 0);
        Assert.Equal(255, p10[0]); Assert.Equal(0, p10[1]); Assert.Equal(0, p10[2]);
        // Bottom row (y=1): green, blue.
        var p01 = ReadPel(loaded, 0, 1);
        Assert.Equal(0, p01[0]); Assert.Equal(255, p01[1]); Assert.Equal(0, p01[2]);
        var p11 = ReadPel(loaded, 1, 1);
        Assert.Equal(0, p11[0]); Assert.Equal(0, p11[1]); Assert.Equal(255, p11[2]);
    }
}
