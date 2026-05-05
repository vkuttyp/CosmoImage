using System;
using System.Buffers.Binary;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 173 — phase correlation (image registration). The result of
/// <c>Phasecor(a, b)</c> is a Float image whose peak coordinate is the
/// translation that best aligns <c>a</c> with <c>b</c>. Tests fabricate
/// a base + shifted-base pair and verify the recovered peak position.
/// </summary>
public class Round173Tests
{
    /// <summary>
    /// 32×32 1-band UChar with a deterministic non-trivial pattern so the
    /// phase-correlation peak is sharp. (A constant image produces no peak.)
    /// </summary>
    private static VipsImage MakePattern(int w, int h)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) =>
            {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        int gx = reg.Valid.Left + x;
                        int gy = reg.Valid.Top + y;
                        // A diagonal pattern with multiple frequencies.
                        int v = (gx * 11 + gy * 7) ^ ((gx * gy) & 0xFF);
                        addr[x] = (byte)(v & 0xFF);
                    }
                }
                return 0;
            }
        };

    /// <summary>
    /// Cyclically (toroidal) shift an image by (dx, dy). This is what phase
    /// correlation natively recovers — the FFT is intrinsically periodic.
    /// </summary>
    private static VipsImage TorShift(VipsImage src, int dx, int dy)
    {
        int w = src.Width, h = src.Height;
        var srcReg = new VipsRegion(src);
        srcReg.Prepare(new VipsRect(0, 0, w, h));
        var bytes = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            var srcLine = srcReg.GetAddress(0, y);
            for (int x = 0; x < w; x++)
                bytes[y * w + x] = srcLine[x];
        }
        return new VipsImage
        {
            Width = w, Height = h, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) =>
            {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    int gy = reg.Valid.Top + y;
                    int sy = ((gy - dy) % h + h) % h;
                    var addr = reg.GetAddress(reg.Valid.Left, gy);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        int gx = reg.Valid.Left + x;
                        int sx = ((gx - dx) % w + w) % w;
                        addr[x] = bytes[sy * w + sx];
                    }
                }
                return 0;
            }
        };
    }

    private static (int x, int y, float value) Argmax(VipsImage floatImg)
    {
        int w = floatImg.Width, h = floatImg.Height;
        using var reg = new VipsRegion(floatImg);
        reg.Prepare(new VipsRect(0, 0, w, h));
        float best = float.NegativeInfinity;
        int bestX = 0, bestY = 0;
        for (int y = 0; y < h; y++)
        {
            var line = reg.GetAddress(0, y);
            for (int x = 0; x < w; x++)
            {
                float v = BinaryPrimitives.ReadSingleLittleEndian(line.Slice(x * 4, 4));
                if (v > best) { best = v; bestX = x; bestY = y; }
            }
        }
        return (bestX, bestY, best);
    }

    [Fact]
    public void IdenticalInputs_PeakAtOrigin()
    {
        var src = MakePattern(32, 32);
        var pc = src.Phasecor(src);
        var (x, y, v) = Argmax(pc);
        Assert.Equal(0, x);
        Assert.Equal(0, y);
        Assert.True(v > 0.99f, $"peak value at origin should be ≈1, got {v}");
    }

    [Theory]
    [InlineData(3, 0)]
    [InlineData(0, 5)]
    [InlineData(7, 4)]
    [InlineData(-2, -3)]
    public void Translation_RecoveredAsPeakCoordinate(int dx, int dy)
    {
        int w = 32, h = 32;
        var src = MakePattern(w, h);
        var shifted = TorShift(src, dx, dy);
        // Phasecor's whitened cross-spectrum is A·conj(B). For B = A shifted
        // by +d, this gives |A|²·exp(+i·2π·k·d/N); the inverse FFT (which
        // uses the +k convention) places the delta at position -d mod N.
        // So Phasecor(src, shifted_by_+d) peaks at -d mod N.
        var pc = src.Phasecor(shifted);
        var (x, y, v) = Argmax(pc);
        int expectedX = ((-dx % w) + w) % w;
        int expectedY = ((-dy % h) + h) % h;
        Assert.Equal(expectedX, x);
        Assert.Equal(expectedY, y);
        Assert.True(v > 0.5f, $"peak should be sharp after translation; got {v}");
    }

    [Fact]
    public void DifferentSizes_Rejected()
    {
        var a = MakePattern(32, 32);
        var b = MakePattern(16, 32);
        Assert.Throws<Exception>(() => a.Phasecor(b));
    }

    [Fact]
    public void NonUCharOrMultiBand_Rejected()
    {
        var rgb = new VipsImage
        {
            Width = 16, Height = 16, Bands = 3, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => 0,
        };
        var grey = MakePattern(16, 16);
        Assert.Throws<Exception>(() => grey.Phasecor(rgb));
    }
}
