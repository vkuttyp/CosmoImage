using System;
using System.Buffers.Binary;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Coverage for the breadth-first Float branches added to the geometric
/// ops: Shrink, Resize1D (X and Y), Affine. VipsResize is a wrapper around
/// Shrink + Resize1D so it inherits Float for free; covered here too.
/// </summary>
public class FloatGeometricTests
{
    private static VipsImage FloatImage(int w, int h, int bands, Func<int, int, int, float> fill)
        => new VipsImage
        {
            Width = w, Height = h, Bands = bands, BandFormat = VipsBandFormat.Float,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        for (int bnd = 0; bnd < bands; bnd++)
                        {
                            int gx = reg.Valid.Left + x;
                            int gy = reg.Valid.Top + y;
                            BinaryPrimitives.WriteSingleLittleEndian(
                                addr.Slice((x * bands + bnd) * 4, 4),
                                fill(gx, gy, bnd));
                        }
                    }
                }
                return 0;
            }
        };

    private static float ReadFloat(VipsRegion reg, int x, int y, int bnd, int bands)
        => BinaryPrimitives.ReadSingleLittleEndian(reg.GetAddress(x, y).Slice(bnd * 4, 4));

    [Fact]
    public void Shrink_Float_BoxAveragesPixels()
    {
        // 4x4 grid of distinct values: each 2x2 block averages predictably.
        var src = FloatImage(4, 4, 1, (x, y, b) => x + y * 4); // 0..15
        var shrunk = src.Shrink(2, 2);
        Assert.Equal(VipsBandFormat.Float, shrunk.BandFormat);
        Assert.Equal(2, shrunk.Width);
        Assert.Equal(2, shrunk.Height);

        using var reg = new VipsRegion(shrunk);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        // (0,0) block = avg(0, 1, 4, 5) = 2.5
        Assert.Equal(2.5f, ReadFloat(reg, 0, 0, 0, 1));
        // (1,0) block = avg(2, 3, 6, 7) = 4.5
        Assert.Equal(4.5f, ReadFloat(reg, 1, 0, 0, 1));
    }

    [Fact]
    public void Resize1D_Float_HorizontalUpscaleOnUniform_Preserves()
    {
        var src = FloatImage(4, 2, 1, (x, y, b) => 7.5f);
        var wide = src.Resize1D(2.0, vertical: false);
        Assert.Equal(VipsBandFormat.Float, wide.BandFormat);
        Assert.Equal(8, wide.Width);
        Assert.Equal(2, wide.Height);

        using var reg = new VipsRegion(wide);
        reg.Prepare(new VipsRect(0, 0, 8, 2));
        // Uniform input → uniform output regardless of kernel.
        Assert.Equal(7.5f, ReadFloat(reg, 4, 1, 0, 1), 1e-4f);
    }

    [Fact]
    public void Resize1D_Float_VerticalUpscaleOnUniform_Preserves()
    {
        var src = FloatImage(2, 4, 1, (x, y, b) => 12.25f);
        var tall = src.Resize1D(2.0, vertical: true);
        Assert.Equal(VipsBandFormat.Float, tall.BandFormat);
        Assert.Equal(2, tall.Width);
        Assert.Equal(8, tall.Height);

        using var reg = new VipsRegion(tall);
        reg.Prepare(new VipsRect(0, 0, 2, 8));
        Assert.Equal(12.25f, ReadFloat(reg, 1, 4, 0, 1), 1e-4f);
    }

    [Fact]
    public void Resize1D_Float_PreservesUnclampedIntermediates()
    {
        // Float pipeline: a value > 255 should round-trip through Resize1D
        // without being clamped — that's the whole point of Float.
        var src = FloatImage(4, 4, 1, (x, y, b) => 1000.0f);
        var resized = src.Resize1D(0.5, vertical: false);
        using var reg = new VipsRegion(resized);
        reg.Prepare(new VipsRect(0, 0, 2, 4));
        Assert.Equal(1000.0f, ReadFloat(reg, 1, 1, 0, 1), 1e-2f);
    }

    [Fact]
    public void Affine_Float_IdentityTransform_ReturnsSameValues()
    {
        var src = FloatImage(8, 8, 1, (x, y, b) => x * 1.5f + y * 0.25f);
        // Identity affine: A=1, B=0, C=0, D=1, no offsets.
        var transformed = src.Affine(1, 0, 0, 1);
        Assert.Equal(VipsBandFormat.Float, transformed.BandFormat);

        using var reg = new VipsRegion(transformed);
        reg.Prepare(new VipsRect(0, 0, 8, 8));
        // Sample interior pixel where the kernel window stays in-image.
        Assert.Equal(3 * 1.5f + 4 * 0.25f, ReadFloat(reg, 3, 4, 0, 1), 1e-3f);
    }

    [Fact]
    public void Affine_Float_NearestKernel_DirectCopy()
    {
        // 8x8 so the conservative `srcX >= W-1` background bound (shared
        // with the UChar path) doesn't kick in at our test pixel.
        var src = FloatImage(8, 8, 1, (x, y, b) => x * 10f + y);
        var translated = src.Affine(1, 0, 0, 1, idx: 0, idy: 0, interpolate: VipsKernel.Nearest);
        using var reg = new VipsRegion(translated);
        reg.Prepare(new VipsRect(0, 0, 8, 8));
        Assert.Equal(10f, ReadFloat(reg, 1, 0, 0, 1));
        Assert.Equal(23f, ReadFloat(reg, 2, 3, 0, 1));
    }

    [Fact]
    public void Affine_Float_OutOfSource_WritesZero()
    {
        var src = FloatImage(4, 4, 1, (x, y, b) => 999.0f);
        // Translate so part of the output reads outside the source.
        var shifted = src.Affine(1, 0, 0, 1, idx: -10, idy: -10);
        using var reg = new VipsRegion(shifted);
        reg.Prepare(new VipsRect(0, 0, 4, 4));
        // Pixel (0, 0) maps to source (-10, -10) → out of source → 0.
        Assert.Equal(0f, ReadFloat(reg, 0, 0, 0, 1));
    }

    [Fact]
    public void Resize_Float_EndToEndComposesShrinkAndResize1D()
    {
        // 3x downscale: VipsResize composes Shrink (integer 3x) then identity
        // Resize1D since the residual scale is 1.0.
        var src = FloatImage(12, 12, 1, (x, y, b) => 100.0f);
        var thumb = src.Resize(1.0 / 3.0);
        Assert.Equal(VipsBandFormat.Float, thumb.BandFormat);
        Assert.Equal(4, thumb.Width);
        Assert.Equal(4, thumb.Height);

        using var reg = new VipsRegion(thumb);
        reg.Prepare(new VipsRect(0, 0, 4, 4));
        Assert.Equal(100.0f, ReadFloat(reg, 2, 2, 0, 1));
    }

    [Fact]
    public void Resize_Float_FractionalScale_KeepsFloatPrecision()
    {
        var src = FloatImage(8, 8, 1, (x, y, b) => 50.5f);
        var resized = src.Resize(0.75);
        Assert.Equal(VipsBandFormat.Float, resized.BandFormat);

        using var reg = new VipsRegion(resized);
        reg.Prepare(new VipsRect(0, 0, resized.Width, resized.Height));
        // Center pixel should preserve the half-integer value through the
        // Float pipeline; UChar would round it.
        Assert.Equal(50.5f, ReadFloat(reg, 2, 2, 0, 1), 0.1f);
    }

    [Fact]
    public void GammaCorrectDownscale_FloatLinearLight_ChainsCleanly()
    {
        // The classic linear-light downscale pipeline, all in Float:
        // Linearize → Resize → Delinearize. On a uniform grey field the
        // result should match the input value within tight Float tolerance.
        var src = FloatImage(16, 16, 3, (x, y, b) => 0.5f);
        var output = src.Linearize().Resize(0.5).Delinearize();
        Assert.Equal(VipsBandFormat.Float, output.BandFormat);

        using var reg = new VipsRegion(output);
        reg.Prepare(new VipsRect(0, 0, output.Width, output.Height));
        Assert.Equal(0.5f, ReadFloat(reg, 4, 4, 0, 3), 1e-3f);
    }
}
