using System;
using System.Buffers.Binary;
using Xunit;

namespace CosmoImage.Tests;

public class Round40Tests
{
    private static float ReadFloat(VipsRegion reg, int x, int y)
        => BinaryPrimitives.ReadSingleLittleEndian(reg.GetAddress(x, y).Slice(0, 4));

    // ---- Eye ----

    [Fact]
    public void Eye_TopRow_HasFullAmplitude_BottomRowIsZero()
    {
        var img = VipsImageOps.Eye(64, 32);
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, 64, 32));
        // Top row (y=0) → amp = 1; bottom row (y=H-1) → amp = 0.
        Assert.Equal(0f, ReadFloat(reg, 30, 31), 4);
        // Top row at x=0 → cos(0) = 1.
        Assert.Equal(1f, ReadFloat(reg, 0, 0), 4);
    }

    [Fact]
    public void Eye_FrequencyRisesLeftToRight()
    {
        var img = VipsImageOps.Eye(128, 16, factor: 1.0);
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, 128, 16));
        // Count zero-crossings in the left half vs right half on the top row.
        int zcLeft = 0, zcRight = 0;
        float prev = ReadFloat(reg, 0, 0);
        for (int x = 1; x < 64; x++)
        {
            float v = ReadFloat(reg, x, 0);
            if (Math.Sign(prev) != Math.Sign(v)) zcLeft++;
            prev = v;
        }
        prev = ReadFloat(reg, 64, 0);
        for (int x = 65; x < 128; x++)
        {
            float v = ReadFloat(reg, x, 0);
            if (Math.Sign(prev) != Math.Sign(v)) zcRight++;
            prev = v;
        }
        Assert.True(zcRight > zcLeft, $"right ({zcRight}) should out-cross left ({zcLeft})");
    }

    // ---- Zone ----

    [Fact]
    public void Zone_CentrePixel_IsOne()
    {
        // r² = 0 at the centre → cos(0) = 1.
        var img = VipsImageOps.Zone(32, 32);
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, 32, 32));
        Assert.Equal(1f, ReadFloat(reg, 16, 16), 4);
    }

    [Fact]
    public void Zone_OutputBounded()
    {
        var img = VipsImageOps.Zone(32, 32);
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, 32, 32));
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
                Assert.InRange(ReadFloat(reg, x, y), -1.001f, 1.001f);
    }

    // ---- Tonelut ----

    [Fact]
    public void Tonelut_DefaultParams_IsIdentity()
    {
        // shadows = 0, midtones = 1, highlights = 0 → identity ramp.
        var lut = VipsImageOps.Tonelut();
        using var reg = new VipsRegion(lut);
        reg.Prepare(new VipsRect(0, 0, 256, 1));
        for (int x = 0; x < 256; x += 16)
            Assert.InRange(reg.GetAddress(x, 0)[0], (byte)Math.Max(0, x - 1), (byte)Math.Min(255, x + 1));
    }

    [Fact]
    public void Tonelut_LiftedShadows_RaisesBlackPoint()
    {
        // shadows = 0.2 → input 0 maps to ~51 (0.2*255).
        var lut = VipsImageOps.Tonelut(shadows: 0.2);
        using var reg = new VipsRegion(lut);
        reg.Prepare(new VipsRect(0, 0, 256, 1));
        Assert.InRange(reg.GetAddress(0, 0)[0], 49, 53);
        Assert.Equal(255, reg.GetAddress(255, 0)[0]);
    }

    [Fact]
    public void Tonelut_CompressedHighlights_LowersWhitePoint()
    {
        var lut = VipsImageOps.Tonelut(highlights: 0.2);
        using var reg = new VipsRegion(lut);
        reg.Prepare(new VipsRect(0, 0, 256, 1));
        Assert.Equal(0, reg.GetAddress(0, 0)[0]);
        Assert.InRange(reg.GetAddress(255, 0)[0], 200, 206);
    }

    // ---- MaskIdealLowpass / Highpass ----

    [Fact]
    public void MaskIdealLowpass_OneInDisc_ZeroOutside()
    {
        var mask = VipsImageOps.MaskIdealLowpass(32, 32, frequencyCutoff: 0.25);
        using var reg = new VipsRegion(mask);
        reg.Prepare(new VipsRect(0, 0, 32, 32));
        // Centre is inside any disc → 1.
        Assert.Equal(1f, ReadFloat(reg, 16, 16), 4);
        // Far corner is outside → 0.
        Assert.Equal(0f, ReadFloat(reg, 0, 0), 4);
    }

    [Fact]
    public void MaskIdealHighpass_IsComplement()
    {
        var lo = VipsImageOps.MaskIdealLowpass(16, 16, frequencyCutoff: 0.5);
        var hi = VipsImageOps.MaskIdealHighpass(16, 16, frequencyCutoff: 0.5);
        using var rl = new VipsRegion(lo);
        using var rh = new VipsRegion(hi);
        rl.Prepare(new VipsRect(0, 0, 16, 16));
        rh.Prepare(new VipsRect(0, 0, 16, 16));
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                Assert.Equal(1f, ReadFloat(rl, x, y) + ReadFloat(rh, x, y), 4);
    }

    // ---- Fractsurf ----

    [Fact]
    public void Fractsurf_ProducesPlausibleRange()
    {
        var img = VipsImageOps.Fractsurf(64, 64, octaves: 4, baseCellSize: 32, seed: 1);
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, 64, 64));
        float min = float.MaxValue, max = float.MinValue;
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                float v = ReadFloat(reg, x, y);
                if (v < min) min = v;
                if (v > max) max = v;
            }
        // Sum of bounded-amplitude Perlin octaves stays well within ±2.
        Assert.True(max - min > 0.1, "fractsurf should have visible variation");
        Assert.InRange(min, -3f, 3f);
        Assert.InRange(max, -3f, 3f);
    }

    [Fact]
    public void Fractsurf_DeterministicForSameSeed()
    {
        var a = VipsImageOps.Fractsurf(16, 16, octaves: 3, seed: 42);
        var b = VipsImageOps.Fractsurf(16, 16, octaves: 3, seed: 42);
        using var ra = new VipsRegion(a);
        using var rb = new VipsRegion(b);
        ra.Prepare(new VipsRect(0, 0, 16, 16));
        rb.Prepare(new VipsRect(0, 0, 16, 16));
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                Assert.Equal(ReadFloat(ra, x, y), ReadFloat(rb, x, y));
    }
}
