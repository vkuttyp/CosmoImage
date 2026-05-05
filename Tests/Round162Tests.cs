using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using CosmoImage.Loaders;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 162 — DWA AC Huffman path (acCompression=0). Round 161
/// validated the deflate path; libimf's encoder defaults to static
/// Huffman, so this round wires that in. Same canonical Huffman as
/// PIZ; we reuse ExrPiz.HuffmanEncode + PackEncTable in the test
/// encoder and ExrPiz.HuffmanDecode + UnpackEncTable in the
/// production decoder.
/// </summary>
public class Round162Tests
{
    [Fact]
    public void DctRoundTrip_8x8_HuffmanAc_ReconstructsWithinPrecision()
    {
        // Same input shape as Round 161, but the AC stream is encoded
        // with the static-Huffman path that libimf actually uses.
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

    private static byte[] BuildSingleBlockDwaaExr(ushort[] inputHalves)
    {
        // Forward DCT.
        var floatBlock = new float[64];
        for (int i = 0; i < 64; i++)
            floatBlock[i] = (float)BitConverter.UInt16BitsToHalf(inputHalves[i]);
        ExrDct.Forward8x8InPlace(floatBlock);

        ushort[] freqHalves = new ushort[64];
        for (int i = 0; i < 64; i++)
            freqHalves[i] = BitConverter.HalfToUInt16Bits((Half)floatBlock[i]);

        // DC stream.
        ushort dcValue = freqHalves[0];
        var dcRaw = new byte[] { (byte)dcValue, (byte)(dcValue >> 8) };
        var dcCompressed = ZlibWrap(dcRaw);

        // AC stream — Huffman encoded this round.
        var acTokens = ZigzagAndRle(freqHalves);
        var acHuffman = HuffmanEncodeAc(acTokens);

        // DWA chunk header + streams.
        var dwaPayload = new MemoryStream();
        WriteU64(dwaPayload, 0);
        WriteU64(dwaPayload, 0);
        WriteU64(dwaPayload, 0);
        WriteU64(dwaPayload, (ulong)acHuffman.Length);
        WriteU64(dwaPayload, (ulong)dcCompressed.Length);
        WriteU64(dwaPayload, 0);
        WriteU64(dwaPayload, 0);
        WriteU64(dwaPayload, 0);
        WriteU64(dwaPayload, (ulong)acTokens.Length);
        WriteU64(dwaPayload, 1);
        WriteU64(dwaPayload, 0);  // acCompression = 0 (Huffman)
        dwaPayload.Write(acHuffman);
        dwaPayload.Write(dcCompressed);
        var dwaBytes = dwaPayload.ToArray();

        // Minimal scanline EXR wrapper.
        using var ms = new MemoryStream();
        WriteI32(ms, 0x01312F76);
        WriteI32(ms, 2);
        WriteAttribute(ms, "channels", "chlist", buf =>
        {
            WriteChannel(buf, "Y");
            buf.WriteByte(0);
        });
        WriteAttribute(ms, "compression", "compression", buf => buf.WriteByte(8));
        WriteAttribute(ms, "dataWindow", "box2i", buf =>
        { WriteI32(buf, 0); WriteI32(buf, 0); WriteI32(buf, 7); WriteI32(buf, 7); });
        WriteAttribute(ms, "displayWindow", "box2i", buf =>
        { WriteI32(buf, 0); WriteI32(buf, 0); WriteI32(buf, 7); WriteI32(buf, 7); });
        WriteAttribute(ms, "lineOrder", "lineOrder", buf => buf.WriteByte(0));
        WriteAttribute(ms, "pixelAspectRatio", "float", buf => WriteI32(buf, BitConverter.SingleToInt32Bits(1.0f)));
        WriteAttribute(ms, "screenWindowCenter", "v2f", buf => { WriteI32(buf, 0); WriteI32(buf, 0); });
        WriteAttribute(ms, "screenWindowWidth", "float", buf => WriteI32(buf, BitConverter.SingleToInt32Bits(1.0f)));
        ms.WriteByte(0);

        long offsetTablePos = ms.Position;
        WriteI64(ms, 0);

        long chunkStart = ms.Position;
        long savedPos = ms.Position;
        ms.Position = offsetTablePos;
        WriteI64(ms, chunkStart);
        ms.Position = savedPos;

        WriteI32(ms, 0);
        WriteI32(ms, dwaBytes.Length);
        ms.Write(dwaBytes);

        return ms.ToArray();
    }

    /// <summary>
    /// Encode AC tokens with PIZ's canonical Huffman + 20-byte header.
    /// Output layout: u32 hufLength + u32 im + u32 iM + u32 0 +
    /// u32 nBits + u32 0 + packed code-length table + encoded bits.
    /// </summary>
    private static byte[] HuffmanEncodeAc(ushort[] tokens)
    {
        var freq = new int[ExrPiz.HufEncSize];
        foreach (var t in tokens) freq[t]++;

        // PIZ convention: RLE marker symbol sits at the top of the
        // alphabet (HufEncSize - 1 = 65536). Reserve one count for it
        // even if it never fires.
        int rlc = ExrPiz.HufEncSize - 1;
        if (freq[rlc] == 0) freq[rlc] = 1;

        ExrPiz.FrequenciesToCodeLengths(freq);

        int im = 0; while (im < ExrPiz.HufEncSize && freq[im] == 0) im++;
        int iM = rlc;

        var codes = ExrPiz.BuildCanonicalCodes(freq, im, iM);
        var (encodedBits, nBits) = ExrPiz.HuffmanEncode(codes, tokens, rlc);

        var packedTable = ExrPiz.PackEncTable(freq, im, iM);
        int hufLen = 20 + packedTable.Length + encodedBits.Length;

        using var ms = new MemoryStream();
        WriteI32(ms, hufLen);
        WriteI32(ms, im);
        WriteI32(ms, iM);
        WriteI32(ms, 0);
        WriteI32(ms, nBits);
        WriteI32(ms, 0);
        ms.Write(packedTable);
        ms.Write(encodedBits);
        return ms.ToArray();
    }

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
        WriteI32(s, 1);
        s.WriteByte(1);
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
