using System;
using System.IO;
using System.IO.Pipelines;
using System.Text;
using System.Threading.Tasks;
using CosmoImage.Core;
using CosmoImage.Loaders;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 146 — Radiance HDR old-style RLE. Scanlines that don't open
/// with the new-style 0x02 0x02 marker may carry run markers per Greg
/// Ward's original encoding: a pixel with R=G=B=1 stands for a run
/// of the previous pixel, with the count = (E &lt;&lt; rshift) and
/// rshift accumulating across consecutive markers.
/// </summary>
public class Round146Tests
{
    private static IVipsSource Source(byte[] bytes)
        => new PipeVipsSource(PipeReader.Create(new MemoryStream(bytes)));

    private static byte[] Concat(params byte[][] parts)
    {
        int total = 0;
        foreach (var p in parts) total += p.Length;
        var dst = new byte[total];
        int o = 0;
        foreach (var p in parts) { Buffer.BlockCopy(p, 0, dst, o, p.Length); o += p.Length; }
        return dst;
    }

    /// <summary>Build a Radiance HDR file with the supplied scanline bytes.</summary>
    private static byte[] BuildHdrFile(int width, int height, byte[] scanlineData)
    {
        var hdr = Encoding.ASCII.GetBytes(
            $"#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y {height} +X {width}\n");
        return Concat(hdr, scanlineData);
    }

    private static float DecodeRgbe(byte component, byte e)
        => e == 0 ? 0f : (float)((component + 0.5) * Math.Pow(2.0, e - 136));

    [Fact]
    public async Task OldRle_SinglePixelRun_Replicates()
    {
        // Width 4 (< 8) forces old-style scanline. Layout:
        //   pixel 0 raw:  R=100, G=50, B=25, E=128
        //   run marker:   1, 1, 1, E=2 (rshift=0 → repCount=2)
        //   pixel 3 raw:  R=200, G=100, B=50, E=128
        // Decoded: pixels 0, 0, 0, 3 (1 raw + 2 from run + 1 raw = 4).
        var scanline = new byte[]
        {
            100, 50, 25, 128,   // pixel 0
            1, 1, 1, 2,         // run marker: replicate pixel 0 twice (pixels 1, 2)
            200, 100, 50, 128,  // pixel 3
        };
        var bytes = BuildHdrFile(width: 4, height: 1, scanline);

        var img = await VipsHdrLoader.LoadAsync(Source(bytes));
        Assert.NotNull(img);
        Assert.Equal(4, img!.Width);
        Assert.Equal(1, img.Height);

        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, 4, 1));

        // Pixels 0..2 should all decode to the same value (the original pixel 0).
        float r0 = ReadFloat(reg, 0, 0, 0);
        float r1 = ReadFloat(reg, 1, 0, 0);
        float r2 = ReadFloat(reg, 2, 0, 0);
        Assert.Equal(r0, r1);
        Assert.Equal(r0, r2);
        Assert.Equal(DecodeRgbe(100, 128), r0, 0.001f);

        // Pixel 3 should decode to the second raw RGBE.
        Assert.Equal(DecodeRgbe(200, 128), ReadFloat(reg, 3, 0, 0), 0.001f);
    }

    [Fact]
    public async Task OldRle_MultipleMarkers_ShiftAccumulates()
    {
        // Two consecutive markers: E values combine as e0 + (e1 << 8).
        //   pixel 0 raw:    R=10, G=20, B=30, E=128
        //   marker 1:       1, 1, 1, E=3 (rshift=0 → 3 copies)  → pixels 1, 2, 3
        //   marker 2:       1, 1, 1, E=1 (rshift=8 → 256 copies, capped at remaining)
        //                   → fills remaining pixels.
        // For a width=8 scanline this means 1 raw + 3 + 4-remaining.
        var scanline = new byte[]
        {
            10, 20, 30, 128,
            1, 1, 1, 3,    // 3 copies of pixel 0
            1, 1, 1, 1,    // 1 << 8 = 256 copies (capped at 4 remaining)
        };
        // Width 7 to stay below new-RLE's 8-pixel threshold.
        var bytes = BuildHdrFile(width: 7, height: 1, scanline);

        var img = await VipsHdrLoader.LoadAsync(Source(bytes));
        Assert.NotNull(img);
        using var reg = new VipsRegion(img!);
        reg.Prepare(new VipsRect(0, 0, 7, 1));
        float expected = DecodeRgbe(10, 128);
        for (int x = 0; x < 7; x++)
            Assert.Equal(expected, ReadFloat(reg, x, 0, 0), 0.001f);
    }

    [Fact]
    public async Task OldRle_LiteralOnly_RoundTrips()
    {
        // Width-7 scanline of pure literal pixels (no run markers). The
        // old-RLE path should pass them through unchanged because no
        // R=G=B=1 quad ever appears.
        var scanline = new byte[]
        {
            10, 20, 30, 128,
            40, 50, 60, 129,
            70, 80, 90, 130,
            100, 110, 120, 131,
            130, 140, 150, 132,
            160, 170, 180, 133,
            190, 200, 210, 134,
        };
        var bytes = BuildHdrFile(width: 7, height: 1, scanline);

        var img = await VipsHdrLoader.LoadAsync(Source(bytes));
        Assert.NotNull(img);
        using var reg = new VipsRegion(img!);
        reg.Prepare(new VipsRect(0, 0, 7, 1));
        for (int x = 0; x < 7; x++)
        {
            byte expR = scanline[x * 4];
            byte expE = scanline[x * 4 + 3];
            Assert.Equal(DecodeRgbe(expR, expE), ReadFloat(reg, x, 0, 0), 0.001f);
        }
    }

    private static float ReadFloat(VipsRegion reg, int x, int y, int bnd)
        => System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(
            reg.GetAddress(x, y).Slice(bnd * 4, 4));
}
