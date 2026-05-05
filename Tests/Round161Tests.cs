using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using CosmoImage.Loaders;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 161 — DWA LOSSY_DCT integration. Ties the four primitives
/// (IDCT, AC RLE expander, DC stream decoder, block placer) together
/// inside ExrDwa.Decompress. Validated end-to-end via a hand-rolled
/// encoder that uses the same primitives in reverse: forward DCT +
/// zigzag + zlib-wrap. No dependency on libimf for the test.
/// </summary>
public class Round161Tests
{
    [Fact]
    public void DctRoundTrip_8x8_SingleChannelHalf_ReconstructsWithinPrecision()
    {
        // Build a known 8×8 HALF input. Choose values that sit
        // comfortably in the HALF range so the round-trip's loss is
        // dominated by float-vs-half rounding and the scaled IDCT,
        // not exponent-related quantization.
        ushort[] inputHalves = new ushort[64];
        for (int i = 0; i < 64; i++)
        {
            float v = 0.1f * (i + 1);
            inputHalves[i] = BitConverter.HalfToUInt16Bits((Half)v);
        }

        var exr = BuildSingleBlockDwaaExr(inputHalves);
        var img = PureExrDecoder.TryDecode(exr);
        Assert.NotNull(img);
        Assert.Equal(8, img!.Width);
        Assert.Equal(8, img.Height);
        Assert.Equal(1, img.Bands);

        // Output is Float (HALF promoted on load). Compare each sample
        // against the input's float value with a generous tolerance —
        // forward DCT followed by inverse DCT is mathematically lossless
        // but float arithmetic adds ~1e-3 noise.
        var pix = img.PixelsLazy!.Value;
        var got = new float[pix.Length / 4];
        Buffer.BlockCopy(pix, 0, got, 0, pix.Length);
        for (int i = 0; i < 64; i++)
        {
            float expected = (float)BitConverter.UInt16BitsToHalf(inputHalves[i]);
            Assert.True(Math.Abs(got[i] - expected) < 0.01f,
                $"sample {i}: expected {expected}, got {got[i]}");
        }
    }

    /// <summary>
    /// Build a complete DWAA EXR file containing one 8×8 single-Y
    /// HALF block. Encoder pipeline is the inverse of the decoder:
    /// forward DCT → round to HALF → split DC + zigzagged AC →
    /// zlib-wrap each stream.
    /// </summary>
    private static byte[] BuildSingleBlockDwaaExr(ushort[] inputHalves)
    {
        // ---- Encode the block ----
        var floatBlock = new float[64];
        for (int i = 0; i < 64; i++)
            floatBlock[i] = (float)BitConverter.UInt16BitsToHalf(inputHalves[i]);
        ExrDct.Forward8x8InPlace(floatBlock);

        ushort[] freqHalves = new ushort[64];
        for (int i = 0; i < 64; i++)
            freqHalves[i] = BitConverter.HalfToUInt16Bits((Half)floatBlock[i]);

        // DC stream: one ushort.
        ushort dcValue = freqHalves[0];
        var dcRaw = new byte[] { (byte)dcValue, (byte)(dcValue >> 8) };
        var dcCompressed = ZlibWrap(dcRaw);

        // AC stream: 63 ushorts in zigzag order. RLE-encode runs of
        // zeros (each 0xFF + count token) so the un-RLE on the decode
        // side has something to expand.
        var acTokens = ZigzagAndRle(freqHalves);
        var acRaw = new byte[acTokens.Length * 2];
        for (int i = 0; i < acTokens.Length; i++)
        {
            acRaw[i * 2]     = (byte)acTokens[i];
            acRaw[i * 2 + 1] = (byte)(acTokens[i] >> 8);
        }
        var acCompressed = ZlibWrap(acRaw);

        // ---- Build DWA chunk: 88-byte counter header + AC + DC ----
        var dwaPayload = new MemoryStream();
        WriteU64(dwaPayload, 0);   // VERSION = 0 (no rule table)
        WriteU64(dwaPayload, 0);   // UNK_UNCOMP
        WriteU64(dwaPayload, 0);   // UNK_COMP
        WriteU64(dwaPayload, (ulong)acCompressed.Length);   // AC_COMP
        WriteU64(dwaPayload, (ulong)dcCompressed.Length);   // DC_COMP
        WriteU64(dwaPayload, 0);   // RLE_COMP
        WriteU64(dwaPayload, 0);   // RLE_UNCOMP
        WriteU64(dwaPayload, 0);   // RLE_RAW
        WriteU64(dwaPayload, (ulong)acTokens.Length);       // AC_COUNT
        WriteU64(dwaPayload, 1);   // DC_COUNT (one block)
        WriteU64(dwaPayload, 1);   // AC_COMPRESSION = 1 (deflate)
        // Streams in order: unknown, ac, dc, rle.
        dwaPayload.Write(acCompressed);
        dwaPayload.Write(dcCompressed);
        var dwaBytes = dwaPayload.ToArray();

        // ---- Build minimal scanline EXR around the DWA chunk ----
        using var ms = new MemoryStream();
        WriteI32(ms, 0x01312F76);
        WriteI32(ms, 2);  // version
        WriteAttribute(ms, "channels", "chlist", buf =>
        {
            WriteChannel(buf, "Y");
            buf.WriteByte(0);
        });
        WriteAttribute(ms, "compression", "compression", buf => buf.WriteByte(8));  // DWAA
        WriteAttribute(ms, "dataWindow", "box2i", buf =>
        { WriteI32(buf, 0); WriteI32(buf, 0); WriteI32(buf, 7); WriteI32(buf, 7); });
        WriteAttribute(ms, "displayWindow", "box2i", buf =>
        { WriteI32(buf, 0); WriteI32(buf, 0); WriteI32(buf, 7); WriteI32(buf, 7); });
        WriteAttribute(ms, "lineOrder", "lineOrder", buf => buf.WriteByte(0));
        WriteAttribute(ms, "pixelAspectRatio", "float", buf => WriteI32(buf, BitConverter.SingleToInt32Bits(1.0f)));
        WriteAttribute(ms, "screenWindowCenter", "v2f", buf => { WriteI32(buf, 0); WriteI32(buf, 0); });
        WriteAttribute(ms, "screenWindowWidth", "float", buf => WriteI32(buf, BitConverter.SingleToInt32Bits(1.0f)));
        ms.WriteByte(0);

        // Single chunk's offset table entry — patched after we know
        // where the chunk lands.
        long offsetTablePos = ms.Position;
        WriteI64(ms, 0);

        long chunkStart = ms.Position;
        long savedPos = ms.Position;
        ms.Position = offsetTablePos;
        WriteI64(ms, chunkStart);
        ms.Position = savedPos;

        WriteI32(ms, 0);                   // yCoord
        WriteI32(ms, dwaBytes.Length);     // dataSize
        ms.Write(dwaBytes);

        return ms.ToArray();
    }

    /// <summary>
    /// Walk a row-major frequency block in zigzag order and RLE-encode
    /// the AC coefficients (positions 1..63). Token convention matches
    /// the decoder: a 0xFFnn token represents nn zeros; any other
    /// value is a literal.
    /// </summary>
    private static ushort[] ZigzagAndRle(ushort[] freqHalves)
    {
        var tokens = new System.Collections.Generic.List<ushort>();
        int zeroRun = 0;
        for (int k = 1; k < 64; k++)
        {
            ushort v = freqHalves[ExrDct.ZigzagToRowMajor[k]];
            if (v == 0)
            {
                zeroRun++;
                if (zeroRun == 0xFF)
                {
                    tokens.Add(0xFFFF);
                    zeroRun = 0;
                }
                continue;
            }
            if (zeroRun > 0)
            {
                tokens.Add((ushort)(0xFF00 | zeroRun));
                zeroRun = 0;
            }
            // Literal coefficient. The HALF bit pattern itself can have
            // high byte = 0xFF (NaN/Inf encodings); we escape those by
            // treating ANY 0xFFxx as a marker, so the encoder must
            // ensure no real coefficient lands in that range. For our
            // test input that's true since we use small float values.
            tokens.Add(v);
        }
        if (zeroRun > 0)
            tokens.Add((ushort)(0xFF00 | zeroRun));
        return tokens.ToArray();
    }

    private static byte[] ZlibWrap(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            z.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    private static void WriteChannel(Stream s, string name)
    {
        foreach (char c in name) s.WriteByte((byte)c);
        s.WriteByte(0);
        WriteI32(s, 1);  // pixelType = HALF
        s.WriteByte(1);  // pLinear = 1 (skip toLinear)
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
    private static void WriteU64(Stream s, ulong v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, v); s.Write(b); }
}
