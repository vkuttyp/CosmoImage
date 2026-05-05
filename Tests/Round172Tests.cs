using System;
using System.Buffers.Binary;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 172 — directional band-pass frequency masks. Each mask family
/// (Gaussian, Butterworth, Ideal) gets a Band variant with two symmetric
/// peaks at <c>(±frequencyX·W/2, ±frequencyY·H/2)</c> from the centre.
/// The two-peak structure preserves real-FFT conjugate symmetry, the
/// reason real-image FFT band-pass design uses paired peaks.
///
/// Tests pin: peak response equals 1 at the two symmetric points, far-
/// away response is near 0, and conjugate-symmetry holds across the
/// centre for any mask configuration.
/// </summary>
public class Round172Tests
{
    private static float ReadMaskValue(VipsImage mask, int x, int y)
    {
        using var reg = new VipsRegion(mask);
        reg.Prepare(new VipsRect(0, 0, mask.Width, mask.Height));
        return BinaryPrimitives.ReadSingleLittleEndian(reg.GetAddress(x, y).Slice(0, 4));
    }

    [Fact]
    public void GaussianBand_PeakAtTargetFrequency_IsOne()
    {
        // 64×64 mask with peak at (fx=0.5, fy=0). Peak position: (cx + W/4, cy)
        // = (32 + 16, 32) = (48, 32). Mirror peak at (32 - 16, 32) = (16, 32).
        var mask = VipsImageOps.MaskGaussianBand(64, 64, frequencyX: 0.5, frequencyY: 0.0, ringWidth: 0.05);
        Assert.Equal(1.0f, ReadMaskValue(mask, 48, 32), 1e-3);
        Assert.Equal(1.0f, ReadMaskValue(mask, 16, 32), 1e-3);
    }

    [Fact]
    public void GaussianBand_FarFromPeak_IsNearZero()
    {
        var mask = VipsImageOps.MaskGaussianBand(64, 64, frequencyX: 0.5, frequencyY: 0.0, ringWidth: 0.05);
        // Centre is far from both peaks (distance = W/4 = 16, σ = 0.05·32 ≈ 1.6).
        Assert.True(ReadMaskValue(mask, 32, 32) < 0.01f);
        // Corner — even further.
        Assert.True(ReadMaskValue(mask, 0, 0) < 0.01f);
    }

    [Fact]
    public void GaussianBand_ConjugateSymmetric()
    {
        // For mask M centred at (cx, cy), real-FFT compatibility requires
        // M(cx+dx, cy+dy) == M(cx-dx, cy-dy). The two-peak construction
        // guarantees this. Use even W so cx lands on a pixel index and
        // the (cx+dx, cy+dy) ↔ (cx-dx, cy-dy) reflection pair is exact.
        var mask = VipsImageOps.MaskGaussianBand(32, 32, frequencyX: 0.3, frequencyY: 0.6, ringWidth: 0.08);
        int cx = 32 / 2, cy = 32 / 2;
        for (int dy = -10; dy <= 10; dy += 5)
            for (int dx = -10; dx <= 10; dx += 5)
            {
                float a = ReadMaskValue(mask, cx + dx, cy + dy);
                float b = ReadMaskValue(mask, cx - dx, cy - dy);
                Assert.Equal(a, b, 1e-5);
            }
    }

    [Fact]
    public void ButterworthBand_PeakIsOne_FallsOffWithOrder()
    {
        var mask = VipsImageOps.MaskButterworthBand(64, 64, frequencyX: 0.5, frequencyY: 0.0,
            ringWidth: 0.05, order: 2);
        Assert.Equal(1.0f, ReadMaskValue(mask, 48, 32), 1e-3);
        Assert.Equal(1.0f, ReadMaskValue(mask, 16, 32), 1e-3);
        // ringWidth=0.05 → σ=1.6. At distance σ exactly, response = 0.5
        // (Butterworth definition). Past σ, monotonic decay. Pixel (50, 32)
        // is 2 px from peak — past σ — so response sits below 0.5.
        float farResp = ReadMaskValue(mask, 50, 32);
        Assert.InRange(farResp, 0.0f, 0.5f);
        // And much further away, near zero.
        Assert.True(ReadMaskValue(mask, 32, 32) < 0.05f);
    }

    [Fact]
    public void IdealBand_InsideDisc_OneOutside_Zero()
    {
        // Disc radius = 0.1·32 = 3.2 pixels around peak (48, 32).
        var mask = VipsImageOps.MaskIdealBand(64, 64, frequencyX: 0.5, frequencyY: 0.0, ringWidth: 0.1);
        Assert.Equal(1f, ReadMaskValue(mask, 48, 32));         // peak
        Assert.Equal(1f, ReadMaskValue(mask, 49, 32));         // 1 px from peak
        Assert.Equal(0f, ReadMaskValue(mask, 52, 32));         // 4 px from peak — outside
        Assert.Equal(0f, ReadMaskValue(mask, 32, 32));         // centre — far from both peaks
    }

    [Fact]
    public void IdealBand_RejectIsComplement()
    {
        var pass = VipsImageOps.MaskIdealBand(32, 32, frequencyX: 0.5, frequencyY: 0.0, ringWidth: 0.1, reject: false);
        var rej = VipsImageOps.MaskIdealBand(32, 32, frequencyX: 0.5, frequencyY: 0.0, ringWidth: 0.1, reject: true);
        for (int y = 0; y < 32; y += 4)
            for (int x = 0; x < 32; x += 4)
                Assert.Equal(1f, ReadMaskValue(pass, x, y) + ReadMaskValue(rej, x, y));
    }

    [Fact]
    public void GaussianBand_DiagonalDirection_PeaksOnDiagonal()
    {
        // (fx=0.5, fy=0.5) on a 64x64 image: peaks at (32+16, 32+16)=(48,48)
        // and (32-16, 32-16)=(16,16). Off-diagonal samples (e.g. 48, 16)
        // are far from both peaks — should be near zero.
        var mask = VipsImageOps.MaskGaussianBand(64, 64, frequencyX: 0.5, frequencyY: 0.5, ringWidth: 0.05);
        Assert.Equal(1.0f, ReadMaskValue(mask, 48, 48), 1e-3);
        Assert.Equal(1.0f, ReadMaskValue(mask, 16, 16), 1e-3);
        Assert.True(ReadMaskValue(mask, 48, 16) < 0.01f);
        Assert.True(ReadMaskValue(mask, 16, 48) < 0.01f);
    }
}
