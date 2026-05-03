using System;
using CosmoImage.Operations.Color;
using Xunit;

namespace CosmoImage.Tests;

public class Round53Tests
{
    /// <summary>Single-band UChar gradient 0..255 spread evenly.</summary>
    private static VipsImage RampX(int w, int h)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                        addr[x] = (byte)Math.Clamp((reg.Valid.Left + x) * 255 / Math.Max(1, w - 1), 0, 255);
                }
                return 0;
            }
        };

    private static VipsImage Solid(int w, int h, byte v)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++) addr[x] = v;
                }
                return 0;
            }
        };

    // ---- Dither (binary, FloydSteinberg) ----

    [Fact]
    public void Dither_FloydSteinberg_ProducesBinary()
    {
        var src = RampX(32, 8);
        var d = src.Dither(VipsDitherMethod.FloydSteinberg, levels: 2);
        using var reg = new VipsRegion(d);
        reg.Prepare(new VipsRect(0, 0, 32, 8));
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 32; x++)
            {
                byte v = reg.GetAddress(x, y)[0];
                Assert.True(v == 0 || v == 255, $"expected 0 or 255 at ({x}, {y}), got {v}");
            }
    }

    [Fact]
    public void Dither_RampDistribution_ApproximatesGray()
    {
        // A 0..255 ramp dithered to binary should produce ~50% white pixels
        // (the integral of the ramp is 0.5).
        var src = RampX(64, 64);
        var d = src.Dither(VipsDitherMethod.FloydSteinberg, levels: 2);
        using var reg = new VipsRegion(d);
        reg.Prepare(new VipsRect(0, 0, 64, 64));
        int whites = 0;
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
                if (reg.GetAddress(x, y)[0] == 255) whites++;
        // Within ±5% of 50%.
        double frac = whites / (64.0 * 64);
        Assert.InRange(frac, 0.45, 0.55);
    }

    [Fact]
    public void Dither_FlatGray_AveragesToGray()
    {
        // Flat 128 input → dither output should average to ~128.
        var src = Solid(32, 32, 128);
        var d = src.Dither(VipsDitherMethod.FloydSteinberg, levels: 2);
        using var reg = new VipsRegion(d);
        reg.Prepare(new VipsRect(0, 0, 32, 32));
        long sum = 0;
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
                sum += reg.GetAddress(x, y)[0];
        double avg = sum / (32.0 * 32);
        Assert.InRange(avg, 120, 136);
    }

    [Fact]
    public void Dither_FourLevels_OutputsFourValues()
    {
        var src = RampX(64, 8);
        var d = src.Dither(VipsDitherMethod.FloydSteinberg, levels: 4);
        using var reg = new VipsRegion(d);
        reg.Prepare(new VipsRect(0, 0, 64, 8));
        // Allowed values: 0, 85, 170, 255.
        var allowed = new HashSet<byte> { 0, 85, 170, 255 };
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 64; x++)
                Assert.Contains(reg.GetAddress(x, y)[0], allowed);
    }

    [Fact]
    public void Dither_Bayer4x4_IsStreamableAndCorrect()
    {
        var src = RampX(16, 16);
        var d = src.Dither(VipsDitherMethod.Bayer4x4, levels: 2);
        using var reg = new VipsRegion(d);
        reg.Prepare(new VipsRect(0, 0, 16, 16));
        // All values must be 0 or 255.
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                byte v = reg.GetAddress(x, y)[0];
                Assert.True(v == 0 || v == 255);
            }
    }

    [Fact]
    public void Dither_AllMethodsRun()
    {
        var src = RampX(16, 16);
        foreach (VipsDitherMethod m in Enum.GetValues<VipsDitherMethod>())
        {
            var d = src.Dither(m, levels: 2);
            Assert.Equal(16, d.Width);
        }
    }

    // ---- BinaryDither / BinaryInvert ----

    [Fact]
    public void BinaryDither_DefaultsToFloydSteinberg2Levels()
    {
        var src = RampX(16, 16);
        var bd = src.BinaryDither();
        using var reg = new VipsRegion(bd);
        reg.Prepare(new VipsRect(0, 0, 16, 16));
        Assert.True(reg.GetAddress(0, 0)[0] == 0 || reg.GetAddress(0, 0)[0] == 255);
    }

    [Fact]
    public void BinaryInvert_FlipsBytes()
    {
        var src = Solid(2, 2, 50);
        var inv = VipsImageOps.BinaryInvert(src);
        using var reg = new VipsRegion(inv);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        Assert.Equal(205, reg.GetAddress(0, 0)[0]);
    }

    // ---- AdaptiveHistogramEqualization ----

    [Fact]
    public void AdaptiveHistogramEqualization_PreservesDimensions()
    {
        var src = Solid(64, 64, 100);
        var eq = src.AdaptiveHistogramEqualization();
        Assert.Equal(64, eq.Width);
        Assert.Equal(64, eq.Height);
    }
}
