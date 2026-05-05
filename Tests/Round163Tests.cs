using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using CosmoImage.Loaders;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 163 — DWA toLinear LUT plumbing. The pLinear flag now flows
/// from the channel header through PureExrDecoder into ExrDwa, so
/// LOSSY_DCT channels with pLinear=0 (libimf default for colour
/// channels) get the toLinear pass: square the HALF after IDCT,
/// undoing the perceptual sqrt the encoder applies.
///
/// <para>Validation against libimf-encoded fixtures is deferred —
/// libimf's VERSION≥1 AC token stream uses an end-of-block marker
/// that ExpandAcTokens doesn't yet recognise. That's a follow-up
/// round.</para>
/// </summary>
public class Round163Tests
{
    [Fact]
    public void DctRoundTrip_PLinearZero_AppliesToLinear()
    {
        // Encode an 8×8 block of HALF values where the encoder
        // pre-applies sqrt (the perceptual encoding libimf does for
        // pLinear=0 channels). The decoder's toLinear pass should
        // square the result, recovering the original linear values.
        ushort[] linearHalves = new ushort[64];
        for (int i = 0; i < 64; i++)
        {
            float v = 0.04f * (i + 1);  // 0.04 .. 2.56, all comfortably HALF
            linearHalves[i] = BitConverter.HalfToUInt16Bits((Half)v);
        }

        // Pre-apply sqrt to mimic the encoder's perceptual pass.
        ushort[] perceptualHalves = new ushort[64];
        for (int i = 0; i < 64; i++)
        {
            float v = (float)BitConverter.UInt16BitsToHalf(linearHalves[i]);
            perceptualHalves[i] = BitConverter.HalfToUInt16Bits((Half)Math.Sqrt(v));
        }

        var exr = BuildSingleBlockDwaaExr(perceptualHalves, pLinear: 0);
        var img = PureExrDecoder.TryDecode(exr);
        Assert.NotNull(img);

        var pix = img!.PixelsLazy!.Value;
        var got = new float[pix.Length / 4];
        Buffer.BlockCopy(pix, 0, got, 0, pix.Length);

        // The decoder squares the post-IDCT HALFs, so output ≈ input
        // linear values (within DCT round-trip + sqrt-then-square noise).
        for (int i = 0; i < 64; i++)
        {
            float expected = (float)BitConverter.UInt16BitsToHalf(linearHalves[i]);
            Assert.True(Math.Abs(got[i] - expected) < 0.05f,
                $"sample {i}: expected {expected}, got {got[i]}");
        }
    }

    private static byte[] BuildSingleBlockDwaaExr(ushort[] inputHalves, byte pLinear)
    {
        var floatBlock = new float[64];
        for (int i = 0; i < 64; i++)
            floatBlock[i] = (float)BitConverter.UInt16BitsToHalf(inputHalves[i]);
        ExrDct.Forward8x8InPlace(floatBlock);

        ushort[] freqHalves = new ushort[64];
        for (int i = 0; i < 64; i++)
            freqHalves[i] = BitConverter.HalfToUInt16Bits((Half)floatBlock[i]);

        ushort dcValue = freqHalves[0];
        var dcRaw = new byte[] { (byte)dcValue, (byte)(dcValue >> 8) };
        var dcCompressed = ZlibWrap(dcRaw);

        var acTokens = ZigzagAndRle(freqHalves);
        var acRaw = new byte[acTokens.Length * 2];
        for (int i = 0; i < acTokens.Length; i++)
        {
            acRaw[i * 2]     = (byte)acTokens[i];
            acRaw[i * 2 + 1] = (byte)(acTokens[i] >> 8);
        }
        var acCompressed = ZlibWrap(acRaw);

        var dwaPayload = new MemoryStream();
        WriteU64(dwaPayload, 0);  // VERSION
        WriteU64(dwaPayload, 0);
        WriteU64(dwaPayload, 0);
        WriteU64(dwaPayload, (ulong)acCompressed.Length);
        WriteU64(dwaPayload, (ulong)dcCompressed.Length);
        WriteU64(dwaPayload, 0);
        WriteU64(dwaPayload, 0);
        WriteU64(dwaPayload, 0);
        WriteU64(dwaPayload, (ulong)acTokens.Length);
        WriteU64(dwaPayload, 1);
        WriteU64(dwaPayload, 1);  // acCompression = 1 (deflate)
        dwaPayload.Write(acCompressed);
        dwaPayload.Write(dcCompressed);
        var dwaBytes = dwaPayload.ToArray();

        using var ms = new MemoryStream();
        WriteI32(ms, 0x01312F76);
        WriteI32(ms, 2);
        WriteAttribute(ms, "channels", "chlist", buf =>
        {
            WriteChannel(buf, "Y", pLinear);
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
        if (zeroRun > 0) tokens.Add((ushort)(0xFF00 | zeroRun));
        return tokens.ToArray();
    }

    private static byte[] ZlibWrap(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            z.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    private static void WriteChannel(Stream s, string name, byte pLinear)
    {
        foreach (char c in name) s.WriteByte((byte)c);
        s.WriteByte(0);
        WriteI32(s, 1);
        s.WriteByte(pLinear);
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
