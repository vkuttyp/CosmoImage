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
/// Round 136 — tiled EXR with MIPMAP_LEVELS / RIPMAP_LEVELS modes.
/// We expose level 0 (full resolution) only; sub-levels are walked
/// past in the offset table but their tile data is skipped. Common
/// in production EXRs that pre-bake mip chains for texture loaders.
/// </summary>
public class Round136Tests
{
    private static byte[] BuildMipmapExr(float[] rgb, int width, int height, int tileW, int tileH)
    {
        // ROUND_DOWN: numLevels = floor(log2(max(W, H))) + 1
        int n = Math.Max(width, height);
        int numLevels = FloorLog2(n) + 1;

        using var ms = new MemoryStream();
        WriteI32(ms, 0x01312F76);
        WriteI32(ms, 2 | 0x200);

        WriteAttribute(ms, "channels", "chlist", buf =>
        {
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
            buf.WriteByte(1);  // mode = MIPMAP_LEVELS + ROUND_DOWN
        });
        ms.WriteByte(0);

        // Total tiles across all levels.
        int totalTiles = 0;
        var levelDims = new (int w, int h, int tx, int ty)[numLevels];
        for (int l = 0; l < numLevels; l++)
        {
            int wL = Math.Max(1, width >> l);
            int hL = Math.Max(1, height >> l);
            int txL = (wL + tileW - 1) / tileW;
            int tyL = (hL + tileH - 1) / tileH;
            levelDims[l] = (wL, hL, txL, tyL);
            totalTiles += txL * tyL;
        }

        long offsetTablePos = ms.Position;
        for (int i = 0; i < totalTiles; i++) WriteI64(ms, 0);

        int entryIndex = 0;

        // Level 0 — encode the actual input pixels (test asserts these).
        for (int ty = 0; ty < levelDims[0].ty; ty++)
            for (int tx = 0; tx < levelDims[0].tx; tx++)
            {
                int rows = Math.Min(tileH, height - ty * tileH);
                int cols = Math.Min(tileW, width - tx * tileW);

                long blockStart = ms.Position;
                long savedPos = ms.Position;
                ms.Position = offsetTablePos + entryIndex * 8;
                WriteI64(ms, blockStart);
                ms.Position = savedPos;
                entryIndex++;

                WriteI32(ms, tx);
                WriteI32(ms, ty);
                WriteI32(ms, 0);
                WriteI32(ms, 0);
                int dataSize = rows * cols * 3 * 2;
                WriteI32(ms, dataSize);

                for (int row = 0; row < rows; row++)
                {
                    int y = ty * tileH + row;
                    for (int col = 0; col < cols; col++)
                    { int x = tx * tileW + col; WriteHalf(ms, rgb[(y * width + x) * 3 + 2]); }  // B
                    for (int col = 0; col < cols; col++)
                    { int x = tx * tileW + col; WriteHalf(ms, rgb[(y * width + x) * 3 + 1]); }  // G
                    for (int col = 0; col < cols; col++)
                    { int x = tx * tileW + col; WriteHalf(ms, rgb[(y * width + x) * 3 + 0]); }  // R
                }
            }

        // Levels 1+ — synthetic data; the decoder skips these but they
        // must exist on disk so the file is well-formed.
        for (int l = 1; l < numLevels; l++)
        {
            for (int ty = 0; ty < levelDims[l].ty; ty++)
                for (int tx = 0; tx < levelDims[l].tx; tx++)
                {
                    int rows = Math.Min(tileH, levelDims[l].h - ty * tileH);
                    int cols = Math.Min(tileW, levelDims[l].w - tx * tileW);

                    long blockStart = ms.Position;
                    long savedPos = ms.Position;
                    ms.Position = offsetTablePos + entryIndex * 8;
                    WriteI64(ms, blockStart);
                    ms.Position = savedPos;
                    entryIndex++;

                    WriteI32(ms, tx);
                    WriteI32(ms, ty);
                    WriteI32(ms, l);
                    WriteI32(ms, l);  // MIPMAP: levelX == levelY
                    int dataSize = rows * cols * 3 * 2;
                    WriteI32(ms, dataSize);
                    for (int i = 0; i < rows * cols * 3; i++) WriteHalf(ms, 0.0f);
                }
        }

        return ms.ToArray();
    }

    private static byte[] BuildRipmapExr(float[] rgb, int width, int height, int tileW, int tileH)
    {
        int numXLevels = FloorLog2(width) + 1;
        int numYLevels = FloorLog2(height) + 1;

        using var ms = new MemoryStream();
        WriteI32(ms, 0x01312F76);
        WriteI32(ms, 2 | 0x200);

        WriteAttribute(ms, "channels", "chlist", buf =>
        {
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
            buf.WriteByte(2);  // mode = RIPMAP_LEVELS + ROUND_DOWN
        });
        ms.WriteByte(0);

        // Total tile count = sum over (lx, ly) of tilesX_lx * tilesY_ly.
        int totalTiles = 0;
        for (int ly = 0; ly < numYLevels; ly++)
        {
            int hL = Math.Max(1, height >> ly);
            int tyL = (hL + tileH - 1) / tileH;
            for (int lx = 0; lx < numXLevels; lx++)
            {
                int wL = Math.Max(1, width >> lx);
                int txL = (wL + tileW - 1) / tileW;
                totalTiles += txL * tyL;
            }
        }

        long offsetTablePos = ms.Position;
        for (int i = 0; i < totalTiles; i++) WriteI64(ms, 0);

        int entryIndex = 0;

        // Level (0,0) — full resolution, real data.
        int tilesX = (width + tileW - 1) / tileW;
        int tilesY = (height + tileH - 1) / tileH;
        for (int ty = 0; ty < tilesY; ty++)
            for (int tx = 0; tx < tilesX; tx++)
            {
                int rows = Math.Min(tileH, height - ty * tileH);
                int cols = Math.Min(tileW, width - tx * tileW);

                long blockStart = ms.Position;
                long savedPos = ms.Position;
                ms.Position = offsetTablePos + entryIndex * 8;
                WriteI64(ms, blockStart);
                ms.Position = savedPos;
                entryIndex++;

                WriteI32(ms, tx);
                WriteI32(ms, ty);
                WriteI32(ms, 0);
                WriteI32(ms, 0);
                int dataSize = rows * cols * 3 * 2;
                WriteI32(ms, dataSize);

                for (int row = 0; row < rows; row++)
                {
                    int y = ty * tileH + row;
                    for (int col = 0; col < cols; col++)
                    { int x = tx * tileW + col; WriteHalf(ms, rgb[(y * width + x) * 3 + 2]); }
                    for (int col = 0; col < cols; col++)
                    { int x = tx * tileW + col; WriteHalf(ms, rgb[(y * width + x) * 3 + 1]); }
                    for (int col = 0; col < cols; col++)
                    { int x = tx * tileW + col; WriteHalf(ms, rgb[(y * width + x) * 3 + 0]); }
                }
            }

        // Other (lx, ly) levels — synthetic data; decoder skips them.
        // RIPMAP order: ly outer, lx inner (after (0,0) which we already wrote).
        for (int ly = 0; ly < numYLevels; ly++)
            for (int lx = 0; lx < numXLevels; lx++)
            {
                if (lx == 0 && ly == 0) continue;
                int wL = Math.Max(1, width >> lx);
                int hL = Math.Max(1, height >> ly);
                int txL = (wL + tileW - 1) / tileW;
                int tyL = (hL + tileH - 1) / tileH;
                for (int ty = 0; ty < tyL; ty++)
                    for (int tx = 0; tx < txL; tx++)
                    {
                        int rows = Math.Min(tileH, hL - ty * tileH);
                        int cols = Math.Min(tileW, wL - tx * tileW);

                        long blockStart = ms.Position;
                        long savedPos = ms.Position;
                        ms.Position = offsetTablePos + entryIndex * 8;
                        WriteI64(ms, blockStart);
                        ms.Position = savedPos;
                        entryIndex++;

                        WriteI32(ms, tx);
                        WriteI32(ms, ty);
                        WriteI32(ms, lx);
                        WriteI32(ms, ly);
                        int dataSize = rows * cols * 3 * 2;
                        WriteI32(ms, dataSize);
                        for (int i = 0; i < rows * cols * 3; i++) WriteHalf(ms, 0.0f);
                    }
            }

        // Sanity: the decoder relies on (0,0) being the first numLevel0Tiles
        // entries in the offset table — verify our write order matched that
        // assumption.  RIPMAP order is (ly=0, lx=0), (ly=0, lx=1), …, but
        // since we wrote (0,0) first and skipped it in the second loop, the
        // first tilesX * tilesY entries are indeed level (0,0).
        return ms.ToArray();
    }

    private static int FloorLog2(int x)
    {
        int y = 0;
        while (x > 1) { y++; x >>= 1; }
        return y;
    }

    private static void WriteChannel(Stream s, string name)
    {
        foreach (char c in name) s.WriteByte((byte)c);
        s.WriteByte(0);
        WriteI32(s, 1);
        s.WriteByte(0); s.WriteByte(0); s.WriteByte(0); s.WriteByte(0);
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
            Assert.True(Math.Abs(actual - expected) / Math.Abs(expected) < 0.001f,
                $"expected {expected} got {actual}");
    }

    [Fact]
    public void Pure_MipmapTiledExr_ReturnsFullResolutionLevel()
    {
        int w = 8, h = 8;
        var rgb = new float[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 3;
                rgb[o] = x * 0.1f;
                rgb[o + 1] = y * 0.1f;
                rgb[o + 2] = (x + y) * 0.05f;
            }
        var exr = BuildMipmapExr(rgb, w, h, 4, 4);
        var img = PureExrDecoder.TryDecode(exr);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
        Assert.Equal(3, img.Bands);

        var got = ReadFloats(img.PixelsLazy!.Value);
        for (int i = 0; i < rgb.Length; i++)
            AssertHalfPrecision(rgb[i], got[i]);
    }

    [Fact]
    public void Pure_RipmapTiledExr_ReturnsFullResolutionLevel()
    {
        int w = 8, h = 4;
        var rgb = new float[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 3;
                rgb[o] = x * 0.07f;
                rgb[o + 1] = y * 0.13f;
                rgb[o + 2] = (x ^ y) * 0.02f;
            }
        var exr = BuildRipmapExr(rgb, w, h, 4, 2);
        var img = PureExrDecoder.TryDecode(exr);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);

        var got = ReadFloats(img.PixelsLazy!.Value);
        for (int i = 0; i < rgb.Length; i++)
            AssertHalfPrecision(rgb[i], got[i]);
    }

    [Fact]
    public async Task LoadAsync_MipmapExr_TakesPureFastPath()
    {
        int w = 16, h = 8;
        var rgb = new float[w * h * 3];
        for (int i = 0; i < rgb.Length; i++) rgb[i] = (i % 100) * 0.01f;
        var exr = BuildMipmapExr(rgb, w, h, 8, 4);
        var src = new PipeVipsSource(PipeReader.Create(new MemoryStream(exr)));
        var img = await VipsExrLoader.LoadAsync(src);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
        Assert.Equal(3, img.Bands);
    }
}
