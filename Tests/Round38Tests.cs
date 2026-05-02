using System;
using System.Buffers.Binary;
using Xunit;

namespace CosmoImage.Tests;

public class Round38Tests
{
    // ---- Black ----

    [Fact]
    public void Black_ProducesZeroImage()
    {
        var img = VipsImageOps.Black(4, 3, bands: 3);
        Assert.Equal(4, img.Width);
        Assert.Equal(3, img.Height);
        Assert.Equal(3, img.Bands);
        Assert.Equal(VipsBandFormat.UChar, img.BandFormat);

        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, 4, 3));
        for (int y = 0; y < 3; y++)
            for (int x = 0; x < 4; x++)
            {
                var addr = reg.GetAddress(x, y);
                Assert.Equal(0, addr[0]);
                Assert.Equal(0, addr[1]);
                Assert.Equal(0, addr[2]);
            }
    }

    [Fact]
    public void Black_FloatFormat()
    {
        var img = VipsImageOps.Black(2, 2, format: VipsBandFormat.Float);
        Assert.Equal(VipsBandFormat.Float, img.BandFormat);
    }

    // ---- Xyz ----

    [Fact]
    public void Xyz_PixelEqualsCoordinate()
    {
        var img = VipsImageOps.Xyz(8, 8);
        Assert.Equal(2, img.Bands);
        Assert.Equal(VipsBandFormat.UInt, img.BandFormat);

        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, 8, 8));
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                var addr = reg.GetAddress(x, y);
                uint xv = BinaryPrimitives.ReadUInt32LittleEndian(addr.Slice(0, 4));
                uint yv = BinaryPrimitives.ReadUInt32LittleEndian(addr.Slice(4, 4));
                Assert.Equal((uint)x, xv);
                Assert.Equal((uint)y, yv);
            }
    }

    // ---- Identity ----

    [Fact]
    public void Identity_DefaultUChar_IsRamp0to255()
    {
        var lut = VipsImageOps.Identity();
        Assert.Equal(256, lut.Width);
        Assert.Equal(1, lut.Height);
        Assert.Equal(1, lut.Bands);
        Assert.Equal(VipsBandFormat.UChar, lut.BandFormat);

        using var reg = new VipsRegion(lut);
        reg.Prepare(new VipsRect(0, 0, 256, 1));
        for (int x = 0; x < 256; x++) Assert.Equal(x, reg.GetAddress(x, 0)[0]);
    }

    [Fact]
    public void Identity_UShort_GoesTo65535()
    {
        var lut = VipsImageOps.Identity(ushort_: true);
        Assert.Equal(65536, lut.Width);
        Assert.Equal(VipsBandFormat.UShort, lut.BandFormat);
        using var reg = new VipsRegion(lut);
        reg.Prepare(new VipsRect(0, 0, 65536, 1));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(reg.GetAddress(0, 0).Slice(0, 2)));
        Assert.Equal(65535, BinaryPrimitives.ReadUInt16LittleEndian(reg.GetAddress(65535, 0).Slice(0, 2)));
        Assert.Equal(1234, BinaryPrimitives.ReadUInt16LittleEndian(reg.GetAddress(1234, 0).Slice(0, 2)));
    }

    // ---- BuildLut ----

    [Fact]
    public void BuildLut_LinearInterpolation()
    {
        // Two anchors: (0, 0) → (10, 100). Mid x = 5 should give y = 50.
        var lut = VipsImageOps.BuildLut(new double[,] { { 0, 0 }, { 10, 100 } });
        Assert.Equal(11, lut.Width);
        using var reg = new VipsRegion(lut);
        reg.Prepare(new VipsRect(0, 0, 11, 1));
        Assert.Equal(0, reg.GetAddress(0, 0)[0]);
        Assert.Equal(50, reg.GetAddress(5, 0)[0]);
        Assert.Equal(100, reg.GetAddress(10, 0)[0]);
    }

    [Fact]
    public void BuildLut_MultiSegment_SCurve()
    {
        // Classic S-curve: (0, 0), (64, 30), (192, 220), (255, 255).
        var lut = VipsImageOps.BuildLut(new double[,]
        {
            { 0, 0 }, { 64, 30 }, { 192, 220 }, { 255, 255 },
        });
        Assert.Equal(256, lut.Width);
        using var reg = new VipsRegion(lut);
        reg.Prepare(new VipsRect(0, 0, 256, 1));
        Assert.Equal(30, reg.GetAddress(64, 0)[0]);
        Assert.Equal(220, reg.GetAddress(192, 0)[0]);
        // At x=128 (mid of (64..192)): y = 30 + (128-64)/(192-64) * (220-30) = 30 + 95 = 125.
        Assert.InRange(reg.GetAddress(128, 0)[0], 124, 126);
    }

    [Fact]
    public void BuildLut_MultiBand_OutputsPerBand()
    {
        // Two y-values per anchor → 2-band LUT.
        var lut = VipsImageOps.BuildLut(new double[,]
        {
            { 0, 0, 100 }, { 10, 100, 0 },
        });
        Assert.Equal(2, lut.Bands);
        using var reg = new VipsRegion(lut);
        reg.Prepare(new VipsRect(0, 0, 11, 1));
        var p = reg.GetAddress(5, 0);
        Assert.Equal(50, p[0]);
        Assert.Equal(50, p[1]); // band 1: 100 → 0, mid = 50.
    }

    // ---- Gaussmat ----

    [Fact]
    public void Gaussmat_PeakAtCenter()
    {
        var k = VipsImageOps.Gaussmat(sigma: 1.0, minAmpl: 0.1);
        Assert.Equal(VipsBandFormat.Float, k.BandFormat);
        Assert.Equal(VipsInterpretation.Matrix, k.Interpretation);

        using var reg = new VipsRegion(k);
        reg.Prepare(new VipsRect(0, 0, k.Width, k.Height));
        int cx = k.Width / 2, cy = k.Height / 2;
        float peak = BinaryPrimitives.ReadSingleLittleEndian(reg.GetAddress(cx, cy).Slice(0, 4));
        float corner = BinaryPrimitives.ReadSingleLittleEndian(reg.GetAddress(0, 0).Slice(0, 4));
        Assert.Equal(1.0f, peak, 5);
        Assert.True(corner < peak);
    }

    [Fact]
    public void Gaussmat_Separable_IsOneRowTall()
    {
        var k = VipsImageOps.Gaussmat(sigma: 1.5, separable: true);
        Assert.Equal(1, k.Height);
        Assert.True(k.Width > 1);
    }

    // ---- Sines ----

    [Fact]
    public void Sines_RangeIsWithinPlusMinusOne()
    {
        var img = VipsImageOps.Sines(16, 16, hFreq: 1.0, vFreq: 0);
        Assert.Equal(VipsBandFormat.Float, img.BandFormat);
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, 16, 16));
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                float v = BinaryPrimitives.ReadSingleLittleEndian(reg.GetAddress(x, y).Slice(0, 4));
                Assert.InRange(v, -1.001f, 1.001f);
            }
    }

    [Fact]
    public void Sines_HorizontalFrequency_IsConstantAlongY()
    {
        var img = VipsImageOps.Sines(16, 16, hFreq: 1.0, vFreq: 0);
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, 16, 16));
        // Pure horizontal pattern: column x has same value across all rows.
        float v0 = BinaryPrimitives.ReadSingleLittleEndian(reg.GetAddress(8, 0).Slice(0, 4));
        float v1 = BinaryPrimitives.ReadSingleLittleEndian(reg.GetAddress(8, 8).Slice(0, 4));
        Assert.Equal(v0, v1, 5);
    }
}
