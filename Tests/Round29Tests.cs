using System;
using System.Buffers.Binary;
using CosmoImage.Operations.Misc;
using Xunit;

namespace CosmoImage.Tests;

public class Round29Tests
{
    /// <summary>Single-band gradient left-to-right; pixel (x, y) = x.</summary>
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
                        addr[x] = (byte)(reg.Valid.Left + x);
                }
                return 0;
            }
        };

    /// <summary>3-band image with R = x, G = y, B = constant.</summary>
    private static VipsImage RgbXy(int w, int h, byte b)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 3, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? bb, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        addr[x * 3 + 0] = (byte)(reg.Valid.Left + x);
                        addr[x * 3 + 1] = (byte)(reg.Valid.Top + y);
                        addr[x * 3 + 2] = b;
                    }
                }
                return 0;
            }
        };

    private static VipsImage FloatImage(int w, int h, Func<int, int, float> fn)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 1, BandFormat = VipsBandFormat.Float,
            Interpretation = VipsInterpretation.BW,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        int gx = reg.Valid.Left + x;
                        int gy = reg.Valid.Top + y;
                        BinaryPrimitives.WriteSingleLittleEndian(addr.Slice(x * 4, 4), fn(gx, gy));
                    }
                }
                return 0;
            }
        };

    // ---- Bandfold / Bandunfold ----

    [Fact]
    public void Bandfold_FoldsRowOntoBands()
    {
        // 6×1 1-band → 2×1 3-band. Row [0,1,2,3,4,5] becomes pels (0,1,2) and (3,4,5).
        var src = RampX(6, 1);
        var fold = src.Bandfold(factor: 3);
        Assert.Equal(2, fold.Width);
        Assert.Equal(3, fold.Bands);

        using var reg = new VipsRegion(fold);
        reg.Prepare(new VipsRect(0, 0, 2, 1));
        var p = reg.GetAddress(0, 0);
        Assert.Equal(0, p[0]); Assert.Equal(1, p[1]); Assert.Equal(2, p[2]);
        Assert.Equal(3, p[3]); Assert.Equal(4, p[4]); Assert.Equal(5, p[5]);
    }

    [Fact]
    public void Bandunfold_RoundTripsBandfold()
    {
        var src = RampX(12, 2);
        var fold = src.Bandfold(factor: 4);   // (3, 2, 4)
        var back = fold.Bandunfold();         // (12, 2, 1)
        Assert.Equal(12, back.Width);
        Assert.Equal(1, back.Bands);

        using var reg = new VipsRegion(back);
        reg.Prepare(new VipsRect(0, 0, 12, 2));
        for (int x = 0; x < 12; x++)
            Assert.Equal((byte)x, reg.GetAddress(x, 0)[0]);
    }

    [Fact]
    public void Bandfold_RejectsNonDivisibleWidth()
    {
        var src = RampX(7, 1);
        Assert.Throws<Exception>(() => src.Bandfold(factor: 3));
    }

    // ---- BandjoinConst ----

    [Fact]
    public void BandjoinConst_AppendsConstantsAsBands()
    {
        var src = RgbXy(2, 2, b: 50);
        var rgba = src.BandjoinConst(255.0);
        Assert.Equal(4, rgba.Bands);

        using var reg = new VipsRegion(rgba);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        var p = reg.GetAddress(1, 1);
        Assert.Equal(1, p[0]);   // R = x
        Assert.Equal(1, p[1]);   // G = y
        Assert.Equal(50, p[2]);  // B = const
        Assert.Equal(255, p[3]); // appended alpha
    }

    [Fact]
    public void BandjoinConst_AppendsMultiple()
    {
        var src = RampX(4, 1); // 1-band
        var fat = src.BandjoinConst(100.0, 200.0);
        Assert.Equal(3, fat.Bands);

        using var reg = new VipsRegion(fat);
        reg.Prepare(new VipsRect(0, 0, 4, 1));
        var p = reg.GetAddress(2, 0);
        Assert.Equal(2, p[0]);
        Assert.Equal(100, p[1]);
        Assert.Equal(200, p[2]);
    }

    // ---- Wrap ----

    [Fact]
    public void Wrap_DefaultOffsetCentresImage()
    {
        // 4-pixel ramp: [0,1,2,3]. Default wrap dx=2 (W/2) gives [2,3,0,1].
        var src = RampX(4, 1);
        var w = src.Wrap();
        Assert.Equal(4, w.Width);

        using var reg = new VipsRegion(w);
        reg.Prepare(new VipsRect(0, 0, 4, 1));
        Assert.Equal(2, reg.GetAddress(0, 0)[0]);
        Assert.Equal(3, reg.GetAddress(1, 0)[0]);
        Assert.Equal(0, reg.GetAddress(2, 0)[0]);
        Assert.Equal(1, reg.GetAddress(3, 0)[0]);
    }

    [Fact]
    public void Wrap_ExplicitOffsetShifts()
    {
        var src = RampX(5, 1);
        var w = src.Wrap(x: 1, y: 0);
        using var reg = new VipsRegion(w);
        reg.Prepare(new VipsRect(0, 0, 5, 1));
        // out[0] = src[(0+1)%5] = 1, out[4] = src[(4+1)%5] = 0.
        Assert.Equal(1, reg.GetAddress(0, 0)[0]);
        Assert.Equal(0, reg.GetAddress(4, 0)[0]);
    }

    [Fact]
    public void Wrap_NegativeOffsetReducesCorrectly()
    {
        var src = RampX(4, 1);
        var w = src.Wrap(x: -1, y: 0);
        using var reg = new VipsRegion(w);
        reg.Prepare(new VipsRect(0, 0, 4, 1));
        // -1 mod 4 = 3. out[0] = src[(0+3)%4] = 3.
        Assert.Equal(3, reg.GetAddress(0, 0)[0]);
        Assert.Equal(0, reg.GetAddress(1, 0)[0]);
    }

    // ---- Zoom ----

    [Fact]
    public void Zoom_ExpandsEachPixelToBlock()
    {
        // 2×2 ramp: pels (0,0)=0, (1,0)=1, (0,1)=0, (1,1)=1.
        var src = RampX(2, 2);
        var z = src.Zoom(2, 3);
        Assert.Equal(4, z.Width);
        Assert.Equal(6, z.Height);

        using var reg = new VipsRegion(z);
        reg.Prepare(new VipsRect(0, 0, 4, 6));
        // Block (0..1, 0..2) all read pel (0, 0) = 0.
        Assert.Equal(0, reg.GetAddress(0, 0)[0]);
        Assert.Equal(0, reg.GetAddress(1, 2)[0]);
        // Block (2..3, 0..2) reads pel (1, 0) = 1.
        Assert.Equal(1, reg.GetAddress(2, 0)[0]);
        Assert.Equal(1, reg.GetAddress(3, 2)[0]);
        // Block (0..1, 3..5) reads pel (0, 1) = 0.
        Assert.Equal(0, reg.GetAddress(0, 5)[0]);
        // Block (2..3, 3..5) reads pel (1, 1) = 1.
        Assert.Equal(1, reg.GetAddress(3, 5)[0]);
    }

    [Fact]
    public void Zoom_PreservesBandsAndFormat()
    {
        var src = RgbXy(3, 3, b: 100);
        var z = src.Zoom(2, 2);
        Assert.Equal(3, z.Bands);
        Assert.Equal(VipsBandFormat.UChar, z.BandFormat);
    }

    // ---- Scale ----

    [Fact]
    public void Scale_LinearStretch_FloatToUChar()
    {
        // Float gradient 0..1 over 5 pixels → after stretch: 0, 64, 128, 191, 255.
        var src = FloatImage(5, 1, (x, y) => x / 4.0f);
        var scaled = src.Scale();
        Assert.Equal(VipsBandFormat.UChar, scaled.BandFormat);

        using var reg = new VipsRegion(scaled);
        reg.Prepare(new VipsRect(0, 0, 5, 1));
        Assert.Equal(0, reg.GetAddress(0, 0)[0]);
        Assert.Equal(255, reg.GetAddress(4, 0)[0]);
        // Middle should be near 128.
        Assert.InRange(reg.GetAddress(2, 0)[0], 125, 130);
    }

    [Fact]
    public void Scale_DegenerateInput_DoesNotDivideByZero()
    {
        var src = FloatImage(4, 1, (x, y) => 7.0f); // constant
        var scaled = src.Scale();
        using var reg = new VipsRegion(scaled);
        reg.Prepare(new VipsRect(0, 0, 4, 1));
        // No range → all zero (libvips behaviour).
        Assert.Equal(0, reg.GetAddress(0, 0)[0]);
        Assert.Equal(0, reg.GetAddress(3, 0)[0]);
    }

    [Fact]
    public void Scale_LogMode_HandlesWideRange()
    {
        // Wide-range positive values; log scale should produce a smoother map.
        var src = FloatImage(5, 1, (x, y) => MathF.Pow(10f, x));
        var scaled = src.Scale(log: true);
        using var reg = new VipsRegion(scaled);
        reg.Prepare(new VipsRect(0, 0, 5, 1));
        // First pel (value 1) maps small, last (value 10000) maps to 255-ish.
        Assert.True(reg.GetAddress(0, 0)[0] < reg.GetAddress(4, 0)[0]);
        Assert.True(reg.GetAddress(4, 0)[0] > 200);
    }
}
