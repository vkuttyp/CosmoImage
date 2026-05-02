using System.Buffers.Binary;
using Xunit;

namespace CosmoImage.Tests;

public class FloatPipelineTests
{
    private static VipsImage UCharUniform(int w, int h, byte value, int bands = 1)
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

    private static float ReadFloat(VipsRegion reg, int x, int y, int band, int bands)
        => BinaryPrimitives.ReadSingleLittleEndian(reg.GetAddress(x, y).Slice(band * 4, 4));

    [Fact]
    public void Cast_UChar_To_Float_PreservesNumericValue()
    {
        var src = UCharUniform(2, 2, 100);
        var f = src.CastFloat();
        Assert.Equal(VipsBandFormat.Float, f.BandFormat);

        using var reg = new VipsRegion(f);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        Assert.Equal(100f, ReadFloat(reg, 0, 0, 0, 1));
    }

    [Fact]
    public void Cast_Float_To_UChar_RoundsAndClamps()
    {
        var src = UCharUniform(2, 2, 100);
        var roundTrip = src.CastFloat().Linear(new[] { 1.0 }, new[] { 0.4 }).CastUChar();

        using var reg = new VipsRegion(roundTrip);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        Assert.Equal(100, reg.GetAddress(0, 0)[0]); // 100.4 → round → 100
    }

    [Fact]
    public void Cast_Identity_IsPassThrough()
    {
        var src = UCharUniform(2, 2, 50);
        var same = src.CastUChar();
        Assert.Same(src, same);
    }

    [Fact]
    public void Linear_OnFloat_ProducesFloatOutputWithoutClamp()
    {
        var src = UCharUniform(2, 2, 200);
        var f = src.CastFloat();
        // 2*200 + 0 = 400, would clamp to 255 in UChar; Float keeps it.
        var doubled = f.Linear(new[] { 2.0 }, new[] { 0.0 });
        Assert.Equal(VipsBandFormat.Float, doubled.BandFormat);

        using var reg = new VipsRegion(doubled);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        Assert.Equal(400f, ReadFloat(reg, 0, 0, 0, 1));
    }

    [Fact]
    public void UCharLinear_VsFloatLinear_AgreeWithinRounding()
    {
        var src = UCharUniform(4, 4, 100);
        var direct = src.Linear(new[] { 1.5 }, new[] { 10.0 });
        var viaFloat = src.CastFloat().Linear(new[] { 1.5 }, new[] { 10.0 }).CastUChar();

        using var rd = new VipsRegion(direct);
        using var rf = new VipsRegion(viaFloat);
        rd.Prepare(new VipsRect(0, 0, 4, 4));
        rf.Prepare(new VipsRect(0, 0, 4, 4));
        // 1.5*100+10 = 160 either way.
        Assert.Equal(160, rd.GetAddress(0, 0)[0]);
        // Float path uses round-to-nearest on cast back; UChar Linear truncates.
        // For inputs that don't sit exactly on a half-pixel, the two can differ
        // by 1 — accept that.
        Assert.InRange(rf.GetAddress(0, 0)[0], 159, 161);
    }

    [Fact]
    public void GaussBlur_OnFloat_PreservesUniformImage()
    {
        var src = UCharUniform(10, 10, 80);
        var blurred = src.CastFloat().GaussBlur(1.0);
        Assert.Equal(VipsBandFormat.Float, blurred.BandFormat);

        using var reg = new VipsRegion(blurred);
        reg.Prepare(new VipsRect(3, 3, 4, 4));
        // Far from edges, Gaussian over a uniform field returns the field value.
        Assert.Equal(80f, ReadFloat(reg, 5, 5, 0, 1), 0.5f);
    }

    [Fact]
    public void FloatPipeline_LinearizeWorkflow_ChainsThroughCastFloat()
    {
        // Sanity check: a longer Float chain runs end-to-end. Image is sized
        // so the GaussBlur kernel fully fits at the test pixel — same edge
        // truncation as the UChar path otherwise drops weight at boundaries.
        var src = UCharUniform(20, 20, 128, bands: 3);
        var output = src
            .CastFloat()
            .Linear(new[] { 1.0 }, new[] { 5.0 })
            .GaussBlur(1.5)
            .CastUChar();

        Assert.Equal(VipsBandFormat.UChar, output.BandFormat);
        using var reg = new VipsRegion(output);
        reg.Prepare(new VipsRect(8, 8, 4, 4));
        // 128 + 5 = 133, blur of uniform = 133 → cast back to 133.
        Assert.InRange(reg.GetAddress(10, 10)[0], 132, 134);
    }
}
