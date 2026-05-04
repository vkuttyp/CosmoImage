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
/// Round 140 — EXR UINT pixel type (32-bit unsigned int per channel).
/// Completes the EXR pixel-type matrix (HALF in 127, FLOAT in 135,
/// UINT here). Used by depth / object-id / mask passes in production
/// VFX pipelines where exact integer precision matters.
/// </summary>
public class Round140Tests
{
    private static byte[] BuildScanlineUintExr(uint[] rgb, int width, int height)
    {
        using var ms = new MemoryStream();
        WriteI32(ms, 0x01312F76);
        WriteI32(ms, 2);

        WriteAttribute(ms, "channels", "chlist", buf =>
        {
            WriteChannel(buf, "B", pixelType: 0);
            WriteChannel(buf, "G", pixelType: 0);
            WriteChannel(buf, "R", pixelType: 0);
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

            // Per spec: alphabetical (B, G, R), each row's samples consecutive.
            for (int x = 0; x < width; x++) WriteU32(ms, rgb[(y * width + x) * 3 + 2]);
            for (int x = 0; x < width; x++) WriteU32(ms, rgb[(y * width + x) * 3 + 1]);
            for (int x = 0; x < width; x++) WriteU32(ms, rgb[(y * width + x) * 3 + 0]);
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
    private static void WriteU32(Stream s, uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); s.Write(b); }
    private static void WriteI64(Stream s, long v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64LittleEndian(b, v); s.Write(b); }
    private static void WriteF32(Stream s, float v) => WriteI32(s, BitConverter.SingleToInt32Bits(v));

    private static uint[] ReadUInts(byte[] raw)
    {
        var u = new uint[raw.Length / 4];
        Buffer.BlockCopy(raw, 0, u, 0, raw.Length);
        return u;
    }

    [Fact]
    public void Pure_UintExr_ExactRoundTrip()
    {
        // UINT channels store exact 32-bit unsigned values; round-trip
        // should be byte-identical. Test with values spanning the
        // dynamic range including high bits that floats couldn't
        // represent exactly.
        int w = 8, h = 4;
        var rgb = new uint[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 3;
                rgb[o]     = (uint)(x * 0x10001 + y);
                rgb[o + 1] = 0xDEAD_BEEFu - (uint)(x + y);
                rgb[o + 2] = 0xFFFF_FFFFu - (uint)((y * w + x) * 7);
            }
        var exr = BuildScanlineUintExr(rgb, w, h);

        var img = PureExrDecoder.TryDecode(exr);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
        Assert.Equal(3, img.Bands);
        Assert.Equal(VipsBandFormat.UInt, img.BandFormat);

        var got = ReadUInts(img.PixelsLazy!.Value);
        for (int i = 0; i < rgb.Length; i++)
            Assert.Equal(rgb[i], got[i]);
    }

    [Fact]
    public async Task LoadAsync_UintExr_TakesPureFastPath()
    {
        int w = 16, h = 8;
        var rgb = new uint[w * h * 3];
        for (int i = 0; i < rgb.Length; i++) rgb[i] = (uint)(i * 0x9E3779B1u);
        var exr = BuildScanlineUintExr(rgb, w, h);
        var src = new PipeVipsSource(PipeReader.Create(new MemoryStream(exr)));
        var img = await VipsExrLoader.LoadAsync(src);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
        Assert.Equal(VipsBandFormat.UInt, img.BandFormat);
        var got = ReadUInts(img.PixelsLazy!.Value);
        for (int i = 0; i < rgb.Length; i++) Assert.Equal(rgb[i], got[i]);
    }
}
