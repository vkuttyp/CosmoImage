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
/// Round 117 — 32-bit IEEE float sample TIFFs (SampleFormat=3,
/// BitsPerSample=32). Common in HDR / scientific / GIS pipelines
/// where uint8/16 quantization throws away dynamic range.
/// </summary>
public class Round117Tests
{
    /// <summary>
    /// Hand-build a single-strip uncompressed float TIFF. Pixels are
    /// already host-LE float bytes; the helper writes them through
    /// without touching them, so the round-trip is bit-exact.
    /// </summary>
    private static byte[] BuildFloatTiff(float[] pixels, int width, int height, int spp,
        int photometric, bool le = true)
    {
        var pix = new byte[pixels.Length * 4];
        Buffer.BlockCopy(pixels, 0, pix, 0, pix.Length);
        if (!le)
        {
            // Test fixture: byte-swap to TIFF's stated byte order.
            for (int i = 0; i + 3 < pix.Length; i += 4)
            {
                (pix[i], pix[i + 3]) = (pix[i + 3], pix[i]);
                (pix[i + 1], pix[i + 2]) = (pix[i + 2], pix[i + 1]);
            }
        }

        // Same hand-builder pattern as Round 109/110: build a minimal
        // single-strip IFD with the additional SampleFormat tag.
        int numTags = 10;
        int headerSize = 8;
        int ifdSize = 2 + 12 * numTags + 4;
        int ifdOffset = headerSize;
        int pos = ifdOffset + ifdSize;

        // BitsPerSample is external when spp > 2 (each entry is 2 bytes).
        int bpsExt = -1;
        if (spp > 2) { bpsExt = pos; pos += spp * 2; }
        // SampleFormat too (one SHORT per channel).
        int sfExt = -1;
        if (spp > 2) { sfExt = pos; pos += spp * 2; }
        if ((pos & 1) != 0) pos++;
        int stripDataStart = pos;
        pos += pix.Length;

        var buf = new byte[pos];
        buf[0] = (byte)(le ? 0x49 : 0x4D);
        buf[1] = (byte)(le ? 0x49 : 0x4D);
        WriteU16(buf, 2, 0x002A, le);
        WriteU32(buf, 4, (uint)ifdOffset, le);
        WriteU16(buf, ifdOffset, (ushort)numTags, le);

        int e = ifdOffset + 2;
        void Entry(int tag, ushort type, uint count, byte[] val4)
        {
            WriteU16(buf, e, (ushort)tag, le);
            WriteU16(buf, e + 2, type, le);
            WriteU32(buf, e + 4, count, le);
            Array.Copy(val4, 0, buf, e + 8, 4);
            e += 12;
        }
        void EShort(int tag, ushort v) { var b = new byte[4]; WriteU16(b, 0, v, le); Entry(tag, 3, 1, b); }
        void ELong(int tag, uint v)    { var b = new byte[4]; WriteU32(b, 0, v, le); Entry(tag, 4, 1, b); }

        ELong(256, (uint)width);
        ELong(257, (uint)height);

        // BitsPerSample = 32 per channel.
        if (spp == 1)
        {
            var v = new byte[4]; WriteU16(v, 0, 32, le); Entry(258, 3, 1, v);
        }
        else if (spp == 2)
        {
            var v = new byte[4]; WriteU16(v, 0, 32, le); WriteU16(v, 2, 32, le);
            Entry(258, 3, 2, v);
        }
        else
        {
            var v = new byte[4]; WriteU32(v, 0, (uint)bpsExt, le);
            Entry(258, 3, (uint)spp, v);
            for (int i = 0; i < spp; i++) WriteU16(buf, bpsExt + i * 2, 32, le);
        }

        EShort(259, 1);                       // Compression = None
        EShort(262, (ushort)photometric);     // Photometric

        var stripOff = new byte[4]; WriteU32(stripOff, 0, (uint)stripDataStart, le);
        Entry(273, 4, 1, stripOff);

        EShort(277, (ushort)spp);
        ELong(278, (uint)height);

        var stripBC = new byte[4]; WriteU32(stripBC, 0, (uint)pix.Length, le);
        Entry(279, 4, 1, stripBC);

        // SampleFormat = 3 (IEEE float) per channel.
        if (spp == 1)
        {
            var v = new byte[4]; WriteU16(v, 0, 3, le); Entry(339, 3, 1, v);
        }
        else if (spp == 2)
        {
            var v = new byte[4]; WriteU16(v, 0, 3, le); WriteU16(v, 2, 3, le);
            Entry(339, 3, 2, v);
        }
        else
        {
            var v = new byte[4]; WriteU32(v, 0, (uint)sfExt, le);
            Entry(339, 3, (uint)spp, v);
            for (int i = 0; i < spp; i++) WriteU16(buf, sfExt + i * 2, 3, le);
        }

        WriteU32(buf, e, 0, le);  // next-IFD = 0
        Array.Copy(pix, 0, buf, stripDataStart, pix.Length);
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

    private static float[] ReadFloats(byte[] raw)
    {
        var result = new float[raw.Length / 4];
        Buffer.BlockCopy(raw, 0, result, 0, raw.Length);
        return result;
    }

    [Fact]
    public void Pure_FloatRgb_RoundTrips()
    {
        int w = 8, h = 4;
        // HDR-ish float pixels with values both inside and outside [0,1].
        var pixels = new float[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 3;
                pixels[o] = x * 0.5f;
                pixels[o + 1] = y * 1.7f - 1.0f;
                pixels[o + 2] = (x + y) * 0.13f;
            }

        var tiff = BuildFloatTiff(pixels, w, h, spp: 3, photometric: 2, le: true);
        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
        Assert.Equal(3, img.Bands);
        Assert.Equal(VipsBandFormat.Float, img.BandFormat);

        var got = ReadFloats(img.PixelsLazy!.Value);
        for (int i = 0; i < pixels.Length; i++)
            Assert.Equal(pixels[i], got[i]);
    }

    [Fact]
    public void Pure_FloatGrayscale_RoundTrips()
    {
        int w = 4, h = 4;
        var pixels = new float[w * h];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = i * 0.07f - 0.3f;

        var tiff = BuildFloatTiff(pixels, w, h, spp: 1, photometric: 1);
        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        Assert.Equal(VipsBandFormat.Float, img!.BandFormat);
        Assert.Equal(VipsInterpretation.BW, img.Interpretation);

        var got = ReadFloats(img.PixelsLazy!.Value);
        for (int i = 0; i < pixels.Length; i++) Assert.Equal(pixels[i], got[i]);
    }

    [Fact]
    public void Pure_FloatBigEndian_ByteSwapsToHostLE()
    {
        int w = 4, h = 2;
        var pixels = new float[w * h * 3];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = i * 0.25f;

        var tiff = BuildFloatTiff(pixels, w, h, spp: 3, photometric: 2, le: false);
        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        Assert.Equal(VipsBandFormat.Float, img!.BandFormat);

        var got = ReadFloats(img.PixelsLazy!.Value);
        for (int i = 0; i < pixels.Length; i++) Assert.Equal(pixels[i], got[i]);
    }

    [Fact]
    public void Pure_FloatPaletteRejected_FallsBack()
    {
        // Float + palette is not a valid combination — pure decoder
        // should reject (return null) for the caller to fall back.
        int w = 4, h = 2;
        var pixels = new float[w * h];
        var tiff = BuildFloatTiff(pixels, w, h, spp: 1, photometric: 3);
        Assert.Null(PureTiffDecoder.TryDecode(tiff));
    }

    [Fact]
    public async Task LoadAsync_FloatTiff_TakesPureFastPath()
    {
        int w = 8, h = 4;
        var pixels = new float[w * h * 3];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = i * 0.1f;
        var tiff = BuildFloatTiff(pixels, w, h, spp: 3, photometric: 2);
        var src = new PipeVipsSource(PipeReader.Create(new MemoryStream(tiff)));
        var img = await VipsTiffLoader.LoadAsync(src);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(VipsBandFormat.Float, img.BandFormat);
    }
}
