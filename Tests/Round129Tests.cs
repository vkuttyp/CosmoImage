using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Core;
using CosmoImage.Loaders;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 129 — EXR ZIP compressor (16-scanline blocks). Same
/// zlib + delta-predictor + byte-interleave as ZIPS, but each
/// block holds up to 16 scanlines. Validates the multi-scanline
/// block code path.
/// </summary>
public class Round129Tests
{
    /// <summary>
    /// Build a single-part ZIP-compressed EXR. Scanlines pack into
    /// 16-row blocks; each block's bytes are interleaved + predicted +
    /// zlib-compressed as a single unit.
    /// </summary>
    private static byte[] BuildZipExr(float[] rgba, int width, int height, bool hasAlpha)
    {
        const int rowsPerBlock = 16;
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
        WriteAttribute(ms, "compression", "compression", buf => buf.WriteByte(3));  // ZIP
        WriteAttribute(ms, "dataWindow", "box2i", buf =>
        { WriteI32(buf, 0); WriteI32(buf, 0); WriteI32(buf, width - 1); WriteI32(buf, height - 1); });
        WriteAttribute(ms, "displayWindow", "box2i", buf =>
        { WriteI32(buf, 0); WriteI32(buf, 0); WriteI32(buf, width - 1); WriteI32(buf, height - 1); });
        WriteAttribute(ms, "lineOrder", "lineOrder", buf => buf.WriteByte(0));
        WriteAttribute(ms, "pixelAspectRatio", "float", buf => WriteF32(buf, 1.0f));
        WriteAttribute(ms, "screenWindowCenter", "v2f", buf => { WriteF32(buf, 0); WriteF32(buf, 0); });
        WriteAttribute(ms, "screenWindowWidth", "float", buf => WriteF32(buf, 1.0f));
        ms.WriteByte(0);

        int blockCount = (height + rowsPerBlock - 1) / rowsPerBlock;
        long offsetTablePos = ms.Position;
        for (int i = 0; i < blockCount; i++) WriteI64(ms, 0);

        int channelsInFile = hasAlpha ? 4 : 3;
        int rowBytes = channelsInFile * width * 2;

        for (int b = 0; b < blockCount; b++)
        {
            int yStart = b * rowsPerBlock;
            int rows = Math.Min(rowsPerBlock, height - yStart);
            // Concatenated raw scanlines for this block.
            var raw = new byte[rows * rowBytes];
            int rp = 0;
            for (int row = 0; row < rows; row++)
            {
                int y = yStart + row;
                if (hasAlpha)
                    for (int x = 0; x < width; x++) WriteHalfTo(raw, ref rp, rgba[(y * width + x) * 4 + 3]);
                for (int x = 0; x < width; x++) WriteHalfTo(raw, ref rp, rgba[(y * width + x) * 4 + 2]);
                for (int x = 0; x < width; x++) WriteHalfTo(raw, ref rp, rgba[(y * width + x) * 4 + 1]);
                for (int x = 0; x < width; x++) WriteHalfTo(raw, ref rp, rgba[(y * width + x) * 4 + 0]);
            }

            // Spec ordering: interleave first, then forward delta predictor,
            // then zlib. Same code as Round 128's ZIPS test.
            var interleaved = new byte[raw.Length];
            int half = (raw.Length + 1) / 2;
            for (int i = 0; i < raw.Length; i++)
                interleaved[i / 2 + ((i & 1) == 0 ? 0 : half)] = raw[i];
            byte prev = interleaved[0];
            for (int i = 1; i < interleaved.Length; i++)
            {
                byte cur = interleaved[i];
                interleaved[i] = (byte)(cur - prev + 128);
                prev = cur;
            }

            byte[] compressed = ZlibCompress(interleaved);
            byte[] payload = compressed.Length < raw.Length ? compressed : raw;

            long blockStart = ms.Position;
            long savedPos = ms.Position;
            ms.Position = offsetTablePos + b * 8;
            WriteI64(ms, blockStart);
            ms.Position = savedPos;

            WriteI32(ms, yStart);
            WriteI32(ms, payload.Length);
            ms.Write(payload, 0, payload.Length);
        }

        return ms.ToArray();
    }

    // ---- Shared helpers (same as Round 127) ----

    private static void WriteChannel(Stream s, string name)
    {
        foreach (char c in name) s.WriteByte((byte)c);
        s.WriteByte(0);
        WriteI32(s, 1); s.WriteByte(0); s.WriteByte(0); s.WriteByte(0); s.WriteByte(0);
        WriteI32(s, 1); WriteI32(s, 1);
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
    private static void WriteHalfTo(byte[] buf, ref int p, float v)
    {
        ushort h = BitConverter.HalfToUInt16Bits((Half)v);
        buf[p++] = (byte)h;
        buf[p++] = (byte)(h >> 8);
    }

    private static byte[] ZlibCompress(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(raw, 0, raw.Length);
        return ms.ToArray();
    }

    private static float[] ReadFloats(byte[] raw)
    {
        var f = new float[raw.Length / 4];
        Buffer.BlockCopy(raw, 0, f, 0, raw.Length);
        return f;
    }

    private static void AssertHalfPrecision(float expected, float actual)
    {
        if (Math.Abs(expected) < 0.001f)
            Assert.True(Math.Abs(actual - expected) < 0.002f, $"expected {expected} got {actual}");
        else
            Assert.True(Math.Abs(actual - expected) / Math.Abs(expected) < 0.001f, $"expected {expected} got {actual}");
    }

    [Fact]
    public void Pure_ZipRgbHalf_SingleBlock_RoundTrips()
    {
        // 8 rows fits in a single 16-scanline block.
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
        var exr = BuildZipExr(rgba, w, h, hasAlpha: false);
        var img = PureExrDecoder.TryDecode(exr);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);

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
    public void Pure_ZipRgbaHalf_MultipleBlocks_RoundTrips()
    {
        // 40 rows → 3 blocks: 16, 16, 8.
        int w = 16, h = 40;
        var rgba = new float[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                rgba[o] = x * 0.07f;
                rgba[o + 1] = y * 0.03f;
                rgba[o + 2] = (x * 11 + y * 13) * 0.01f;
                rgba[o + 3] = (x + y) * 0.02f + 0.1f;
            }
        var exr = BuildZipExr(rgba, w, h, hasAlpha: true);
        var img = PureExrDecoder.TryDecode(exr);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
        Assert.Equal(4, img.Bands);

        var got = ReadFloats(img.PixelsLazy!.Value);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int srcOff = (y * w + x) * 4;
                AssertHalfPrecision(rgba[srcOff],     got[srcOff]);
                AssertHalfPrecision(rgba[srcOff + 1], got[srcOff + 1]);
                AssertHalfPrecision(rgba[srcOff + 2], got[srcOff + 2]);
                AssertHalfPrecision(rgba[srcOff + 3], got[srcOff + 3]);
            }
    }

    [Fact]
    public async Task LoadAsync_ZipRgbHalf_TakesPureFastPath()
    {
        int w = 16, h = 32;
        var rgba = new float[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            rgba[i * 4] = i * 0.01f;
            rgba[i * 4 + 1] = i * 0.005f;
            rgba[i * 4 + 2] = i * 0.0025f;
        }
        var exr = BuildZipExr(rgba, w, h, hasAlpha: false);
        var src = new PipeVipsSource(PipeReader.Create(new MemoryStream(exr)));
        var img = await VipsExrLoader.LoadAsync(src);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
        Assert.Equal(VipsBandFormat.Float, img.BandFormat);
    }
}
