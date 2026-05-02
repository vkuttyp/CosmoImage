using System;
using System.Buffers.Binary;
using Xunit;

namespace CosmoImage.Tests;

public class Round39Tests
{
    private static float ReadFloat(VipsRegion reg, int x, int y)
        => BinaryPrimitives.ReadSingleLittleEndian(reg.GetAddress(x, y).Slice(0, 4));

    // ---- Logmat ----

    [Fact]
    public void Logmat_PeakIsNegativeAtCentre()
    {
        // LoG centre value: (0 - 2σ²)/σ⁴ * exp(0) = -2/σ² (negative).
        var k = VipsImageOps.Logmat(sigma: 1.0, minAmpl: 0.05);
        using var reg = new VipsRegion(k);
        reg.Prepare(new VipsRect(0, 0, k.Width, k.Height));
        int cx = k.Width / 2, cy = k.Height / 2;
        float centre = ReadFloat(reg, cx, cy);
        Assert.True(centre < 0, $"LoG centre should be negative, got {centre}");
        // The corner should be ≥ 0 (positive ring further out).
        float corner = ReadFloat(reg, 0, 0);
        Assert.True(corner > centre);
    }

    // ---- Gaussnoise ----

    [Fact]
    public void Gaussnoise_HasApproximateMeanAndSigma()
    {
        var img = VipsImageOps.Gaussnoise(64, 64, mean: 100, sigma: 20, seed: 42);
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, 64, 64));
        double sum = 0, sumSq = 0;
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                double v = ReadFloat(reg, x, y);
                sum += v; sumSq += v * v;
            }
        double mean = sum / (64 * 64);
        double sigma = Math.Sqrt(sumSq / (64 * 64) - mean * mean);
        Assert.InRange(mean, 99, 101);
        Assert.InRange(sigma, 18, 22);
    }

    [Fact]
    public void Gaussnoise_DeterministicForSameSeed()
    {
        var a = VipsImageOps.Gaussnoise(8, 8, seed: 123);
        var b = VipsImageOps.Gaussnoise(8, 8, seed: 123);
        using var ra = new VipsRegion(a);
        using var rb = new VipsRegion(b);
        ra.Prepare(new VipsRect(0, 0, 8, 8));
        rb.Prepare(new VipsRect(0, 0, 8, 8));
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                Assert.Equal(ReadFloat(ra, x, y), ReadFloat(rb, x, y));
    }

    // ---- Perlin ----

    [Fact]
    public void Perlin_OutputInPlausibleRange()
    {
        var img = VipsImageOps.Perlin(32, 32, cellSize: 16, seed: 1);
        Assert.Equal(VipsBandFormat.Float, img.BandFormat);
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, 32, 32));
        // Perlin theoretical range is [-√(N/2), +√(N/2)] ≈ ±1 for 2D; we
        // assert the practical bound used by Ken Perlin's reference impl.
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
            {
                float v = ReadFloat(reg, x, y);
                Assert.InRange(v, -1.5f, 1.5f);
            }
    }

    [Fact]
    public void Perlin_AtCornerIsZero()
    {
        // (0, 0) sits on a lattice point — Perlin noise at a lattice
        // point is identically 0.
        var img = VipsImageOps.Perlin(8, 8, cellSize: 8, seed: 7);
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, 8, 8));
        Assert.Equal(0f, ReadFloat(reg, 0, 0), 5);
    }

    // ---- Worley ----

    [Fact]
    public void Worley_ProducesNonNegativeDistances()
    {
        var img = VipsImageOps.Worley(32, 32, cellSize: 16, seed: 1);
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, 32, 32));
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
                Assert.True(ReadFloat(reg, x, y) >= 0);
    }

    [Fact]
    public void Worley_DeterministicForSameSeed()
    {
        var a = VipsImageOps.Worley(8, 8, cellSize: 4, seed: 99);
        var b = VipsImageOps.Worley(8, 8, cellSize: 4, seed: 99);
        using var ra = new VipsRegion(a);
        using var rb = new VipsRegion(b);
        ra.Prepare(new VipsRect(0, 0, 8, 8));
        rb.Prepare(new VipsRect(0, 0, 8, 8));
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                Assert.Equal(ReadFloat(ra, x, y), ReadFloat(rb, x, y));
    }

    // ---- Sdf ----

    [Fact]
    public void SdfCircle_ZeroOnBoundary_NegativeInside()
    {
        var sdf = VipsImageOps.SdfCircle(20, 20, radius: 5);
        using var reg = new VipsRegion(sdf);
        reg.Prepare(new VipsRect(0, 0, 20, 20));
        // Image centre (10, 10) → distance to boundary = -5.
        Assert.Equal(-5f, ReadFloat(reg, 10, 10), 4);
        // (15, 10): 5 pixels right of centre → exactly on boundary.
        Assert.Equal(0f, ReadFloat(reg, 15, 10), 4);
        // Far corner: positive (outside).
        Assert.True(ReadFloat(reg, 0, 0) > 0);
    }

    [Fact]
    public void SdfBox_AxisAligned()
    {
        var sdf = VipsImageOps.SdfBox(20, 20, halfWidth: 4, halfHeight: 3);
        using var reg = new VipsRegion(sdf);
        reg.Prepare(new VipsRect(0, 0, 20, 20));
        // Centre of canvas → -min(halfW, halfH) = -3.
        Assert.Equal(-3f, ReadFloat(reg, 10, 10), 4);
        // (14, 10): 4 px right → exactly on right edge.
        Assert.Equal(0f, ReadFloat(reg, 14, 10), 4);
    }

    // ---- Invertlut ----

    [Fact]
    public void Invertlut_OfIdentity_IsIdentity()
    {
        var id = VipsImageOps.Identity();
        var inv = VipsImageOps.Invertlut(id);
        Assert.Equal(256, inv.Width);
        using var reg = new VipsRegion(inv);
        reg.Prepare(new VipsRect(0, 0, 256, 1));
        // Identity should round-trip closely (allow ±2 from the
        // crossing-search rounding).
        for (int x = 0; x < 256; x += 32)
            Assert.InRange(reg.GetAddress(x, 0)[0], (byte)Math.Max(0, x - 2), (byte)Math.Min(255, x + 2));
    }

    [Fact]
    public void Invertlut_OfHalfRamp_DoublesValues()
    {
        // Build a LUT mapping x → x/2 (compresses to 0..128).
        // Inverting it should expand 0..128 → 0..256.
        var compress = VipsImageOps.BuildLut(new double[,]
        {
            { 0, 0 }, { 255, 128 },
        });
        var expand = VipsImageOps.Invertlut(compress);
        using var reg = new VipsRegion(expand);
        reg.Prepare(new VipsRect(0, 0, expand.Width, 1));
        // At y=64 → expanded x ≈ 128 (since y = x/2 → x = 2y).
        int half = expand.Width / 2;
        Assert.InRange(reg.GetAddress(half, 0)[0], 250, 255);
    }
}
