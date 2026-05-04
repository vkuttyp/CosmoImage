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
/// Round 130 — tiled EXR support (single-level, ONE_LEVEL mode).
/// Tiles partition the image into uniform rectangles for partial
/// random access. Each tile is decoded as one block via the same
/// per-block dispatch used for scanlines.
/// </summary>
public class Round130Tests
{
    /// <summary>
    /// Build a single-part tiled EXR with NO_COMPRESSION HALF channels.
    /// Right- and bottom-edge tiles carry the trimmed pixel count per spec.
    /// </summary>
    private static byte[] BuildTiledExr(float[] rgba, int width, int height,
        int tileW, int tileH, bool hasAlpha)
    {
        using var ms = new MemoryStream();
        WriteI32(ms, 0x01312F76);
        WriteI32(ms, 2 | 0x200);  // version 2 + tiled bit

        WriteAttribute(ms, "channels", "chlist", buf =>
        {
            if (hasAlpha) WriteChannel(buf, "A");
            WriteChannel(buf, "B");
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
        WriteAttribute(ms, "tiles", "tiledesc", buf =>
        {
            WriteI32(buf, tileW);
            WriteI32(buf, tileH);
            buf.WriteByte(0);  // mode = ONE_LEVEL + ROUND_DOWN
        });
        ms.WriteByte(0);

        int tilesX = (width + tileW - 1) / tileW;
        int tilesY = (height + tileH - 1) / tileH;
        int numTiles = tilesX * tilesY;

        long offsetTablePos = ms.Position;
        for (int i = 0; i < numTiles; i++) WriteI64(ms, 0);

        int channelsInFile = hasAlpha ? 4 : 3;

        for (int ty = 0; ty < tilesY; ty++)
            for (int tx = 0; tx < tilesX; tx++)
            {
                int rows = Math.Min(tileH, height - ty * tileH);
                int cols = Math.Min(tileW, width - tx * tileW);

                long blockStart = ms.Position;
                long savedPos = ms.Position;
                ms.Position = offsetTablePos + (ty * tilesX + tx) * 8;
                WriteI64(ms, blockStart);
                ms.Position = savedPos;

                WriteI32(ms, tx);
                WriteI32(ms, ty);
                WriteI32(ms, 0);  // levelX
                WriteI32(ms, 0);  // levelY
                int dataSize = rows * cols * channelsInFile * 2;
                WriteI32(ms, dataSize);

                // Tile data: per row, per channel (alphabetical), per pixel.
                for (int row = 0; row < rows; row++)
                {
                    int y = ty * tileH + row;
                    if (hasAlpha)
                        for (int col = 0; col < cols; col++)
                        { int x = tx * tileW + col; WriteHalf(ms, rgba[(y * width + x) * 4 + 3]); }
                    for (int col = 0; col < cols; col++)
                    { int x = tx * tileW + col; WriteHalf(ms, rgba[(y * width + x) * 4 + 2]); }
                    for (int col = 0; col < cols; col++)
                    { int x = tx * tileW + col; WriteHalf(ms, rgba[(y * width + x) * 4 + 1]); }
                    for (int col = 0; col < cols; col++)
                    { int x = tx * tileW + col; WriteHalf(ms, rgba[(y * width + x) * 4 + 0]); }
                }
            }

        return ms.ToArray();
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
    private static void WriteHalf(Stream s, float v)
    {
        ushort h = BitConverter.HalfToUInt16Bits((Half)v);
        Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, h); s.Write(b);
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
    public void Pure_TiledRgbHalf_AlignedTiles_RoundTrips()
    {
        // 32x16 image with 16x8 tiles → 2x2 tile grid, all tiles full-sized.
        int w = 32, h = 16;
        var rgba = new float[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                rgba[o] = x * 0.05f;
                rgba[o + 1] = y * 0.1f;
                rgba[o + 2] = (x + y) * 0.025f;
            }
        var exr = BuildTiledExr(rgba, w, h, 16, 8, hasAlpha: false);
        var img = PureExrDecoder.TryDecode(exr);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
        Assert.Equal(3, img.Bands);

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
    public void Pure_TiledRgbHalf_EdgeTiles_RoundTrips()
    {
        // 30x20 image with 16x8 tiles → 2x3 grid where:
        //   right column (tx=1) has 14 cols
        //   bottom row (ty=2) has 4 rows
        int w = 30, h = 20;
        var rgba = new float[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                rgba[o] = x * 0.07f;
                rgba[o + 1] = y * 0.05f;
                rgba[o + 2] = (x ^ y) * 0.01f;
            }
        var exr = BuildTiledExr(rgba, w, h, 16, 8, hasAlpha: false);
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
    public async Task LoadAsync_TiledRgbaHalf_TakesPureFastPath()
    {
        int w = 24, h = 16;
        var rgba = new float[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            rgba[i * 4] = i * 0.01f;
            rgba[i * 4 + 1] = i * 0.005f;
            rgba[i * 4 + 2] = i * 0.0025f;
            rgba[i * 4 + 3] = 0.9f;
        }
        var exr = BuildTiledExr(rgba, w, h, 8, 8, hasAlpha: true);
        var src = new PipeVipsSource(PipeReader.Create(new MemoryStream(exr)));
        var img = await VipsExrLoader.LoadAsync(src);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
        Assert.Equal(4, img.Bands);
    }
}
