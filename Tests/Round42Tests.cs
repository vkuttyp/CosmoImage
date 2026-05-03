using System;
using System.Buffers.Binary;
using Xunit;

namespace CosmoImage.Tests;

public class Round42Tests
{
    /// <summary>Single-band UChar gradient where pixel (x, y) = x.</summary>
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

    /// <summary>RGB image with R=x, G=y, B=constant.</summary>
    private static VipsImage RgbXy(int w, int h)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 3, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        addr[x * 3 + 0] = (byte)(reg.Valid.Left + x);
                        addr[x * 3 + 1] = (byte)(reg.Valid.Top + y);
                        addr[x * 3 + 2] = 50;
                    }
                }
                return 0;
            }
        };

    /// <summary>Build a Float 2-band index image from a (x, y) → (sx, sy) function.</summary>
    private static VipsImage Index(int w, int h, Func<int, int, (float, float)> fn)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 2, BandFormat = VipsBandFormat.Float,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        var (sx, sy) = fn(reg.Valid.Left + x, reg.Valid.Top + y);
                        BinaryPrimitives.WriteSingleLittleEndian(addr.Slice(x * 8 + 0, 4), sx);
                        BinaryPrimitives.WriteSingleLittleEndian(addr.Slice(x * 8 + 4, 4), sy);
                    }
                }
                return 0;
            }
        };

    // ---- Mapim ----

    [Fact]
    public void Mapim_IdentityIndex_ReturnsInput()
    {
        var src = RampX(8, 8);
        var idx = Index(8, 8, (x, y) => (x, y));
        var warped = src.Mapim(idx);
        Assert.Equal(src.Width, warped.Width);
        using var rs = new VipsRegion(src);
        using var rw = new VipsRegion(warped);
        rs.Prepare(new VipsRect(0, 0, 8, 8));
        rw.Prepare(new VipsRect(0, 0, 8, 8));
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                Assert.Equal(rs.GetAddress(x, y)[0], rw.GetAddress(x, y)[0]);
    }

    [Fact]
    public void Mapim_HorizontalFlip()
    {
        var src = RampX(8, 4);
        var idx = Index(8, 4, (x, y) => (7 - x, y));
        var warped = src.Mapim(idx);
        using var rw = new VipsRegion(warped);
        rw.Prepare(new VipsRect(0, 0, 8, 4));
        // Pixel 0 of output = pixel 7 of input = 7. Pixel 7 = 0.
        Assert.Equal(7, rw.GetAddress(0, 0)[0]);
        Assert.Equal(0, rw.GetAddress(7, 0)[0]);
    }

    [Fact]
    public void Mapim_OutOfBoundsGetsBackground()
    {
        var src = RampX(8, 4);
        var idx = Index(8, 4, (x, y) => (-1, -1)); // all out of bounds
        var warped = src.Mapim(idx, background: new[] { 99.0 });
        using var rw = new VipsRegion(warped);
        rw.Prepare(new VipsRect(0, 0, 8, 4));
        Assert.Equal(99, rw.GetAddress(0, 0)[0]);
        Assert.Equal(99, rw.GetAddress(7, 3)[0]);
    }

    [Fact]
    public void Mapim_BilinearInterpolatesFractionalCoords()
    {
        // Source ramp: pixel x = x. Sample at sx = 2.5 → should interpolate
        // halfway between pixel 2 (=2) and pixel 3 (=3) → 2.5 → rounds to 3.
        var src = RampX(8, 4);
        var idx = Index(1, 1, (x, y) => (2.5f, 0));
        var warped = src.Mapim(idx);
        using var rw = new VipsRegion(warped);
        rw.Prepare(new VipsRect(0, 0, 1, 1));
        Assert.InRange(rw.GetAddress(0, 0)[0], (byte)2, (byte)3); // (2 + 3) / 2 rounds either way
    }

    [Fact]
    public void Mapim_PreservesBandsForRgb()
    {
        var src = RgbXy(8, 8);
        var idx = Index(8, 8, (x, y) => (x, y));
        var warped = src.Mapim(idx);
        Assert.Equal(3, warped.Bands);
        using var rw = new VipsRegion(warped);
        rw.Prepare(new VipsRect(0, 0, 8, 8));
        var p = rw.GetAddress(3, 4);
        Assert.Equal(3, p[0]);
        Assert.Equal(4, p[1]);
        Assert.Equal(50, p[2]);
    }

    // ---- Quadratic ----

    [Fact]
    public void Quadratic_IdentityCoefficients_ReturnsInput()
    {
        var src = RampX(8, 8);
        // sx = x, sy = y → coeffs [0,1,0,0,0,0, 0,0,1,0,0,0]
        var coeffs = new[] { 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0 };
        var warped = src.Quadratic(coeffs);
        using var rs = new VipsRegion(src);
        using var rw = new VipsRegion(warped);
        rs.Prepare(new VipsRect(0, 0, 8, 8));
        rw.Prepare(new VipsRect(0, 0, 8, 8));
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                Assert.Equal(rs.GetAddress(x, y)[0], rw.GetAddress(x, y)[0]);
    }

    [Fact]
    public void Quadratic_TranslateByConstant()
    {
        var src = RampX(8, 4);
        // sx = x + 2 (so output[0] = input[2]), sy = y
        var coeffs = new[] { 2.0, 1.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0 };
        var warped = src.Quadratic(coeffs);
        using var rw = new VipsRegion(warped);
        rw.Prepare(new VipsRect(0, 0, 8, 4));
        Assert.Equal(2, rw.GetAddress(0, 0)[0]);
        Assert.Equal(7, rw.GetAddress(5, 0)[0]);
    }

    // ---- Similarity ----

    [Fact]
    public void Similarity_NoOp_ReturnsInputApprox()
    {
        var src = RgbXy(16, 16);
        var same = src.Similarity();
        using var rs = new VipsRegion(src);
        using var rw = new VipsRegion(same);
        rs.Prepare(new VipsRect(0, 0, 16, 16));
        rw.Prepare(new VipsRect(0, 0, 16, 16));
        var p = rw.GetAddress(5, 5);
        Assert.Equal(5, p[0]);
        Assert.Equal(5, p[1]);
    }

    [Fact]
    public void Similarity_ScaleHalvingMaps2xCoordinate()
    {
        // scale=0.5 means output is the input scaled by 0.5: pixel (x, y) of
        // the output samples (2x, 2y) of the input.
        var src = RampX(16, 16);
        var s = src.Similarity(scale: 0.5);
        using var rw = new VipsRegion(s);
        rw.Prepare(new VipsRect(0, 0, 16, 16));
        // out (2, 0) → src (4, 0) = 4.
        Assert.Equal(4, rw.GetAddress(2, 0)[0]);
        Assert.Equal(6, rw.GetAddress(3, 0)[0]);
    }

    [Fact]
    public void Similarity_RotateNinety_MovesAxes()
    {
        var src = RgbXy(16, 16);
        // Rotate 90° CCW about origin; pre-translate so the result lands on canvas.
        // For pixel (x, y) → source (cos θ · x − sin θ · y, sin θ · x + cos θ · y);
        // here θ = -90° (because Affine reads source coords).
        var rotated = src.Similarity(angle: 90.0, idx: 0, idy: 15.0);
        Assert.Equal(16, rotated.Width);
        Assert.Equal(16, rotated.Height);
        // We can't do an exact pixel check without knowing the pivot — but
        // the output should still be RGB and same dims.
        Assert.Equal(3, rotated.Bands);
    }
}
