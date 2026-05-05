using System;
using System.Buffers.Binary;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 174 — <c>fastcor</c> (FFT-accelerated cross-correlation).
/// Output is <c>UInt</c> sized <c>(W−tw+1, H−th+1)</c>; values are the
/// raw <c>Σ in·ref</c> dot-product at each valid position. Tests verify
/// the result against a brute-force spatial reference and that a
/// template extracted from the input recovers its source position as
/// the peak.
/// </summary>
public class Round174Tests
{
    private static VipsImage MakeRandomLike(int w, int h, int seed)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) =>
            {
                var rng = new Random(seed);
                var bytes = new byte[w * h];
                rng.NextBytes(bytes);
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                        addr[x] = bytes[(reg.Valid.Top + y) * w + (reg.Valid.Left + x)];
                }
                return 0;
            }
        };

    private static byte[] Materialise(VipsImage img)
    {
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, img.Width, img.Height));
        var bytes = new byte[img.Width * img.Height];
        for (int y = 0; y < img.Height; y++)
        {
            var line = reg.GetAddress(0, y);
            for (int x = 0; x < img.Width; x++)
                bytes[y * img.Width + x] = line[x];
        }
        return bytes;
    }

    private static uint ReadUInt(VipsImage img, int x, int y)
    {
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, img.Width, img.Height));
        return BinaryPrimitives.ReadUInt32LittleEndian(reg.GetAddress(x, y).Slice(0, 4));
    }

    /// <summary>Brute-force reference: out(x, y) = Σ in(x+u, y+v) · ref(u, v).</summary>
    private static uint[,] BruteCorrelate(byte[] inP, int W, int H, byte[] refP, int tw, int th)
    {
        int outW = W - tw + 1, outH = H - th + 1;
        var result = new uint[outH, outW];
        for (int y = 0; y < outH; y++)
            for (int x = 0; x < outW; x++)
            {
                uint sum = 0;
                for (int v = 0; v < th; v++)
                    for (int u = 0; u < tw; u++)
                        sum += (uint)(inP[(y + v) * W + (x + u)] * refP[v * tw + u]);
                result[y, x] = sum;
            }
        return result;
    }

    [Fact]
    public void Output_Matches_BruteForceReference_SmallImage()
    {
        var inImg = MakeRandomLike(16, 16, seed: 42);
        var refImg = MakeRandomLike(4, 4, seed: 99);
        var corr = inImg.Fastcor(refImg);

        Assert.Equal(13, corr.Width);   // 16 - 4 + 1
        Assert.Equal(13, corr.Height);
        Assert.Equal(VipsBandFormat.UInt, corr.BandFormat);

        var inP = Materialise(inImg);
        var refP = Materialise(refImg);
        var brute = BruteCorrelate(inP, 16, 16, refP, 4, 4);
        for (int y = 0; y < 13; y++)
            for (int x = 0; x < 13; x++)
            {
                uint actual = ReadUInt(corr, x, y);
                uint expected = brute[y, x];
                // FFT path accumulates in doubles; allow ±2 rounding tolerance.
                Assert.InRange((int)actual - (int)expected, -2, 2);
            }
    }

    [Fact]
    public void BrightPatchOnDarkInput_PeaksAtPatchPosition()
    {
        // Raw cross-correlation favours bright regions: Σ in·ref grows with
        // input pixel brightness. To test peak-as-position recovery we need
        // a contrast scenario — uniformly-dark background, single bright
        // patch matching the template. The peak should land at the patch.
        int W = 16, H = 16, tw = 4, th = 4, sx = 5, sy = 7;
        var bytes = new byte[W * H]; // background = 0
        // Place a constant-bright patch at (sx, sy).
        for (int v = 0; v < th; v++)
            for (int u = 0; u < tw; u++)
                bytes[(sy + v) * W + (sx + u)] = 200;

        var inImg = new VipsImage
        {
            Width = W, Height = H, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) =>
            {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                        addr[x] = bytes[(reg.Valid.Top + y) * W + (reg.Valid.Left + x)];
                }
                return 0;
            }
        };

        // Constant-bright template — same shape as the patch.
        var refImg = new VipsImage
        {
            Width = tw, Height = th, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) =>
            {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++) addr[x] = 200;
                }
                return 0;
            }
        };
        var corr = inImg.Fastcor(refImg);

        int outW = W - tw + 1, outH = H - th + 1;
        uint best = 0; int bestX = 0, bestY = 0;
        for (int y = 0; y < outH; y++)
            for (int x = 0; x < outW; x++)
            {
                uint v = ReadUInt(corr, x, y);
                if (v > best) { best = v; bestX = x; bestY = y; }
            }
        Assert.Equal(sx, bestX);
        Assert.Equal(sy, bestY);
        Assert.Equal((uint)(200 * 200 * tw * th), best);
    }

    [Fact]
    public void TemplateLargerThanInput_Rejected()
    {
        var small = MakeRandomLike(8, 8, seed: 1);
        var big = MakeRandomLike(16, 16, seed: 2);
        Assert.Throws<Exception>(() => small.Fastcor(big));
    }

    [Fact]
    public void NonUCharOrMultiBand_Rejected()
    {
        var rgb = new VipsImage
        {
            Width = 16, Height = 16, Bands = 3, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => 0,
        };
        var grey = MakeRandomLike(16, 16, 5);
        Assert.Throws<Exception>(() => grey.Fastcor(rgb));
    }
}
