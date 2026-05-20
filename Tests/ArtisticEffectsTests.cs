using Xunit;

namespace CosmoImage.Tests;

public class ArtisticEffectsTests
{
    private static VipsImage BuildRgba(int width = 24, int height = 16)
        => new()
        {
            Width = width,
            Height = height,
            Bands = 4,
            BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) =>
            {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        int xx = reg.Valid.Left + x;
                        int yy = reg.Valid.Top + y;
                        addr[x * 4] = (byte)(xx * 7);
                        addr[x * 4 + 1] = (byte)(yy * 11);
                        addr[x * 4 + 2] = (byte)(xx + yy);
                        addr[x * 4 + 3] = (byte)(255 - yy * 8);
                    }
                }
                return 0;
            }
        };

    [Fact]
    public void OilPaint_PreservesImageShape()
    {
        var src = BuildRgba();
        var output = VipsImageOps.OilPaint(src, radius: 2, sigma: 1);
        Assert.Equal(src.Width, output.Width);
        Assert.Equal(src.Height, output.Height);
        Assert.Equal(src.Bands, output.Bands);
    }

    [Fact]
    public void Charcoal_PreservesAlphaBand()
    {
        var src = BuildRgba();
        var output = VipsImageOps.Charcoal(src, radius: 1, sigma: 1);
        using var reg = new VipsRegion(output);
        reg.Prepare(new VipsRect(0, 0, output.Width, output.Height));
        Assert.Equal(src.Bands, output.Bands);
        Assert.Equal(255, reg.GetAddress(0, 0)[3]);
    }

    [Fact]
    public void Sketch_PreservesImageShape()
    {
        var src = BuildRgba();
        var output = VipsImageOps.Sketch(src, radius: 1, sigma: 1, angle: 30);
        Assert.Equal(src.Width, output.Width);
        Assert.Equal(src.Height, output.Height);
        Assert.Equal(src.Bands, output.Bands);
    }
}
