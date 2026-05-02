using System;
using System.Buffers.Binary;
using Xunit;

namespace CosmoImage.Tests;

public class Round32Tests
{
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

    private static VipsImage UCharFn(int w, int h, Func<int, int, byte> fn)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                        addr[x] = fn(reg.Valid.Left + x, reg.Valid.Top + y);
                }
                return 0;
            }
        };

    private static VipsImage FloatFn(int w, int h, Func<int, int, float> fn)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 1, BandFormat = VipsBandFormat.Float,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                        BinaryPrimitives.WriteSingleLittleEndian(addr.Slice(x * 4, 4),
                            fn(reg.Valid.Left + x, reg.Valid.Top + y));
                }
                return 0;
            }
        };

    // ---- Spcor ----

    [Fact]
    public void Spcor_FindsTemplateAtItsLocation()
    {
        // Build a 16×16 image with a 3×3 plus-sign at (8, 8). Use the
        // plus-sign as the template; correlation should peak there.
        var src = UCharFn(16, 16, (x, y) =>
        {
            int dx = x - 8, dy = y - 8;
            bool plus = (dx == 0 && Math.Abs(dy) <= 1) || (dy == 0 && Math.Abs(dx) <= 1);
            return (byte)(plus ? 200 : 50);
        });
        var tpl = UCharFn(3, 3, (x, y) =>
        {
            int dx = x - 1, dy = y - 1;
            bool plus = (dx == 0 && Math.Abs(dy) <= 1) || (dy == 0 && Math.Abs(dx) <= 1);
            return (byte)(plus ? 200 : 50);
        });
        var corr = src.Spcor(tpl);

        // Output is (16-3+1)×(16-3+1) = 14×14. Peak should be at (7, 7)
        // (template top-left when its centre lands on (8, 8)).
        Assert.Equal(14, corr.Width);
        using var reg = new VipsRegion(corr);
        reg.Prepare(new VipsRect(0, 0, 14, 14));
        // Find argmax.
        int bestX = 0, bestY = 0; byte bestV = 0;
        for (int y = 0; y < 14; y++)
            for (int x = 0; x < 14; x++)
            {
                byte v = reg.GetAddress(x, y)[0];
                if (v > bestV) { bestV = v; bestX = x; bestY = y; }
            }
        Assert.Equal(7, bestX);
        Assert.Equal(7, bestY);
        Assert.Equal(255, bestV); // exact match → r = 1 → mapped to 255.
    }

    // ---- Countlines ----

    [Fact]
    public void Countlines_AlternatingPattern_HasExpectedRate()
    {
        // 10-wide row with strict 0/255 alternation → 9 transitions per row.
        var src = UCharFn(10, 4, (x, y) => (byte)((x & 1) == 0 ? 0 : 255));
        double n = src.Countlines();
        Assert.Equal(9.0, n, 1);
    }

    [Fact]
    public void Countlines_FlatImage_IsZero()
    {
        var src = Solid(8, 8, 200);
        Assert.Equal(0, src.Countlines());
    }

    // ---- Stdif ----

    [Fact]
    public void Stdif_FlatRegion_ProducesTargetMean()
    {
        // Constant input → variance is zero, scaling collapses, output = meanTarget.
        var src = Solid(16, 16, 100);
        var stretched = src.Stdif(windowWidth: 5, windowHeight: 5,
            sigmaTarget: 50, meanTarget: 128);
        using var reg = new VipsRegion(stretched);
        reg.Prepare(new VipsRect(0, 0, 16, 16));
        Assert.Equal(128, reg.GetAddress(8, 8)[0]);
    }

    [Fact]
    public void Stdif_StretchesLowContrast()
    {
        // Tight gradient near 100 — local stddev is small, so scaling
        // amplifies. Output should span a wider range than input.
        var src = UCharFn(16, 16, (x, y) => (byte)(100 + (x & 1) * 4));
        var stretched = src.Stdif(windowWidth: 5, windowHeight: 5,
            sigmaTarget: 60, meanTarget: 128, a: 1.0);
        using var reg = new VipsRegion(stretched);
        reg.Prepare(new VipsRect(0, 0, 16, 16));
        byte hi = 0, lo = 255;
        for (int y = 5; y < 11; y++)
            for (int x = 5; x < 11; x++)
            {
                byte v = reg.GetAddress(x, y)[0];
                if (v > hi) hi = v;
                if (v < lo) lo = v;
            }
        // Input span was 4; stretched span should be much wider.
        Assert.True(hi - lo > 50, $"expected stretch, got span {hi - lo}");
    }

    // ---- Freqmult ----

    [Fact]
    public void Freqmult_AllOnesMask_IsRoundTrip()
    {
        // A unit-mask should leave the input ~unchanged after FFT/IFFT.
        var src = FloatFn(8, 8, (x, y) => x + y * 8.0f);
        var ones = FloatFn(8, 8, (x, y) => 1.0f);
        var done = src.Freqmult(ones);
        Assert.Equal(VipsBandFormat.Float, done.BandFormat);
        using var rs = new VipsRegion(src);
        using var rd = new VipsRegion(done);
        rs.Prepare(new VipsRect(0, 0, 8, 8));
        rd.Prepare(new VipsRect(0, 0, 8, 8));
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                float a = BinaryPrimitives.ReadSingleLittleEndian(rs.GetAddress(x, y).Slice(0, 4));
                float b = BinaryPrimitives.ReadSingleLittleEndian(rd.GetAddress(x, y).Slice(0, 4));
                Assert.True(MathF.Abs(a - b) < 0.01f, $"(x,y)=({x},{y}) src={a} round-trip={b}");
            }
    }

    [Fact]
    public void Freqmult_ZeroMaskGivesZero()
    {
        var src = FloatFn(8, 8, (x, y) => 100.0f);
        var zero = FloatFn(8, 8, (x, y) => 0.0f);
        var killed = src.Freqmult(zero);
        using var rd = new VipsRegion(killed);
        rd.Prepare(new VipsRect(0, 0, 8, 8));
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                float v = BinaryPrimitives.ReadSingleLittleEndian(rd.GetAddress(x, y).Slice(0, 4));
                Assert.True(MathF.Abs(v) < 1e-3f, $"expected ~0 at ({x},{y}), got {v}");
            }
    }

    // ---- Switch ----

    [Fact]
    public void Switch_PicksFirstNonzeroTest()
    {
        var t0 = UCharFn(4, 4, (x, y) => (byte)(x < 2 ? 1 : 0));
        var t1 = UCharFn(4, 4, (x, y) => (byte)(y < 2 ? 1 : 0));
        var t2 = Solid(4, 4, 1);
        var sel = VipsImageOps.Switch(t0, t1, t2);
        using var reg = new VipsRegion(sel);
        reg.Prepare(new VipsRect(0, 0, 4, 4));
        Assert.Equal(0, reg.GetAddress(0, 0)[0]); // t0 fires first
        Assert.Equal(1, reg.GetAddress(3, 0)[0]); // t0 false, t1 true → 1
        Assert.Equal(2, reg.GetAddress(3, 3)[0]); // only t2 true → 2
    }

    [Fact]
    public void Switch_OutputsNWhenAllFalse()
    {
        var t0 = Solid(4, 4, 0);
        var t1 = Solid(4, 4, 0);
        var sel = VipsImageOps.Switch(t0, t1);
        using var reg = new VipsRegion(sel);
        reg.Prepare(new VipsRect(0, 0, 4, 4));
        Assert.Equal(2, reg.GetAddress(0, 0)[0]);
    }

    // ---- Case ----

    [Fact]
    public void Case_PicksIndexedSource()
    {
        var idx = UCharFn(4, 4, (x, y) => (byte)(x % 3));
        var c0 = Solid(4, 4, 10);
        var c1 = Solid(4, 4, 20);
        var c2 = Solid(4, 4, 30);
        var picked = VipsImageOps.Case(idx, c0, c1, c2);
        using var reg = new VipsRegion(picked);
        reg.Prepare(new VipsRect(0, 0, 4, 4));
        Assert.Equal(10, reg.GetAddress(0, 0)[0]); // idx 0
        Assert.Equal(20, reg.GetAddress(1, 0)[0]); // idx 1
        Assert.Equal(30, reg.GetAddress(2, 0)[0]); // idx 2
    }

    [Fact]
    public void Case_OutOfRangeIndex_FallsBackToLastSource()
    {
        var idx = Solid(2, 2, 99);
        var c0 = Solid(2, 2, 10);
        var c1 = Solid(2, 2, 20);
        var picked = VipsImageOps.Case(idx, c0, c1);
        using var reg = new VipsRegion(picked);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        Assert.Equal(20, reg.GetAddress(0, 0)[0]);
    }
}
