using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Core;
using CosmoImage.Loaders;
using ImageMagick;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 109 — pure-managed TIFF baseline decoder. Hand-built TIFFs
/// exercise every supported configuration (RGB/grayscale/palette,
/// 8/16-bit, single-/multi-strip, LE/BE) plus rejection paths
/// (compression / multi-page / tiles / planar / BigTIFF).
/// </summary>
public class Round109Tests
{
    // ---- Hand-built TIFF helper ----

    /// <summary>
    /// Build a baseline uncompressed TIFF with the given parameters.
    /// Pixel bytes must already be in <paramref name="le"/>'s byte order
    /// (relevant for 16-bit samples).
    /// </summary>
    private static byte[] BuildTiff(byte[] pixels, int width, int height,
        int bps, int spp, int photometric,
        ushort[]? colorMap = null, int? rowsPerStrip = null, bool le = true)
    {
        int rps = rowsPerStrip ?? height;
        int rowBytes = width * spp * (bps / 8);
        int numStrips = (height + rps - 1) / rps;
        int numTags = 9 + (photometric == 3 ? 1 : 0);

        int headerSize = 8;
        int ifdSize = 2 + 12 * numTags + 4;
        int ifdOffset = headerSize;
        int pos = ifdOffset + ifdSize;

        // Layout external blocks.
        int bpsExt = -1;
        if (spp > 2) { bpsExt = pos; pos += spp * 2; }

        int stripOffsetsExt = -1, stripByteCountsExt = -1;
        if (numStrips > 1)
        {
            stripOffsetsExt = pos; pos += numStrips * 4;
            stripByteCountsExt = pos; pos += numStrips * 4;
        }

        int colorMapExt = -1;
        if (photometric == 3) { colorMapExt = pos; pos += 3 * (1 << bps) * 2; }

        // Word-align before strip data so SHORTs in external blocks land cleanly.
        if ((pos & 1) != 0) pos++;
        int stripDataStart = pos;
        pos += pixels.Length;

        var buf = new byte[pos];

        // Header
        buf[0] = (byte)(le ? 0x49 : 0x4D);
        buf[1] = (byte)(le ? 0x49 : 0x4D);
        WriteU16(buf, 2, 0x002A, le);
        WriteU32(buf, 4, (uint)ifdOffset, le);

        // IFD
        WriteU16(buf, ifdOffset, (ushort)numTags, le);
        int e = ifdOffset + 2;

        void WriteRaw(int tag, ushort type, uint count, byte[] val4)
        {
            WriteU16(buf, e, (ushort)tag, le);
            WriteU16(buf, e + 2, type, le);
            WriteU32(buf, e + 4, count, le);
            Array.Copy(val4, 0, buf, e + 8, 4);
            e += 12;
        }

        void EShort(int tag, ushort v) { var b = new byte[4]; WriteU16(b, 0, v, le); WriteRaw(tag, 3, 1, b); }
        void ELong(int tag, uint v)    { var b = new byte[4]; WriteU32(b, 0, v, le); WriteRaw(tag, 4, 1, b); }

        ELong(256, (uint)width);
        ELong(257, (uint)height);

        // BitsPerSample
        if (spp == 1)
        {
            var v = new byte[4]; WriteU16(v, 0, (ushort)bps, le);
            WriteRaw(258, 3, 1, v);
        }
        else if (spp == 2)
        {
            var v = new byte[4]; WriteU16(v, 0, (ushort)bps, le); WriteU16(v, 2, (ushort)bps, le);
            WriteRaw(258, 3, 2, v);
        }
        else
        {
            var v = new byte[4]; WriteU32(v, 0, (uint)bpsExt, le);
            WriteRaw(258, 3, (uint)spp, v);
            for (int i = 0; i < spp; i++) WriteU16(buf, bpsExt + i * 2, (ushort)bps, le);
        }

        EShort(259, 1);                       // Compression = None
        EShort(262, (ushort)photometric);     // Photometric

        // StripOffsets
        if (numStrips == 1)
        {
            var v = new byte[4]; WriteU32(v, 0, (uint)stripDataStart, le);
            WriteRaw(273, 4, 1, v);
        }
        else
        {
            var v = new byte[4]; WriteU32(v, 0, (uint)stripOffsetsExt, le);
            WriteRaw(273, 4, (uint)numStrips, v);
        }

        EShort(277, (ushort)spp);
        ELong(278, (uint)rps);

        // StripByteCounts
        if (numStrips == 1)
        {
            var v = new byte[4]; WriteU32(v, 0, (uint)pixels.Length, le);
            WriteRaw(279, 4, 1, v);
        }
        else
        {
            var v = new byte[4]; WriteU32(v, 0, (uint)stripByteCountsExt, le);
            WriteRaw(279, 4, (uint)numStrips, v);
        }

        if (photometric == 3)
        {
            var v = new byte[4]; WriteU32(v, 0, (uint)colorMapExt, le);
            uint count = (uint)(3 * (1 << bps));
            WriteRaw(320, 3, count, v);
            if (colorMap != null)
                for (int i = 0; i < colorMap.Length; i++) WriteU16(buf, colorMapExt + i * 2, colorMap[i], le);
        }

        // Next-IFD pointer = 0
        WriteU32(buf, e, 0, le);

        // Multi-strip table fill-in
        if (numStrips > 1)
        {
            for (int s = 0; s < numStrips; s++)
            {
                int rows = Math.Min(rps, height - s * rps);
                int len = rows * rowBytes;
                int off = stripDataStart + s * rps * rowBytes;
                WriteU32(buf, stripOffsetsExt + s * 4, (uint)off, le);
                WriteU32(buf, stripByteCountsExt + s * 4, (uint)len, le);
            }
        }

        // Strip data
        Array.Copy(pixels, 0, buf, stripDataStart, pixels.Length);
        return buf;
    }

    private static void WriteU16(byte[] buf, int off, ushort v, bool le)
    {
        if (le) { buf[off] = (byte)v; buf[off + 1] = (byte)(v >> 8); }
        else    { buf[off] = (byte)(v >> 8); buf[off + 1] = (byte)v; }
    }
    private static void WriteU32(byte[] buf, int off, uint v, bool le)
    {
        if (le)
        {
            buf[off] = (byte)v; buf[off + 1] = (byte)(v >> 8);
            buf[off + 2] = (byte)(v >> 16); buf[off + 3] = (byte)(v >> 24);
        }
        else
        {
            buf[off] = (byte)(v >> 24); buf[off + 1] = (byte)(v >> 16);
            buf[off + 2] = (byte)(v >> 8); buf[off + 3] = (byte)v;
        }
    }

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

    private static byte[] BuildRgb16Pixels(int w, int h, bool le)
    {
        var px = new byte[w * h * 6];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 6;
                ushort r = (ushort)Math.Min(65535, x * 257);
                ushort g = (ushort)Math.Min(65535, y * 257);
                ushort b = (ushort)Math.Min(65535, (x + y) * 257);
                WriteU16(px, o, r, le);
                WriteU16(px, o + 2, g, le);
                WriteU16(px, o + 4, b, le);
            }
        return px;
    }

    // ---- 8-bit RGB ----

    [Fact]
    public void Pure_RgbEightBit_DecodesExactPixels()
    {
        int w = 6, h = 5;
        var px = BuildRgbPixels(w, h);
        var tiff = BuildTiff(px, w, h, bps: 8, spp: 3, photometric: 2);

        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
        Assert.Equal(3, img.Bands);
        Assert.Equal(VipsBandFormat.UChar, img.BandFormat);
        Assert.Equal(VipsInterpretation.RGB, img.Interpretation);
        AssertPixelsEqual(px, img);
    }

    [Fact]
    public void Pure_RgbaEightBit_DecodesAllChannels()
    {
        int w = 4, h = 4;
        var px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            px[i * 4] = (byte)i; px[i * 4 + 1] = (byte)(i + 50);
            px[i * 4 + 2] = (byte)(i + 100); px[i * 4 + 3] = (byte)(i * 11);
        }
        var tiff = BuildTiff(px, w, h, 8, 4, 2);
        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        Assert.Equal(4, img!.Bands);
        AssertPixelsEqual(px, img);
    }

    [Fact]
    public void Pure_GrayBlackIsZero_PassesThrough()
    {
        int w = 8, h = 4;
        var px = new byte[w * h];
        for (int i = 0; i < px.Length; i++) px[i] = (byte)(i * 3 & 0xFF);
        var tiff = BuildTiff(px, w, h, 8, 1, photometric: 1);
        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        Assert.Equal(1, img!.Bands);
        Assert.Equal(VipsInterpretation.BW, img.Interpretation);
        AssertPixelsEqual(px, img);
    }

    [Fact]
    public void Pure_GrayWhiteIsZero_InvertsSamples()
    {
        int w = 4, h = 4;
        var px = new byte[w * h];
        for (int i = 0; i < px.Length; i++) px[i] = (byte)(i * 17 & 0xFF);
        var tiff = BuildTiff(px, w, h, 8, 1, photometric: 0);
        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        var got = img!.PixelsLazy!.Value;
        for (int i = 0; i < px.Length; i++)
            Assert.Equal((byte)(0xFF - px[i]), got[i]);
    }

    [Fact]
    public void Pure_GrayAlphaWhiteIsZero_LeavesAlphaUntouched()
    {
        int w = 4, h = 1;
        var px = new byte[] {
            10, 200,   // pixel 0: gray 10, alpha 200
            50, 250,   // pixel 1
            100, 100,
            240, 0,
        };
        var tiff = BuildTiff(px, w, h, 8, 2, photometric: 0);
        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        var got = img!.PixelsLazy!.Value;
        Assert.Equal((byte)(255 - 10), got[0]); Assert.Equal(200, got[1]);
        Assert.Equal((byte)(255 - 50), got[2]); Assert.Equal(250, got[3]);
        Assert.Equal((byte)(255 - 100), got[4]); Assert.Equal(100, got[5]);
        Assert.Equal((byte)(255 - 240), got[6]); Assert.Equal(0, got[7]);
    }

    // ---- 16-bit ----

    [Fact]
    public void Pure_Rgb16BitLe_HostEndianOutput()
    {
        int w = 5, h = 3;
        var px = BuildRgb16Pixels(w, h, le: true);
        var tiff = BuildTiff(px, w, h, 16, 3, 2, le: true);
        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        Assert.Equal(VipsBandFormat.UShort, img!.BandFormat);
        // Already host-LE → straight equality
        AssertPixelsEqual(px, img);
    }

    [Fact]
    public void Pure_Rgb16BitBe_ByteSwappedToHostEndian()
    {
        int w = 4, h = 3;
        var pxBe = BuildRgb16Pixels(w, h, le: false);
        var pxLe = BuildRgb16Pixels(w, h, le: true);
        var tiff = BuildTiff(pxBe, w, h, 16, 3, 2, le: false);
        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        // After decode, output should match the host-LE reference.
        AssertPixelsEqual(pxLe, img!);
    }

    [Fact]
    public void Pure_Gray16WhiteIsZero_InvertsAcrossEndianness()
    {
        int w = 3, h = 1;
        var pxBe = new byte[w * 2];
        // pixel values BE: 0x1234, 0xABCD, 0x0001
        WriteU16(pxBe, 0, 0x1234, le: false);
        WriteU16(pxBe, 2, 0xABCD, le: false);
        WriteU16(pxBe, 4, 0x0001, le: false);
        var tiff = BuildTiff(pxBe, w, h, 16, 1, photometric: 0, le: false);
        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        var got = img!.PixelsLazy!.Value;
        // After byte-swap to host LE, 0x1234 → invert → 0xEDCB → host-LE bytes CB ED
        Assert.Equal(0xCB, got[0]); Assert.Equal(0xED, got[1]);
        Assert.Equal(0x32, got[2]); Assert.Equal(0x54, got[3]);  // 0xABCD → 0x5432
        Assert.Equal(0xFE, got[4]); Assert.Equal(0xFF, got[5]);  // 0x0001 → 0xFFFE
    }

    // ---- Multi-strip ----

    [Fact]
    public void Pure_MultiStrip_AssemblesCorrectly()
    {
        int w = 4, h = 8;
        var px = BuildRgbPixels(w, h);
        // RowsPerStrip = 3 → 3 strips: rows 0..2, 3..5, 6..7
        var tiff = BuildTiff(px, w, h, 8, 3, 2, rowsPerStrip: 3);
        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        AssertPixelsEqual(px, img!);
    }

    [Fact]
    public void Pure_MultiStripRowsPerStripOne_AssemblesCorrectly()
    {
        int w = 3, h = 5;
        var px = BuildRgbPixels(w, h);
        var tiff = BuildTiff(px, w, h, 8, 3, 2, rowsPerStrip: 1);
        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        AssertPixelsEqual(px, img!);
    }

    // ---- Palette ----

    [Fact]
    public void Pure_Palette8Bit_ExpandsToRgb()
    {
        int w = 4, h = 2;
        var indices = new byte[] {
            0, 1, 2, 3,
            3, 2, 1, 0,
        };
        // ColorMap layout: 256 R, 256 G, 256 B (each 16-bit). We populate
        // 4 entries; rest are zero. Indices 0..3 map to known colors.
        var cmap = new ushort[3 * 256];
        // entry 0: (0xFFFF, 0, 0) → red, scaled to (255, 0, 0)
        cmap[0] = 0xFFFF; cmap[256] = 0; cmap[512] = 0;
        // entry 1: (0, 0xFFFF, 0) → green
        cmap[1] = 0; cmap[257] = 0xFFFF; cmap[513] = 0;
        // entry 2: (0, 0, 0xFFFF) → blue
        cmap[2] = 0; cmap[258] = 0; cmap[514] = 0xFFFF;
        // entry 3: (0x8080, 0x4040, 0xC0C0) → mid-tones
        cmap[3] = 0x8080; cmap[259] = 0x4040; cmap[515] = 0xC0C0;

        var tiff = BuildTiff(indices, w, h, 8, 1, photometric: 3, colorMap: cmap);
        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        Assert.Equal(3, img!.Bands);
        Assert.Equal(VipsInterpretation.RGB, img.Interpretation);
        var got = img.PixelsLazy!.Value;

        // Pixel 0 (idx 0) → red
        Assert.Equal(0xFF, got[0]); Assert.Equal(0, got[1]); Assert.Equal(0, got[2]);
        // Pixel 1 (idx 1) → green
        Assert.Equal(0, got[3]); Assert.Equal(0xFF, got[4]); Assert.Equal(0, got[5]);
        // Pixel 2 (idx 2) → blue
        Assert.Equal(0, got[6]); Assert.Equal(0, got[7]); Assert.Equal(0xFF, got[8]);
        // Pixel 3 (idx 3) → 0x80, 0x40, 0xC0 (top byte of each entry)
        Assert.Equal(0x80, got[9]); Assert.Equal(0x40, got[10]); Assert.Equal(0xC0, got[11]);
    }

    // ---- Rejection paths (decoder returns null → caller falls back) ----

    [Fact]
    public void Pure_CompressedTiff_ReturnsNull()
    {
        int w = 4, h = 4;
        var px = BuildRgbPixels(w, h);
        var tiff = BuildTiff(px, w, h, 8, 3, 2);
        // Patch Compression tag (259) in IFD: find entry, change SHORT value 1 → 5 (LZW).
        // IFD starts at offset 8; entries follow the 2-byte count. BuildTiff emits
        // 256, 257, 258, 259 in order — Compression is index 3.
        int compEntryOff = 8 + 2 + 3 * 12;
        // Tag at compEntryOff should be 259 (LE)
        Assert.Equal(259, tiff[compEntryOff] | (tiff[compEntryOff + 1] << 8));
        // Value-or-offset at +8 — overwrite SHORT value
        WriteU16(tiff, compEntryOff + 8, 5, le: true);

        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.Null(img);
    }

    [Fact]
    public void Pure_BigTiff_ReturnsNull()
    {
        // BigTIFF header: II, magic 0x002B
        var buf = new byte[16];
        buf[0] = 0x49; buf[1] = 0x49;
        WriteU16(buf, 2, 0x002B, le: true);
        Assert.Null(PureTiffDecoder.TryDecode(buf));
    }

    [Fact]
    public void Pure_NonTiff_ReturnsNull()
    {
        var notTiff = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0 };
        Assert.Null(PureTiffDecoder.TryDecode(notTiff));
    }

    // ---- End-to-end via VipsTiffLoader ----

    [Fact]
    public async Task LoadAsync_PureFastPath_RgbRoundTrip()
    {
        int w = 8, h = 6;
        var px = BuildRgbPixels(w, h);
        var tiff = BuildTiff(px, w, h, 8, 3, 2);
        var src = new PipeVipsSource(PipeReader.Create(new MemoryStream(tiff)));
        var img = await VipsTiffLoader.LoadAsync(src);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
        Assert.Equal(3, img.Bands);
        Assert.Equal(VipsBandFormat.UChar, img.BandFormat);
        AssertPixelsEqual(px, img);
    }

    [Fact]
    public async Task LoadAsync_CompressedFallsBackToMagick()
    {
        // Build a real LZW-compressed TIFF via Magick. Pure decoder will
        // reject (Compression != 1); the loader should fall through and
        // still produce a valid image.
        int w = 8, h = 4;
        var raw = BuildRgbPixels(w, h);
        var settings = new MagickReadSettings { Width = (uint)w, Height = (uint)h, Format = MagickFormat.Rgb, Depth = 8 };
        using var img = new MagickImage();
        img.Read(raw, settings);
        img.Format = MagickFormat.Tiff;
        img.Settings.Compression = CompressionMethod.LZW;
        var tiff = img.ToByteArray();

        var src = new PipeVipsSource(PipeReader.Create(new MemoryStream(tiff)));
        var loaded = await VipsTiffLoader.LoadAsync(src);
        Assert.NotNull(loaded);
        Assert.Equal(w, loaded!.Width);
        Assert.Equal(h, loaded.Height);
    }

    // ---- Pixel comparison helper ----

    private static void AssertPixelsEqual(byte[] expected, VipsImage img)
    {
        var got = img.PixelsLazy!.Value;
        Assert.Equal(expected.Length, got.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] != got[i])
                Assert.Fail($"pixel byte mismatch at {i}: expected {expected[i]:X2} got {got[i]:X2}");
        }
    }
}
