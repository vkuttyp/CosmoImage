using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Core;
using CosmoImage.Loaders;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 141 — PIZ integration retry. Round 132 validated each
/// primitive in isolation; this round wires Compress + Decompress
/// together and proves the full pipeline round-trips. Fixes the
/// LUT-vs-wavelet ordering bug from the first attempt (libimf does
/// inverse-wavelet THEN reverse-LUT; we had it reversed).
/// </summary>
public class Round141Tests
{
    [Fact]
    public void PizCompress_HalfChannel_RoundTrips()
    {
        // 8x4 single-HALF-channel block. Build a raw byte buffer in the
        // per-row-per-channel-per-pixel layout the decoder/encoder use.
        int w = 8, rows = 4;
        var raw = new byte[w * rows * 2];
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < w; x++)
            {
                ushort h = (ushort)((y * 7 + x * 3) & 0x3FFF);
                int o = (y * w + x) * 2;
                raw[o]     = (byte)h;
                raw[o + 1] = (byte)(h >> 8);
            }

        var compressed = ExrPiz.Compress(raw, rows, new[] { 2 }, w);
        Assert.NotNull(compressed);
        Assert.True(compressed.Length > 0);

        var decompressed = new byte[raw.Length];
        bool ok = ExrPiz.Decompress(compressed, 0, compressed.Length,
            decompressed, rows, new[] { 2 }, w);
        Assert.True(ok);
        Assert.Equal(raw, decompressed);
    }

    [Fact]
    public void PizCompress_RgbThreeChannel_RoundTrips()
    {
        // 16x4 RGB HALF block: three channels, each 1 sub-band.
        int w = 16, rows = 4;
        var raw = new byte[w * rows * 3 * 2];
        var rng = new Random(12345);
        for (int i = 0; i < raw.Length / 2; i++)
        {
            ushort h = (ushort)(rng.Next() & 0x3FFF);
            raw[i * 2]     = (byte)h;
            raw[i * 2 + 1] = (byte)(h >> 8);
        }

        var compressed = ExrPiz.Compress(raw, rows, new[] { 2, 2, 2 }, w);
        var decompressed = new byte[raw.Length];
        bool ok = ExrPiz.Decompress(compressed, 0, compressed.Length,
            decompressed, rows, new[] { 2, 2, 2 }, w);
        Assert.True(ok);
        Assert.Equal(raw, decompressed);
    }

    [Fact]
    public void PizCompress_FloatChannel_RoundTrips()
    {
        // FLOAT contributes 2 sub-bands per pixel — exercise that case.
        int w = 8, rows = 4;
        var raw = new byte[w * rows * 4];
        var rng = new Random(54321);
        for (int i = 0; i < raw.Length; i++) raw[i] = (byte)rng.Next(256);

        var compressed = ExrPiz.Compress(raw, rows, new[] { 4 }, w);
        var decompressed = new byte[raw.Length];
        bool ok = ExrPiz.Decompress(compressed, 0, compressed.Length,
            decompressed, rows, new[] { 4 }, w);
        Assert.True(ok);
        Assert.Equal(raw, decompressed);
    }

    [Fact]
    public void PizCompress_AllSamplesIdentical_RoundTrips()
    {
        // Edge case: only one distinct value → 1-symbol Huffman tree;
        // FrequenciesToCodeLengths must synthesize length 1 for it.
        int w = 8, rows = 4;
        var raw = new byte[w * rows * 2];
        for (int i = 0; i < raw.Length; i += 2) { raw[i] = 0x42; raw[i + 1] = 0x12; }

        var compressed = ExrPiz.Compress(raw, rows, new[] { 2 }, w);
        var decompressed = new byte[raw.Length];
        bool ok = ExrPiz.Decompress(compressed, 0, compressed.Length,
            decompressed, rows, new[] { 2 }, w);
        Assert.True(ok);
        Assert.Equal(raw, decompressed);
    }

    [Fact]
    public void Pure_PizScanlineExr_FullPipeline()
    {
        // End-to-end: build a PIZ-compressed scanline EXR by hand and
        // decode through PureExrDecoder.TryDecode. Validates the
        // dispatcher (compression=4, scanlinesPerBlock=32) is wired
        // correctly and the demux integrates with our pixel layout.
        int w = 8, h = 6;
        var rgb = new ushort[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 3;
                rgb[o]     = (ushort)((x * 11) & 0x3FFF);
                rgb[o + 1] = (ushort)((y * 17) & 0x3FFF);
                rgb[o + 2] = (ushort)(((x ^ y) * 5) & 0x3FFF);
            }

        var exr = BuildPizScanlineExr(rgb, w, h);
        var img = PureExrDecoder.TryDecode(exr);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
        Assert.Equal(3, img.Bands);

        var got = ReadFloats(img.PixelsLazy!.Value);
        for (int i = 0; i < rgb.Length; i++)
        {
            float expected = (float)BitConverter.UInt16BitsToHalf(rgb[i]);
            Assert.True(Math.Abs(expected - got[i]) < 0.01f * Math.Max(1f, Math.Abs(expected)),
                $"i={i}: expected {expected} got {got[i]}");
        }
    }

    /// <summary>
    /// Hand-built scanline EXR with PIZ compression. Bundles all rows
    /// into a single 32-row PIZ block (PIZ's standard block size).
    /// </summary>
    private static byte[] BuildPizScanlineExr(ushort[] rgb, int w, int h)
    {
        using var ms = new MemoryStream();
        WriteI32(ms, 0x01312F76);
        WriteI32(ms, 2);

        WriteAttribute(ms, "channels", "chlist", buf =>
        {
            WriteChannel(buf, "B", pixelType: 1);
            WriteChannel(buf, "G", pixelType: 1);
            WriteChannel(buf, "R", pixelType: 1);
            buf.WriteByte(0);
        });
        WriteAttribute(ms, "compression", "compression", buf => buf.WriteByte(4));  // PIZ
        WriteAttribute(ms, "dataWindow", "box2i", buf =>
        { WriteI32(buf, 0); WriteI32(buf, 0); WriteI32(buf, w - 1); WriteI32(buf, h - 1); });
        WriteAttribute(ms, "displayWindow", "box2i", buf =>
        { WriteI32(buf, 0); WriteI32(buf, 0); WriteI32(buf, w - 1); WriteI32(buf, h - 1); });
        WriteAttribute(ms, "lineOrder", "lineOrder", buf => buf.WriteByte(0));
        WriteAttribute(ms, "pixelAspectRatio", "float", buf => WriteF32(buf, 1.0f));
        WriteAttribute(ms, "screenWindowCenter", "v2f", buf => { WriteF32(buf, 0); WriteF32(buf, 0); });
        WriteAttribute(ms, "screenWindowWidth", "float", buf => WriteF32(buf, 1.0f));
        ms.WriteByte(0);

        // PIZ uses 32-scanline blocks; if h <= 32 we have one block.
        int scanlinesPerBlock = 32;
        int blockCount = (h + scanlinesPerBlock - 1) / scanlinesPerBlock;
        long offsetTablePos = ms.Position;
        for (int i = 0; i < blockCount; i++) WriteI64(ms, 0);

        for (int b = 0; b < blockCount; b++)
        {
            int yStart = b * scanlinesPerBlock;
            int rowsInBlock = Math.Min(scanlinesPerBlock, h - yStart);

            // Build the raw per-row-per-channel-per-pixel byte buffer.
            var raw = new byte[rowsInBlock * w * 3 * 2];
            int p = 0;
            for (int y = 0; y < rowsInBlock; y++)
            {
                int yIdx = yStart + y;
                // Alphabetical channel order: B, G, R
                for (int x = 0; x < w; x++)
                { ushort v = rgb[(yIdx * w + x) * 3 + 2]; raw[p++] = (byte)v; raw[p++] = (byte)(v >> 8); }
                for (int x = 0; x < w; x++)
                { ushort v = rgb[(yIdx * w + x) * 3 + 1]; raw[p++] = (byte)v; raw[p++] = (byte)(v >> 8); }
                for (int x = 0; x < w; x++)
                { ushort v = rgb[(yIdx * w + x) * 3 + 0]; raw[p++] = (byte)v; raw[p++] = (byte)(v >> 8); }
            }

            var compressed = ExrPiz.Compress(raw, rowsInBlock, new[] { 2, 2, 2 }, w);

            long blockStart = ms.Position;
            long savedPos = ms.Position;
            ms.Position = offsetTablePos + b * 8;
            WriteI64(ms, blockStart);
            ms.Position = savedPos;

            WriteI32(ms, yStart);
            WriteI32(ms, compressed.Length);
            ms.Write(compressed, 0, compressed.Length);
        }

        return ms.ToArray();
    }

    private static void WriteChannel(Stream s, string name, int pixelType)
    {
        foreach (char c in name) s.WriteByte((byte)c);
        s.WriteByte(0);
        WriteI32(s, pixelType);
        s.WriteByte(0); s.WriteByte(0); s.WriteByte(0); s.WriteByte(0);
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

    private static float[] ReadFloats(byte[] raw)
    {
        var f = new float[raw.Length / 4];
        Buffer.BlockCopy(raw, 0, f, 0, raw.Length);
        return f;
    }
}
