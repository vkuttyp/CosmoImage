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
/// Round 135 — EXR FLOAT pixel type (32-bit IEEE per channel).
/// HALF is the common case but high-precision HDR / scientific
/// pipelines use FLOAT for exact representation across the full
/// 32-bit range. UINT (32-bit unsigned int) is supported as a
/// future round once VipsBandFormat.UInt is plumbed end-to-end.
/// </summary>
public class Round135Tests
{
    private static byte[] BuildScanlineFloatExr(float[] rgb, int width, int height)
    {
        using var ms = new MemoryStream();
        WriteI32(ms, 0x01312F76);
        WriteI32(ms, 2);

        WriteAttribute(ms, "channels", "chlist", buf =>
        {
            WriteChannel(buf, "B");  // alphabetical channel order
            WriteChannel(buf, "G");
            WriteChannel(buf, "R");
            buf.WriteByte(0);
        });
        WriteAttribute(ms, "compression", "compression", buf => buf.WriteByte(0));
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

        // FLOAT channel data: 4 bytes per sample, 3 channels per pixel,
        // per-channel-then-per-pixel layout within each scanline block.
        for (int y = 0; y < height; y++)
        {
            long blockStart = ms.Position;
            long savedPos = ms.Position;
            ms.Position = offsetTablePos + y * 8;
            WriteI64(ms, blockStart);
            ms.Position = savedPos;

            WriteI32(ms, y);
            int dataSize = 3 * width * 4;
            WriteI32(ms, dataSize);

            // Alphabetical: B then G then R, each row's samples consecutively.
            for (int x = 0; x < width; x++) WriteF32(ms, rgb[(y * width + x) * 3 + 2]);  // B
            for (int x = 0; x < width; x++) WriteF32(ms, rgb[(y * width + x) * 3 + 1]);  // G
            for (int x = 0; x < width; x++) WriteF32(ms, rgb[(y * width + x) * 3 + 0]);  // R
        }
        return ms.ToArray();
    }

    private static void WriteChannel(Stream s, string name)
    {
        foreach (char c in name) s.WriteByte((byte)c);
        s.WriteByte(0);
        WriteI32(s, 2);   // pixelType = FLOAT
        s.WriteByte(0);
        s.WriteByte(0); s.WriteByte(0); s.WriteByte(0);
        WriteI32(s, 1);
        WriteI32(s, 1);
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

    [Fact]
    public void Pure_FloatExr_FullPrecisionRoundTrip()
    {
        // FLOAT channels store exact IEEE-754 single-precision — we
        // expect bit-exact round-trip without the HALF tolerance the
        // Round 127 tests need.
        int w = 8, h = 4;
        var rgb = new float[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 3;
                rgb[o]     = x * 0.123456f;
                rgb[o + 1] = y * 7.89f - 1.0f;
                rgb[o + 2] = (x + y) * 0.0001f + 1000.0f;
            }
        var exr = BuildScanlineFloatExr(rgb, w, h);

        var img = PureExrDecoder.TryDecode(exr);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
        Assert.Equal(3, img.Bands);
        Assert.Equal(VipsBandFormat.Float, img.BandFormat);

        var got = ReadFloats(img.PixelsLazy!.Value);
        for (int i = 0; i < rgb.Length; i++)
            Assert.Equal(rgb[i], got[i]);
    }

    [Fact]
    public async Task LoadAsync_FloatExr_TakesPureFastPath()
    {
        int w = 16, h = 8;
        var rgb = new float[w * h * 3];
        for (int i = 0; i < rgb.Length; i++) rgb[i] = i * 0.01f;
        var exr = BuildScanlineFloatExr(rgb, w, h);
        var src = new PipeVipsSource(PipeReader.Create(new MemoryStream(exr)));
        var img = await VipsExrLoader.LoadAsync(src);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
        Assert.Equal(VipsBandFormat.Float, img.BandFormat);
        var got = ReadFloats(img.PixelsLazy!.Value);
        for (int i = 0; i < rgb.Length; i++) Assert.Equal(rgb[i], got[i]);
    }
}
