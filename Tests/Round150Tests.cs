using System;
using System.Buffers.Binary;
using System.IO;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 150 — accept arbitrary 1-4 channel EXRs. The previous
/// channel resolver only recognised R/G/B[A] and Y; Z (depth),
/// U/V (motion vectors), and any other VFX-domain channel set
/// fell back to Magick. The generic fallback returns channels
/// in the file's alphabetical order with band count == channel count.
/// </summary>
public class Round150Tests
{
    [Fact]
    public void Pure_SingleZChannelExr_Decodes()
    {
        // 4×2 single-Z FLOAT EXR. Z is not in the recognised set
        // (R/G/B/A/Y), so the generic fallback should map it to
        // 1-band output.
        int w = 4, h = 2;
        var z = new float[w * h];
        for (int i = 0; i < z.Length; i++) z[i] = i * 0.1f;
        var exr = BuildScanlineExr(channelNames: new[] { "Z" }, w, h,
            samplesProvider: (cy, ch, cx) => BitConverter.SingleToInt32Bits(z[cy * w + cx]),
            pixelType: 2,  // FLOAT
            bytesPerSample: 4);

        var img = PureExrDecoder.TryDecode(exr);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
        Assert.Equal(1, img.Bands);
        Assert.Equal(VipsBandFormat.Float, img.BandFormat);

        var pix = img.PixelsLazy!.Value;
        var got = new float[pix.Length / 4];
        Buffer.BlockCopy(pix, 0, got, 0, pix.Length);
        for (int i = 0; i < z.Length; i++) Assert.Equal(z[i], got[i]);
    }

    [Fact]
    public void Pure_TwoChannelMotionExr_Decodes()
    {
        // 4×2 motion-vector EXR (U + V channels). File ordering is
        // alphabetical (U comes before V), so band 0 = U, band 1 = V.
        int w = 4, h = 2;
        var u = new float[w * h];
        var v = new float[w * h];
        for (int i = 0; i < u.Length; i++) { u[i] = i * 0.5f; v[i] = -i * 0.25f; }

        var exr = BuildScanlineExr(channelNames: new[] { "U", "V" }, w, h,
            samplesProvider: (cy, ch, cx) =>
            {
                int idx = cy * w + cx;
                return ch == 0
                    ? BitConverter.SingleToInt32Bits(u[idx])
                    : BitConverter.SingleToInt32Bits(v[idx]);
            },
            pixelType: 2, bytesPerSample: 4);

        var img = PureExrDecoder.TryDecode(exr);
        Assert.NotNull(img);
        Assert.Equal(2, img!.Bands);

        var pix = img.PixelsLazy!.Value;
        var got = new float[pix.Length / 4];
        Buffer.BlockCopy(pix, 0, got, 0, pix.Length);
        for (int i = 0; i < u.Length; i++)
        {
            Assert.Equal(u[i], got[i * 2]);
            Assert.Equal(v[i], got[i * 2 + 1]);
        }
    }

    [Fact]
    public void Pure_RgbExrWithExtraZ_StillReturnsRgb()
    {
        // R + G + B + Z. Resolver should pick the RGB triplet and
        // ignore Z, so output is 3-band (not 4-band).
        int w = 4, h = 2;
        var r = new float[w * h];
        var gg = new float[w * h];
        var b = new float[w * h];
        var z = new float[w * h];
        for (int i = 0; i < r.Length; i++)
        { r[i] = 1.0f; gg[i] = 0.5f; b[i] = 0.0f; z[i] = 99.0f; }

        // File order is alphabetical: B, G, R, Z.
        var exr = BuildScanlineExr(channelNames: new[] { "B", "G", "R", "Z" }, w, h,
            samplesProvider: (cy, ch, cx) =>
            {
                int idx = cy * w + cx;
                float[] arr = ch switch { 0 => b, 1 => gg, 2 => r, _ => z };
                return BitConverter.SingleToInt32Bits(arr[idx]);
            },
            pixelType: 2, bytesPerSample: 4);

        var img = PureExrDecoder.TryDecode(exr);
        Assert.NotNull(img);
        Assert.Equal(3, img!.Bands);  // RGB only — Z dropped

        var pix = img.PixelsLazy!.Value;
        var got = new float[pix.Length / 4];
        Buffer.BlockCopy(pix, 0, got, 0, pix.Length);
        Assert.Equal(1.0f, got[0]); Assert.Equal(0.5f, got[1]); Assert.Equal(0.0f, got[2]);
    }

    /// <summary>
    /// Hand-build a scanline EXR with arbitrary channels. Matches the
    /// existing Round-135/140 fixture utilities; samplesProvider gets
    /// (y, channelIndex, x) and returns the raw 4-byte sample bits
    /// (as int32; both float and uint round-trip through this).
    /// </summary>
    private static byte[] BuildScanlineExr(string[] channelNames, int width, int height,
        Func<int, int, int, int> samplesProvider, int pixelType, int bytesPerSample)
    {
        // Channels must be stored alphabetically — sort caller's names.
        var sorted = (string[])channelNames.Clone();
        Array.Sort(sorted, StringComparer.Ordinal);

        using var ms = new MemoryStream();
        WriteI32(ms, 0x01312F76);
        WriteI32(ms, 2);

        WriteAttribute(ms, "channels", "chlist", buf =>
        {
            foreach (var name in sorted) WriteChannel(buf, name, pixelType);
            buf.WriteByte(0);
        });
        WriteAttribute(ms, "compression", "compression", buf => buf.WriteByte(0));
        WriteAttribute(ms, "dataWindow", "box2i", buf =>
        { WriteI32(buf, 0); WriteI32(buf, 0); WriteI32(buf, width - 1); WriteI32(buf, height - 1); });
        WriteAttribute(ms, "displayWindow", "box2i", buf =>
        { WriteI32(buf, 0); WriteI32(buf, 0); WriteI32(buf, width - 1); WriteI32(buf, height - 1); });
        WriteAttribute(ms, "lineOrder", "lineOrder", buf => buf.WriteByte(0));
        WriteAttribute(ms, "pixelAspectRatio", "float", buf => WriteI32(buf, BitConverter.SingleToInt32Bits(1.0f)));
        WriteAttribute(ms, "screenWindowCenter", "v2f", buf => { WriteI32(buf, 0); WriteI32(buf, 0); });
        WriteAttribute(ms, "screenWindowWidth", "float", buf => WriteI32(buf, BitConverter.SingleToInt32Bits(1.0f)));
        ms.WriteByte(0);

        long offsetTablePos = ms.Position;
        for (int y = 0; y < height; y++) WriteI64(ms, 0);

        for (int y = 0; y < height; y++)
        {
            long blockStart = ms.Position;
            long savedPos = ms.Position;
            ms.Position = offsetTablePos + y * 8;
            WriteI64(ms, blockStart);
            ms.Position = savedPos;

            WriteI32(ms, y);
            int dataSize = sorted.Length * width * bytesPerSample;
            WriteI32(ms, dataSize);

            // Each row: per channel (alphabetical), all samples.
            for (int ch = 0; ch < sorted.Length; ch++)
            {
                for (int x = 0; x < width; x++)
                {
                    int bits = samplesProvider(y, ch, x);
                    Span<byte> bytes = stackalloc byte[4];
                    BinaryPrimitives.WriteInt32LittleEndian(bytes, bits);
                    ms.Write(bytes);
                }
            }
        }
        return ms.ToArray();
    }

    private static void WriteChannel(Stream s, string name, int pixelType)
    {
        foreach (char c in name) s.WriteByte((byte)c);
        s.WriteByte(0);
        WriteI32(s, pixelType);
        s.WriteByte(0);
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
}
