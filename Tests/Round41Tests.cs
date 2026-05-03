using System;
using System.Buffers.Binary;
using Xunit;

namespace CosmoImage.Tests;

public class Round41Tests
{
    private static float ReadFloat(VipsRegion reg, int x, int y)
        => BinaryPrimitives.ReadSingleLittleEndian(reg.GetAddress(x, y).Slice(0, 4));

    // ---- MaskGaussian ----

    [Fact]
    public void MaskGaussianLowpass_PeaksAtCentre_FallsOff()
    {
        var m = VipsImageOps.MaskGaussianLowpass(32, 32, frequencyCutoff: 0.25);
        using var reg = new VipsRegion(m);
        reg.Prepare(new VipsRect(0, 0, 32, 32));
        Assert.Equal(1f, ReadFloat(reg, 16, 16), 4);
        // Far corner should be small but non-zero (Gaussian, not ideal).
        float corner = ReadFloat(reg, 0, 0);
        Assert.True(corner < 0.1f && corner >= 0);
    }

    [Fact]
    public void MaskGaussianHighpass_IsComplementOfLowpass()
    {
        var lo = VipsImageOps.MaskGaussianLowpass(16, 16, frequencyCutoff: 0.5);
        var hi = VipsImageOps.MaskGaussianHighpass(16, 16, frequencyCutoff: 0.5);
        using var rl = new VipsRegion(lo);
        using var rh = new VipsRegion(hi);
        rl.Prepare(new VipsRect(0, 0, 16, 16));
        rh.Prepare(new VipsRect(0, 0, 16, 16));
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                Assert.Equal(1f, ReadFloat(rl, x, y) + ReadFloat(rh, x, y), 4);
    }

    [Fact]
    public void MaskGaussianRing_PeakAtRingRadius()
    {
        var m = VipsImageOps.MaskGaussianRing(64, 64,
            frequencyCutoff: 0.5, ringWidth: 0.05);
        using var reg = new VipsRegion(m);
        reg.Prepare(new VipsRect(0, 0, 64, 64));
        // Centre is far below 1, ring radius (16 px from centre) ≈ 1.
        float centre = ReadFloat(reg, 32, 32);
        float onRing = ReadFloat(reg, 48, 32); // 16 px right of centre
        Assert.True(onRing > centre, $"ring ({onRing}) should exceed centre ({centre})");
        Assert.InRange(onRing, 0.95f, 1.001f);
    }

    // ---- MaskButterworth ----

    [Fact]
    public void MaskButterworthLowpass_HalfPowerAtCutoff()
    {
        // At d = cutoff: H = 1 / (1 + 1) = 0.5.
        var m = VipsImageOps.MaskButterworthLowpass(64, 64,
            frequencyCutoff: 0.25, order: 2);
        using var reg = new VipsRegion(m);
        reg.Prepare(new VipsRect(0, 0, 64, 64));
        // cutoff = 0.25 * 32 = 8 px from centre.
        Assert.InRange(ReadFloat(reg, 32 + 8, 32), 0.49f, 0.51f);
    }

    [Fact]
    public void MaskButterworthHigherOrder_SharperRolloff()
    {
        // Order 8 should give a much sharper cutoff than order 1.
        var soft = VipsImageOps.MaskButterworthLowpass(64, 64,
            frequencyCutoff: 0.25, order: 1);
        var sharp = VipsImageOps.MaskButterworthLowpass(64, 64,
            frequencyCutoff: 0.25, order: 8);
        using var rs = new VipsRegion(soft);
        using var rh = new VipsRegion(sharp);
        rs.Prepare(new VipsRect(0, 0, 64, 64));
        rh.Prepare(new VipsRect(0, 0, 64, 64));
        // Just past the cutoff (10 px right of centre), high-order should be lower.
        float softVal = ReadFloat(rs, 32 + 10, 32);
        float sharpVal = ReadFloat(rh, 32 + 10, 32);
        Assert.True(sharpVal < softVal,
            $"sharp ({sharpVal}) should be < soft ({softVal}) past the cutoff");
    }

    [Fact]
    public void MaskButterworthHighpass_IsLowAtCentre()
    {
        var m = VipsImageOps.MaskButterworthHighpass(32, 32);
        using var reg = new VipsRegion(m);
        reg.Prepare(new VipsRect(0, 0, 32, 32));
        Assert.Equal(0f, ReadFloat(reg, 16, 16), 4);
        // Far corner (high freq) → close to 1.
        Assert.True(ReadFloat(reg, 0, 0) > 0.5f);
    }

    [Fact]
    public void MaskButterworthRing_PeakAtRingRadius()
    {
        var m = VipsImageOps.MaskButterworthRing(64, 64,
            frequencyCutoff: 0.5, ringWidth: 0.1, order: 2);
        using var reg = new VipsRegion(m);
        reg.Prepare(new VipsRect(0, 0, 64, 64));
        // ring radius = 0.5 * 32 = 16 px from centre.
        float centre = ReadFloat(reg, 32, 32);
        float onRing = ReadFloat(reg, 48, 32);
        Assert.True(onRing > centre);
    }

    // ---- MaskFractal ----

    [Fact]
    public void MaskFractal_DcBinIsZero()
    {
        var m = VipsImageOps.MaskFractal(32, 32, fractalDimension: 2.5);
        using var reg = new VipsRegion(m);
        reg.Prepare(new VipsRect(0, 0, 32, 32));
        Assert.Equal(0f, ReadFloat(reg, 16, 16), 4);
    }

    [Fact]
    public void MaskFractal_FallsOffWithDistance()
    {
        var m = VipsImageOps.MaskFractal(64, 64, fractalDimension: 2.0);
        using var reg = new VipsRegion(m);
        reg.Prepare(new VipsRect(0, 0, 64, 64));
        float near = ReadFloat(reg, 33, 32); // d = 1
        float far = ReadFloat(reg, 48, 32);  // d = 16
        Assert.True(near > far,
            $"closer-to-centre ({near}) should exceed farther ({far})");
    }
}
