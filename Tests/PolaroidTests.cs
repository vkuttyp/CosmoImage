using CosmoImage.Core;
using Xunit;

namespace CosmoImage.Tests;

public class PolaroidTests
{
    private static VipsImage SolidRgb(int width, int height, byte r, byte g, byte b)
        => new VipsImage
        {
            Width = width,
            Height = height,
            Bands = 3,
            BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? bctx, ref bool stop) =>
            {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        addr[x * 3 + 0] = r;
                        addr[x * 3 + 1] = g;
                        addr[x * 3 + 2] = b;
                    }
                }
                return 0;
            }
        };

    [Fact]
    public void Polaroid_ReturnsExpandedRgbaImage()
    {
        var src = SolidRgb(24, 18, 20, 40, 60);
        var outImg = VipsImageOps.Polaroid(src, angle: -7);

        Assert.Equal(4, outImg.Bands);
        Assert.True(outImg.Width > src.Width);
        Assert.True(outImg.Height > src.Height);

        using var reg = new VipsRegion(outImg);
        reg.Prepare(new VipsRect(0, 0, outImg.Width, outImg.Height));

        // Rotated RGBA output should have transparent corners.
        Assert.Equal(0, reg.GetAddress(0, 0)[3]);

        // The image body should remain opaque somewhere near the centre.
        var centre = reg.GetAddress(outImg.Width / 2, outImg.Height / 2);
        Assert.True(centre[3] > 0);
    }
}
