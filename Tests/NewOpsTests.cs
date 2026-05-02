using System;
using CosmoImage.Operations.Analysis;
using CosmoImage.Operations.Convolution;
using CosmoImage.Operations.Misc;
using Xunit;

namespace CosmoImage.Tests;

public class NewOpsTests
{
    private static VipsImage Uniform(int w, int h, byte value, int bands = 1)
    {
        return new VipsImage
        {
            Width = w, Height = h, Bands = bands, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int i = 0; i < reg.Valid.Width * bands; i++) addr[i] = value;
                }
                return 0;
            }
        };
    }

    private static VipsImage Ramp(int w, int h)
    {
        return new VipsImage
        {
            Width = w, Height = h, Bands = 1, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                        addr[x] = (byte)((reg.Valid.Left + x + reg.Valid.Top + y) & 0xff);
                }
                return 0;
            }
        };
    }

    [Fact]
    public void Sqrt_Maps255To255_AndZeroToZero()
    {
        var img = Ramp(256, 1);
        var res = img.Sqrt();
        using var reg = new VipsRegion(res);
        reg.Prepare(new VipsRect(0, 0, 256, 1));
        Assert.Equal(0, reg.GetAddress(0, 0)[0]);
        Assert.Equal(255, reg.GetAddress(255, 0)[0]);
        // sqrt(0.5) * 255 ≈ 180
        Assert.InRange(reg.GetAddress(128, 0)[0], 178, 182);
    }

    [Fact]
    public void Pow_HalfExponent_EqualsSqrt()
    {
        var img = Uniform(2, 2, 64);
        var sq = img.Sqrt();
        var pw = img.Pow(0.5);
        using var rs = new VipsRegion(sq);
        using var rp = new VipsRegion(pw);
        rs.Prepare(new VipsRect(0, 0, 2, 2));
        rp.Prepare(new VipsRect(0, 0, 2, 2));
        Assert.Equal(rs.GetAddress(0, 0)[0], rp.GetAddress(0, 0)[0]);
    }

    [Fact]
    public void Abs_OnUChar_IsIdentity()
    {
        var img = Uniform(2, 2, 100);
        var res = img.Abs();
        using var reg = new VipsRegion(res);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        Assert.Equal(100, reg.GetAddress(0, 0)[0]);
    }

    [Fact]
    public void RelationalConst_GreaterThanThreshold_Produces255Or0Mask()
    {
        var img = Ramp(256, 1);
        var mask = img.MoreConst(127);
        using var reg = new VipsRegion(mask);
        reg.Prepare(new VipsRect(0, 0, 256, 1));
        Assert.Equal(0, reg.GetAddress(127, 0)[0]);
        Assert.Equal(255, reg.GetAddress(128, 0)[0]);
        Assert.Equal(255, reg.GetAddress(255, 0)[0]);
    }

    [Fact]
    public void BooleanConst_AndWith0xF0_KeepsHighNibble()
    {
        var img = Uniform(1, 1, 0xAB);
        var res = img.AndConst(0xF0);
        using var reg = new VipsRegion(res);
        reg.Prepare(new VipsRect(0, 0, 1, 1));
        Assert.Equal(0xA0, reg.GetAddress(0, 0)[0]);
    }

    [Fact]
    public void Boolean2_XorOfImageWithItself_IsZero()
    {
        var a = Ramp(8, 8);
        var b = Ramp(8, 8);
        var x = a.Xor(b);
        using var reg = new VipsRegion(x);
        reg.Prepare(new VipsRect(0, 0, 8, 8));
        for (int y = 0; y < 8; y++)
            for (int xi = 0; xi < 8; xi++)
                Assert.Equal(0, reg.GetAddress(xi, y)[0]);
    }

    [Fact]
    public void Stats_OfUniformImage_HasZeroDeviateAndCorrectAvg()
    {
        var img = Uniform(10, 10, 42, bands: 3);
        var stats = img.Stats();
        Assert.Equal(42, stats.Avg[0], 1);
        Assert.Equal(42, stats.Avg[3], 1); // aggregate
        Assert.Equal(0, stats.Deviate[0], 6);
        Assert.Equal(42, stats.Min[0]);
        Assert.Equal(42, stats.Max[0]);
    }

    [Fact]
    public void Avg_OfRamp_IsRoughlyHalf()
    {
        var img = Ramp(16, 16); // values 0..30
        double avg = img.Avg();
        Assert.InRange(avg, 14.0, 16.0);
    }

    [Fact]
    public void Median_OfImpulseNoise_RemovesSpike()
    {
        // 5x5 uniform 100, single bright spike at center
        var img = new VipsImage
        {
            Width = 5, Height = 5, Bands = 1, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        int gx = reg.Valid.Left + x;
                        int gy = reg.Valid.Top + y;
                        addr[x] = (byte)(gx == 2 && gy == 2 ? 255 : 100);
                    }
                }
                return 0;
            }
        };
        var med = img.Median(3);
        using var reg = new VipsRegion(med);
        reg.Prepare(new VipsRect(0, 0, 5, 5));
        // Median of a 3x3 window with eight 100s and one 255 is 100.
        Assert.Equal(100, reg.GetAddress(2, 2)[0]);
    }

    [Fact]
    public void Open_ThenClose_OnUniformImage_IsIdentity()
    {
        var img = Uniform(8, 8, 80);
        double[,] mask = { { 1, 1, 1 }, { 1, 1, 1 }, { 1, 1, 1 } };
        var opened = img.Open(mask).Close(mask);
        using var reg = new VipsRegion(opened);
        reg.Prepare(new VipsRect(2, 2, 4, 4));
        Assert.Equal(80, reg.GetAddress(4, 4)[0]);
    }

    [Fact]
    public void Mutate_BlockScopedChain_IsEquivalentToFluent()
    {
        var img = Uniform(4, 4, 100);
        var direct = img.Linear(new[] { 2.0 }, new[] { 0.0 });
        var mut = img.Mutate(im => im.Linear(new[] { 2.0 }, new[] { 0.0 }));
        using var rd = new VipsRegion(direct);
        using var rm = new VipsRegion(mut);
        rd.Prepare(new VipsRect(0, 0, 4, 4));
        rm.Prepare(new VipsRect(0, 0, 4, 4));
        Assert.Equal(rd.GetAddress(0, 0)[0], rm.GetAddress(0, 0)[0]);
    }

    [Fact]
    public void FwFft_Then_InvFft_RoundTripsApproximately()
    {
        var img = Uniform(8, 8, 50);
        var spectrum = img.FwFft();
        var back = spectrum.InvFft();
        using var reg = new VipsRegion(back);
        reg.Prepare(new VipsRect(0, 0, 8, 8));
        // Uniform input: only DC component non-zero. Inverse magnitude should
        // recover the original constant within rounding.
        Assert.InRange(reg.GetAddress(4, 4)[0], 48, 52);
    }

    [Fact]
    public void Spectrum_OfUniformImage_HasBrightCenter()
    {
        var img = Uniform(8, 8, 200);
        var spec = img.FwFft().Spectrum();
        using var reg = new VipsRegion(spec);
        reg.Prepare(new VipsRect(0, 0, 8, 8));
        // DC is centered at (W/2, H/2) by FFT-shift: should be max (255).
        Assert.Equal(255, reg.GetAddress(4, 4)[0]);
        // Non-DC bins are zero for a perfectly uniform input.
        Assert.Equal(0, reg.GetAddress(0, 0)[0]);
    }
}
