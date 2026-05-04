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
/// Round 142 — EXR B44 / B44A compression. 4×4-block DPCM with
/// sign-magnitude monotonic mapping. B44 always emits 14-byte
/// blocks; B44A adds a 3-byte short form for uniform blocks.
/// HALF channels only get the block treatment; FLOAT/UINT pass
/// through raw within each scanline block.
/// </summary>
public class Round142Tests
{
    [Fact]
    public void B44_UniformBlock_RoundTripsThroughDecoder()
    {
        // All 16 samples identical → encoder emits the 3-byte
        // uniform form (only when b44a=true) — but the decoder must
        // accept either form. Test the short path directly.
        ushort half = 0x3C00;  // HALF representation of 1.0
        var compressed = ExrB44.EncodeUniformBlock(half, pLinear: true);
        Assert.Equal(3, compressed.Length);
        Assert.Equal(0xFC, compressed[2]);

        // Pad to a 14-byte ExrB44.Compress output for a 4×4×1 block.
        // (Decompress expects compressed input as a stream of blocks.)
        var dst = new byte[4 * 4 * 2];
        var ch = new[] { new ExrB44ChannelInfo(2, true) };
        bool ok = ExrB44.Decompress(compressed, 0, compressed.Length,
            dst, rows: 4, ch, width: 4);
        Assert.True(ok);
        for (int i = 0; i < 16; i++)
        {
            ushort got = (ushort)(dst[i * 2] | (dst[i * 2 + 1] << 8));
            Assert.Equal(half, got);
        }
    }

    [Fact]
    public void B44_SmallDifferences_RoundTripsExactly()
    {
        // Differences fit in [-32, 31] at shift=0 → no rounding loss.
        // Encoder picks shift=0 and the round-trip is bit-exact.
        var s = new ushort[16];
        for (int i = 0; i < 16; i++) s[i] = (ushort)(0x3C00 + i);  // 1.0 + i ULP

        var compressed = ExrB44.EncodeFullBlock(s, pLinear: true);
        Assert.Equal(14, compressed.Length);

        var dst = new byte[4 * 4 * 2];
        var ch = new[] { new ExrB44ChannelInfo(2, true) };
        bool ok = ExrB44.Decompress(compressed, 0, compressed.Length,
            dst, rows: 4, ch, width: 4);
        Assert.True(ok);
        for (int i = 0; i < 16; i++)
        {
            ushort got = (ushort)(dst[i * 2] | (dst[i * 2 + 1] << 8));
            Assert.Equal(s[i], got);
        }
    }

    [Fact]
    public void B44_LargeDifferences_RoundTripsApproximately()
    {
        // Big differences force shift > 0 → quantization. Test that
        // the round-trip stays within the encoder's quantization
        // budget — each value should be within (1 << shift) of the
        // original after t-space wrap.
        var s = new ushort[16];
        var rng = new Random(42);
        for (int i = 0; i < 16; i++) s[i] = (ushort)rng.Next(0x3C00, 0x4000);

        var compressed = ExrB44.EncodeFullBlock(s, pLinear: true);
        var dst = new byte[4 * 4 * 2];
        var ch = new[] { new ExrB44ChannelInfo(2, true) };
        bool ok = ExrB44.Decompress(compressed, 0, compressed.Length,
            dst, rows: 4, ch, width: 4);
        Assert.True(ok);

        int shift = compressed[2] >> 2;
        int tolerance = (1 << shift) * 4;  // accumulated error along DPCM chain
        for (int i = 0; i < 16; i++)
        {
            ushort got = (ushort)(dst[i * 2] | (dst[i * 2 + 1] << 8));
            int delta = Math.Abs(s[i] - got);
            Assert.True(delta <= tolerance,
                $"sample {i}: expected {s[i]:X4} got {got:X4} delta {delta} tolerance {tolerance}");
        }
    }

    [Fact]
    public void Pure_B44ScanlineExr_FullPipeline()
    {
        // End-to-end: build a B44-compressed scanline EXR by hand and
        // decode through PureExrDecoder.TryDecode. Three HALF channels,
        // pLinear=1 so the perceptual LUT is skipped.
        int w = 8, h = 4;
        var rgb = new ushort[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 3;
                rgb[o]     = (ushort)(0x3C00 + x);
                rgb[o + 1] = (ushort)(0x3C00 + y * 2);
                rgb[o + 2] = (ushort)(0x3C00 + (x + y));
            }

        var exr = BuildB44ScanlineExr(rgb, w, h, b44a: false);
        var img = PureExrDecoder.TryDecode(exr);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
        Assert.Equal(3, img.Bands);

        var got = ReadFloats(img.PixelsLazy!.Value);
        for (int i = 0; i < rgb.Length; i++)
        {
            float expected = (float)BitConverter.UInt16BitsToHalf(rgb[i]);
            Assert.True(Math.Abs(expected - got[i]) < 0.05f * Math.Max(1f, Math.Abs(expected)),
                $"i={i}: expected {expected} got {got[i]}");
        }
    }

    [Fact]
    public void Pure_B44aScanlineExr_UniformBlocksUseShortForm()
    {
        // Solid-color image → every block is uniform → B44A's 3-byte
        // form. Compressed size should drop sharply versus B44.
        int w = 8, h = 4;
        var rgb = new ushort[w * h * 3];
        for (int i = 0; i < rgb.Length; i++) rgb[i] = 0x3C00;

        var exrB44 = BuildB44ScanlineExr(rgb, w, h, b44a: false);
        var exrB44a = BuildB44ScanlineExr(rgb, w, h, b44a: true);

        // B44A's compressed output is materially smaller than B44 here.
        Assert.True(exrB44a.Length < exrB44.Length,
            $"B44A should be smaller for uniform input: B44={exrB44.Length} B44A={exrB44a.Length}");

        var img = PureExrDecoder.TryDecode(exrB44a);
        Assert.NotNull(img);
        var got = ReadFloats(img!.PixelsLazy!.Value);
        for (int i = 0; i < rgb.Length; i++)
            Assert.Equal(1.0f, got[i]);
    }

    private static byte[] BuildB44ScanlineExr(ushort[] rgb, int w, int h, bool b44a)
    {
        int compressionCode = b44a ? 7 : 6;
        using var ms = new MemoryStream();
        WriteI32(ms, 0x01312F76);
        WriteI32(ms, 2);

        WriteAttribute(ms, "channels", "chlist", buf =>
        {
            WriteChannel(buf, "B", pLinear: 1);
            WriteChannel(buf, "G", pLinear: 1);
            WriteChannel(buf, "R", pLinear: 1);
            buf.WriteByte(0);
        });
        WriteAttribute(ms, "compression", "compression", buf => buf.WriteByte((byte)compressionCode));
        WriteAttribute(ms, "dataWindow", "box2i", buf =>
        { WriteI32(buf, 0); WriteI32(buf, 0); WriteI32(buf, w - 1); WriteI32(buf, h - 1); });
        WriteAttribute(ms, "displayWindow", "box2i", buf =>
        { WriteI32(buf, 0); WriteI32(buf, 0); WriteI32(buf, w - 1); WriteI32(buf, h - 1); });
        WriteAttribute(ms, "lineOrder", "lineOrder", buf => buf.WriteByte(0));
        WriteAttribute(ms, "pixelAspectRatio", "float", buf => WriteF32(buf, 1.0f));
        WriteAttribute(ms, "screenWindowCenter", "v2f", buf => { WriteF32(buf, 0); WriteF32(buf, 0); });
        WriteAttribute(ms, "screenWindowWidth", "float", buf => WriteF32(buf, 1.0f));
        ms.WriteByte(0);

        int scanlinesPerBlock = 32;
        int blockCount = (h + scanlinesPerBlock - 1) / scanlinesPerBlock;
        long offsetTablePos = ms.Position;
        for (int i = 0; i < blockCount; i++) WriteI64(ms, 0);

        for (int b = 0; b < blockCount; b++)
        {
            int yStart = b * scanlinesPerBlock;
            int rowsInBlock = Math.Min(scanlinesPerBlock, h - yStart);

            // Pack per-row-per-channel-per-pixel layout (channels alphabetical).
            var raw = new byte[rowsInBlock * w * 3 * 2];
            int p = 0;
            for (int y = 0; y < rowsInBlock; y++)
            {
                int yIdx = yStart + y;
                for (int x = 0; x < w; x++)
                { ushort v = rgb[(yIdx * w + x) * 3 + 2]; raw[p++] = (byte)v; raw[p++] = (byte)(v >> 8); }
                for (int x = 0; x < w; x++)
                { ushort v = rgb[(yIdx * w + x) * 3 + 1]; raw[p++] = (byte)v; raw[p++] = (byte)(v >> 8); }
                for (int x = 0; x < w; x++)
                { ushort v = rgb[(yIdx * w + x) * 3 + 0]; raw[p++] = (byte)v; raw[p++] = (byte)(v >> 8); }
            }

            var ch = new[]
            {
                new ExrB44ChannelInfo(2, true),
                new ExrB44ChannelInfo(2, true),
                new ExrB44ChannelInfo(2, true),
            };
            var compressed = ExrB44.Compress(raw, rowsInBlock, ch, w, b44a);

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

    private static void WriteChannel(Stream s, string name, int pLinear)
    {
        foreach (char c in name) s.WriteByte((byte)c);
        s.WriteByte(0);
        WriteI32(s, 1);  // pixelType = HALF
        s.WriteByte((byte)pLinear);
        s.WriteByte(0); s.WriteByte(0); s.WriteByte(0);
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
