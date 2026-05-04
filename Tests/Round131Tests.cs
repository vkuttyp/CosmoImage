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
/// Round 131 — EXR PXR24 compressor (compression=5). Different
/// shape from ZIP/ZIPS: per-pixel 16-bit delta predictor that
/// resets at every channel × row boundary, with bytes split into
/// high/low streams before zlib. 16-scanline blocks.
/// </summary>
public class Round131Tests
{
    private static byte[] BuildPxr24Exr(float[] rgba, int width, int height, bool hasAlpha)
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
        WriteAttribute(ms, "compression", "compression", buf => buf.WriteByte(5));  // PXR24
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
        int rawRowBytes = channelsInFile * width * 2;

        for (int b = 0; b < blockCount; b++)
        {
            int yStart = b * rowsPerBlock;
            int rows = Math.Min(rowsPerBlock, height - yStart);

            // Build the encoded stream: per row, per channel,
            // [high_bytes(W), low_bytes(W)] of per-pixel deltas.
            var encoded = new byte[rows * rawRowBytes];
            int ep = 0;
            for (int row = 0; row < rows; row++)
            {
                int y = yStart + row;
                if (hasAlpha) EncodeChannelRow(rgba, y, width, 3, encoded, ref ep);
                EncodeChannelRow(rgba, y, width, 2, encoded, ref ep);
                EncodeChannelRow(rgba, y, width, 1, encoded, ref ep);
                EncodeChannelRow(rgba, y, width, 0, encoded, ref ep);
            }

            byte[] compressed = ZlibCompress(encoded);
            byte[] payload = compressed.Length < encoded.Length ? compressed : encoded;

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

    /// <summary>
    /// Encode one channel of one row: write width high bytes followed by
    /// width low bytes of (current_pixel - prev_pixel) deltas.
    /// </summary>
    private static void EncodeChannelRow(float[] rgba, int y, int width, int chOffset,
        byte[] encoded, ref int ep)
    {
        // First pass: compute deltas + write high bytes.
        ushort prev = 0;
        for (int x = 0; x < width; x++)
        {
            ushort cur = BitConverter.HalfToUInt16Bits((Half)rgba[(y * width + x) * 4 + chOffset]);
            ushort delta = (ushort)(cur - prev);
            encoded[ep + x] = (byte)(delta >> 8);
            prev = cur;
        }
        // Second pass: write low bytes (need a fresh prev because we
        // didn't save the deltas — recompute).
        prev = 0;
        for (int x = 0; x < width; x++)
        {
            ushort cur = BitConverter.HalfToUInt16Bits((Half)rgba[(y * width + x) * 4 + chOffset]);
            ushort delta = (ushort)(cur - prev);
            encoded[ep + width + x] = (byte)(delta & 0xFF);
            prev = cur;
        }
        ep += 2 * width;
    }

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
    public void Pure_Pxr24RgbHalf_RoundTrips()
    {
        int w = 16, h = 8;
        var rgba = new float[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                rgba[o] = x * 0.05f;
                rgba[o + 1] = y * 0.1f;
                rgba[o + 2] = (x + y) * 0.025f;
            }
        var exr = BuildPxr24Exr(rgba, w, h, hasAlpha: false);
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
    public void Pure_Pxr24RgbaHalf_MultipleBlocks_RoundTrips()
    {
        int w = 12, h = 36;
        var rgba = new float[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                rgba[o] = x * 0.07f;
                rgba[o + 1] = y * 0.05f;
                rgba[o + 2] = (x ^ y) * 0.03f;
                rgba[o + 3] = 0.5f + (x * 0.02f);
            }
        var exr = BuildPxr24Exr(rgba, w, h, hasAlpha: true);
        var img = PureExrDecoder.TryDecode(exr);
        Assert.NotNull(img);
        Assert.Equal(4, img!.Bands);
        var got = ReadFloats(img.PixelsLazy!.Value);
        for (int i = 0; i < w * h * 4; i++) AssertHalfPrecision(rgba[i], got[i]);
    }

    [Fact]
    public async Task LoadAsync_Pxr24Tiff_TakesPureFastPath()
    {
        int w = 16, h = 8;
        var rgba = new float[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            rgba[i * 4] = 0.5f;
            rgba[i * 4 + 1] = 0.25f;
            rgba[i * 4 + 2] = 0.125f;
        }
        var exr = BuildPxr24Exr(rgba, w, h, hasAlpha: false);
        var src = new PipeVipsSource(PipeReader.Create(new MemoryStream(exr)));
        var img = await VipsExrLoader.LoadAsync(src);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(VipsBandFormat.Float, img.BandFormat);
    }
}
