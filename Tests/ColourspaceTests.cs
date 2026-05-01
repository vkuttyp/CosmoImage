using System;
using Xunit;

namespace CosmoImage.Tests;

public class ColourspaceTests
{
    [Fact]
    public void Colourspace_RgbToLab_ConvertsToFloats()
    {
        // Arrange: 1x1 RGB image with pure Red (255, 0, 0)
        var image = new VipsImage
        {
            Width = 1, Height = 1, Bands = 3, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                var addr = reg.GetAddress(0, 0);
                addr[0] = 255; addr[1] = 0; addr[2] = 0;
                return 0;
            }
        };

        // Act: RGB to Lab
        var lab = VipsImageOps.Colourspace(image, VipsInterpretation.Lab);

        // Assert
        Assert.Equal(3, lab.Bands);
        Assert.Equal(VipsBandFormat.Float, lab.BandFormat);
        Assert.Equal(VipsInterpretation.Lab, lab.Interpretation);

        using var outRegion = new VipsRegion(lab);
        outRegion.Prepare(new VipsRect(0, 0, 1, 1));
        
        var addr = outRegion.GetAddress(0, 0);
        float L = BitConverter.ToSingle(addr.Slice(0, 4));
        float a_val = BitConverter.ToSingle(addr.Slice(4, 4));
        float b_val = BitConverter.ToSingle(addr.Slice(8, 4));

        // Pure Red in Lab (D65) is approx L=53, a=80, b=67
        Assert.InRange(L, 50, 60);
        Assert.True(a_val > 50);
        Assert.True(b_val > 50);
    }

    [Fact]
    public void Colourspace_RgbToCmyk_ConvertsToFourBands()
    {
        // Arrange: 1x1 RGB image with pure Blue (0, 0, 255)
        var image = new VipsImage
        {
            Width = 1, Height = 1, Bands = 3, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                var addr = reg.GetAddress(0, 0);
                addr[0] = 0; addr[1] = 0; addr[2] = 255;
                return 0;
            }
        };

        // Act: RGB to CMYK
        var cmyk = VipsImageOps.Colourspace(image, VipsInterpretation.CMYK);

        // Assert
        Assert.Equal(4, cmyk.Bands);
        Assert.Equal(VipsInterpretation.CMYK, cmyk.Interpretation);

        using var outRegion = new VipsRegion(cmyk);
        outRegion.Prepare(new VipsRect(0, 0, 1, 1));
        
        var addr = outRegion.GetAddress(0, 0);
        // Pure blue (0,0,255) -> C=255, M=255, Y=0, K=0 (naive)
        Assert.Equal(255, addr[0]); // C
        Assert.Equal(255, addr[1]); // M
        Assert.Equal(0, addr[2]);   // Y
        Assert.Equal(0, addr[3]);   // K
    }
}
