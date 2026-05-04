using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Text;
using System.Threading.Tasks;
using CosmoImage.Core;
using CosmoImage.Loaders;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 127 — first slice of the OpenEXR arc. Hand-built fixtures
/// validate the header parser and uncompressed scanline decode path
/// for HALF-precision RGB and RGBA EXR files. Subsequent rounds
/// will add RLE / ZIP / PIZ / PXR24 / B44 / DWA compressors and
/// tiled layouts.
/// </summary>
public class Round127Tests
{
    /// <summary>
    /// Hand-build a single-part scanline EXR with NO_COMPRESSION HALF
    /// channels. Channels are emitted in alphabetical order per spec
    /// (so RGBA → A, B, G, R). Caller supplies pixel data as 4-channel
    /// (R, G, B, A) sample-major float; we convert to half and lay out
    /// per-channel-then-per-pixel within each scanline.
    /// </summary>
    private static byte[] BuildScanlineRgbaExr(float[] rgba, int width, int height, bool hasAlpha)
    {
        using var ms = new MemoryStream();
        // Magic + version (single-part scanline, version 2).
        WriteI32(ms, 0x01312F76);
        WriteI32(ms, 2);

        // ---- Attributes ----
        WriteAttribute(ms, "channels", "chlist", buf =>
        {
            // Alphabetical channel order. We always emit B/G/R; A only when requested.
            if (hasAlpha) WriteChannel(buf, "A");
            WriteChannel(buf, "B");
            WriteChannel(buf, "G");
            WriteChannel(buf, "R");
            buf.WriteByte(0);  // chlist terminator
        });
        WriteAttribute(ms, "compression", "compression", buf => buf.WriteByte(0));  // NO_COMPRESSION
        WriteAttribute(ms, "dataWindow", "box2i", buf =>
        {
            WriteI32(buf, 0); WriteI32(buf, 0);
            WriteI32(buf, width - 1); WriteI32(buf, height - 1);
        });
        WriteAttribute(ms, "displayWindow", "box2i", buf =>
        {
            WriteI32(buf, 0); WriteI32(buf, 0);
            WriteI32(buf, width - 1); WriteI32(buf, height - 1);
        });
        WriteAttribute(ms, "lineOrder", "lineOrder", buf => buf.WriteByte(0));  // INCREASING_Y
        WriteAttribute(ms, "pixelAspectRatio", "float", buf => WriteF32(buf, 1.0f));
        WriteAttribute(ms, "screenWindowCenter", "v2f", buf => { WriteF32(buf, 0); WriteF32(buf, 0); });
        WriteAttribute(ms, "screenWindowWidth", "float", buf => WriteF32(buf, 1.0f));
        ms.WriteByte(0);  // attribute list terminator

        // ---- Scanline offset table ----
        // We'll back-patch these once we know each scanline's actual offset.
        long offsetTablePos = ms.Position;
        for (int y = 0; y < height; y++) WriteI64(ms, 0);

        int channelsInFile = hasAlpha ? 4 : 3;

        // ---- Scanline data blocks ----
        for (int y = 0; y < height; y++)
        {
            long blockStart = ms.Position;
            // Patch the offset table.
            long savedPos = ms.Position;
            ms.Position = offsetTablePos + y * 8;
            WriteI64(ms, blockStart);
            ms.Position = savedPos;

            WriteI32(ms, y);                        // yCoord
            int dataSize = channelsInFile * width * 2;
            WriteI32(ms, dataSize);

            // Per-channel layout for this scanline. File order = alphabetical:
            // A (if present), B, G, R.
            if (hasAlpha)
                for (int x = 0; x < width; x++) WriteHalf(ms, rgba[(y * width + x) * 4 + 3]);
            for (int x = 0; x < width; x++) WriteHalf(ms, rgba[(y * width + x) * 4 + 2]);
            for (int x = 0; x < width; x++) WriteHalf(ms, rgba[(y * width + x) * 4 + 1]);
            for (int x = 0; x < width; x++) WriteHalf(ms, rgba[(y * width + x) * 4 + 0]);
        }

        return ms.ToArray();
    }

    private static void WriteChannel(Stream s, string name)
    {
        foreach (char c in name) s.WriteByte((byte)c);
        s.WriteByte(0);
        WriteI32(s, 1);   // pixelType = HALF
        s.WriteByte(0);   // pLinear
        s.WriteByte(0); s.WriteByte(0); s.WriteByte(0);  // reserved
        WriteI32(s, 1);   // xSampling
        WriteI32(s, 1);   // ySampling
    }

    private static void WriteAttribute(Stream s, string name, string type, Action<MemoryStream> writeValue)
    {
        foreach (char c in name) s.WriteByte((byte)c);
        s.WriteByte(0);
        foreach (char c in type) s.WriteByte((byte)c);
        s.WriteByte(0);
        using var ms = new MemoryStream();
        writeValue(ms);
        var data = ms.ToArray();
        WriteI32(s, data.Length);
        s.Write(data, 0, data.Length);
    }

    private static void WriteI32(Stream s, int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); s.Write(b); }
    private static void WriteI64(Stream s, long v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64LittleEndian(b, v); s.Write(b); }
    private static void WriteF32(Stream s, float v) => WriteI32(s, BitConverter.SingleToInt32Bits(v));
    private static void WriteHalf(Stream s, float v)
    {
        ushort h = BitConverter.HalfToUInt16Bits((Half)v);
        Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, h); s.Write(b);
    }

    private static float[] ReadFloats(byte[] raw)
    {
        var f = new float[raw.Length / 4];
        Buffer.BlockCopy(raw, 0, f, 0, raw.Length);
        return f;
    }

    // ---- Tests ----

    [Fact]
    public void Pure_UncompressedRgbHalf_RoundTrips()
    {
        int w = 8, h = 4;
        var rgba = new float[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                rgba[o] = x * 0.1f;
                rgba[o + 1] = y * 0.25f;
                rgba[o + 2] = (x + y) * 0.05f;
                rgba[o + 3] = 1.0f;  // unused since hasAlpha=false
            }
        var exr = BuildScanlineRgbaExr(rgba, w, h, hasAlpha: false);

        var img = PureExrDecoder.TryDecode(exr);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
        Assert.Equal(3, img.Bands);
        Assert.Equal(VipsBandFormat.Float, img.BandFormat);

        var got = ReadFloats(img.PixelsLazy!.Value);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 3;
                // HALF round-trip introduces tiny precision loss; compare with tolerance.
                AssertHalfPrecision(rgba[(y * w + x) * 4], got[o]);
                AssertHalfPrecision(rgba[(y * w + x) * 4 + 1], got[o + 1]);
                AssertHalfPrecision(rgba[(y * w + x) * 4 + 2], got[o + 2]);
            }
    }

    [Fact]
    public void Pure_UncompressedRgbaHalf_DecodesAlpha()
    {
        int w = 4, h = 4;
        var rgba = new float[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            rgba[i * 4]     = i * 0.05f;
            rgba[i * 4 + 1] = i * 0.07f;
            rgba[i * 4 + 2] = i * 0.09f;
            rgba[i * 4 + 3] = i / (float)(w * h);
        }
        var exr = BuildScanlineRgbaExr(rgba, w, h, hasAlpha: true);

        var img = PureExrDecoder.TryDecode(exr);
        Assert.NotNull(img);
        Assert.Equal(4, img!.Bands);

        var got = ReadFloats(img.PixelsLazy!.Value);
        for (int i = 0; i < w * h; i++)
        {
            AssertHalfPrecision(rgba[i * 4],     got[i * 4]);
            AssertHalfPrecision(rgba[i * 4 + 1], got[i * 4 + 1]);
            AssertHalfPrecision(rgba[i * 4 + 2], got[i * 4 + 2]);
            AssertHalfPrecision(rgba[i * 4 + 3], got[i * 4 + 3]);
        }
    }

    [Fact]
    public void Pure_BadMagic_ReturnsNull()
    {
        var notExr = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0, 0, 0, 0, 0 };
        Assert.Null(PureExrDecoder.TryDecode(notExr));
    }

    [Fact]
    public async Task LoadAsync_UncompressedRgbHalf_TakesPureFastPath()
    {
        int w = 8, h = 4;
        var rgba = new float[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            rgba[i * 4] = i * 0.1f;
            rgba[i * 4 + 1] = i * 0.05f;
            rgba[i * 4 + 2] = i * 0.025f;
        }
        var exr = BuildScanlineRgbaExr(rgba, w, h, hasAlpha: false);

        var src = new PipeVipsSource(PipeReader.Create(new MemoryStream(exr)));
        var img = await VipsExrLoader.LoadAsync(src);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
        Assert.Equal(3, img.Bands);
        Assert.Equal(VipsBandFormat.Float, img.BandFormat);
    }

    /// <summary>
    /// Build a single-part scanline EXR with an explicit compression
    /// scheme. <paramref name="compress"/> is invoked per scanline to
    /// produce the encoded payload (after the spec's
    /// predictor + byte-interleave preprocessing).
    /// </summary>
    public static byte[] BuildCompressedExr(float[] rgba, int width, int height,
        bool hasAlpha, byte compressionCode, Func<byte[], byte[]> compress)
    {
        using var ms = new MemoryStream();
        WriteI32(ms, 0x01312F76);
        WriteI32(ms, 2);
        WriteAttribute(ms, "channels", "chlist", buf =>
        {
            if (hasAlpha) WriteChannel(buf, "A");
            WriteChannel(buf, "B");
            WriteChannel(buf, "G");
            WriteChannel(buf, "R");
            buf.WriteByte(0);
        });
        WriteAttribute(ms, "compression", "compression", buf => buf.WriteByte(compressionCode));
        WriteAttribute(ms, "dataWindow", "box2i", buf =>
        { WriteI32(buf, 0); WriteI32(buf, 0); WriteI32(buf, width - 1); WriteI32(buf, height - 1); });
        WriteAttribute(ms, "displayWindow", "box2i", buf =>
        { WriteI32(buf, 0); WriteI32(buf, 0); WriteI32(buf, width - 1); WriteI32(buf, height - 1); });
        WriteAttribute(ms, "lineOrder", "lineOrder", buf => buf.WriteByte(0));
        WriteAttribute(ms, "pixelAspectRatio", "float", buf => WriteF32(buf, 1.0f));
        WriteAttribute(ms, "screenWindowCenter", "v2f", buf => { WriteF32(buf, 0); WriteF32(buf, 0); });
        WriteAttribute(ms, "screenWindowWidth", "float", buf => WriteF32(buf, 1.0f));
        ms.WriteByte(0);

        long offsetTablePos = ms.Position;
        for (int y = 0; y < height; y++) WriteI64(ms, 0);

        int channelsInFile = hasAlpha ? 4 : 3;

        for (int y = 0; y < height; y++)
        {
            // Build raw scanline (per-channel-then-per-pixel, alphabetical channel order).
            var raw = new byte[channelsInFile * width * 2];
            int rp = 0;
            if (hasAlpha)
                for (int x = 0; x < width; x++) WriteHalfTo(raw, ref rp, rgba[(y * width + x) * 4 + 3]);
            for (int x = 0; x < width; x++) WriteHalfTo(raw, ref rp, rgba[(y * width + x) * 4 + 2]);
            for (int x = 0; x < width; x++) WriteHalfTo(raw, ref rp, rgba[(y * width + x) * 4 + 1]);
            for (int x = 0; x < width; x++) WriteHalfTo(raw, ref rp, rgba[(y * width + x) * 4 + 0]);

            // Spec order: interleave FIRST, then forward delta-predictor on
            // the interleaved buffer.
            var interleaved = new byte[raw.Length];
            int half = (raw.Length + 1) / 2;
            for (int i = 0; i < raw.Length; i++)
                interleaved[i / 2 + ((i & 1) == 0 ? 0 : half)] = raw[i];

            // Forward delta-predictor: capture original prev before overwriting.
            byte prev = interleaved[0];
            for (int i = 1; i < interleaved.Length; i++)
            {
                byte cur = interleaved[i];
                interleaved[i] = (byte)(cur - prev + 128);
                prev = cur;
            }

            byte[] compressed = compress(interleaved);
            // EXR convention: if compressed is bigger than raw, ship raw.
            byte[] payload = compressed.Length < raw.Length ? compressed : raw;

            long blockStart = ms.Position;
            long savedPos = ms.Position;
            ms.Position = offsetTablePos + y * 8;
            WriteI64(ms, blockStart);
            ms.Position = savedPos;

            WriteI32(ms, y);
            WriteI32(ms, payload.Length);
            ms.Write(payload, 0, payload.Length);
        }

        return ms.ToArray();
    }

    private static void WriteHalfTo(byte[] buf, ref int p, float v)
    {
        ushort h = BitConverter.HalfToUInt16Bits((Half)v);
        buf[p++] = (byte)h;
        buf[p++] = (byte)(h >> 8);
    }

    private static byte[] ZlibCompress(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(ms,
            System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(raw, 0, raw.Length);
        }
        return ms.ToArray();
    }

    private static byte[] RleCompress(byte[] raw)
    {
        // OpenEXR's RLE encoder: scans for runs of repeated bytes,
        // emits literal blocks for unique bytes. Output format matches
        // the decoder above.
        using var ms = new MemoryStream();
        int p = 0;
        while (p < raw.Length)
        {
            int runStart = p;
            byte v = raw[p];
            int runLen = 1;
            while (p + runLen < raw.Length && raw[p + runLen] == v && runLen < 128) runLen++;
            if (runLen >= 3)
            {
                ms.WriteByte((byte)(sbyte)(-(runLen - 1)));
                ms.WriteByte(v);
                p += runLen;
            }
            else
            {
                int litStart = p;
                int litLen = 0;
                while (p < raw.Length && litLen < 128)
                {
                    int look = 1;
                    while (p + look < raw.Length && raw[p + look] == raw[p] && look < 3) look++;
                    if (look >= 3) break;
                    p++;
                    litLen++;
                }
                ms.WriteByte((byte)(litLen - 1));
                ms.Write(raw, litStart, litLen);
            }
        }
        return ms.ToArray();
    }

    [Fact]
    public void Pure_ZipsRgbHalf_RoundTrips()
    {
        int w = 32, h = 8;
        var rgba = new float[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                rgba[o] = x * 0.05f;
                rgba[o + 1] = y * 0.1f;
                rgba[o + 2] = (x + y) * 0.025f;
            }
        var exr = BuildCompressedExr(rgba, w, h, hasAlpha: false, compressionCode: 2, ZlibCompress);
        var img = PureExrDecoder.TryDecode(exr);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(3, img.Bands);

        var got = ReadFloats(img.PixelsLazy!.Value);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 3;
                AssertHalfPrecision(rgba[(y * w + x) * 4], got[o]);
                AssertHalfPrecision(rgba[(y * w + x) * 4 + 1], got[o + 1]);
                AssertHalfPrecision(rgba[(y * w + x) * 4 + 2], got[o + 2]);
            }
    }

    [Fact]
    public void Pure_RleRgbHalf_RoundTrips()
    {
        int w = 32, h = 4;
        // Highly compressible (constant color) so RLE actually engages.
        var rgba = new float[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            rgba[i * 4] = 0.5f;
            rgba[i * 4 + 1] = 0.25f;
            rgba[i * 4 + 2] = 0.75f;
        }
        var exr = BuildCompressedExr(rgba, w, h, hasAlpha: false, compressionCode: 1, RleCompress);
        var img = PureExrDecoder.TryDecode(exr);
        Assert.NotNull(img);
        var got = ReadFloats(img!.PixelsLazy!.Value);
        for (int i = 0; i < w * h; i++)
        {
            AssertHalfPrecision(0.5f, got[i * 3]);
            AssertHalfPrecision(0.25f, got[i * 3 + 1]);
            AssertHalfPrecision(0.75f, got[i * 3 + 2]);
        }
    }

    [Fact]
    public async Task IsExr_DetectsMagic()
    {
        var exr = BuildScanlineRgbaExr(new float[1 * 1 * 4], 1, 1, hasAlpha: false);
        var src = new PipeVipsSource(PipeReader.Create(new MemoryStream(exr)));
        Assert.True(await VipsExrLoader.IsExrAsync(src));
    }

    private static void AssertHalfPrecision(float expected, float actual)
    {
        // Half has ~3 decimal digits of precision. Use a relative tolerance
        // for non-tiny values, absolute for tiny.
        if (Math.Abs(expected) < 0.001f)
            Assert.True(Math.Abs(actual - expected) < 0.001f, $"expected {expected} got {actual}");
        else
            Assert.True(Math.Abs(actual - expected) / Math.Abs(expected) < 0.001f, $"expected {expected} got {actual}");
    }
}
