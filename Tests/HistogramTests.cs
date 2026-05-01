using System;
using Xunit;

namespace CosmoImage.Tests;

public class HistogramTests
{
    [Fact]
    public void HistFind_SimpleImage_CountsPixels()
    {
        // Arrange: 10x10 image with 50 pixels at 10 and 50 at 20
        var image = new VipsImage
        {
            Width = 10, Height = 10, Bands = 1, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++) {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++) {
                        addr[x] = (byte)(reg.Valid.Top + y < 5 ? 10 : 20);
                    }
                }
                return 0;
            }
        };

        // Act
        var hist = VipsImageOps.HistFind(image);

        // Assert
        using var reg = new VipsRegion(hist);
        reg.Prepare(new VipsRect(0, 0, 256, 1));

        uint count10 = BitConverter.ToUInt32(reg.GetAddress(10, 0));
        uint count20 = BitConverter.ToUInt32(reg.GetAddress(20, 0));
        uint count30 = BitConverter.ToUInt32(reg.GetAddress(30, 0));

        Assert.Equal(50u, count10);
        Assert.Equal(50u, count20);
        Assert.Equal(0u, count30);
    }

    [Fact]
    public void HistEqual_DarkImage_StretchesContrast()
    {
        // Arrange: 10x10 image with all values at 10
        var image = new VipsImage
        {
            Width = 10, Height = 10, Bands = 1, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++) {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++) addr[x] = 10;
                }
                return 0;
            }
        };

        // Act
        var equalized = VipsImageOps.HistEqual(image);

        // Assert
        using var reg = new VipsRegion(equalized);
        reg.Prepare(new VipsRect(0, 0, 10, 10));

        // Since all pixels were 10, the cumulative hist at 10 is 100, max is 100
        // Normalized value should be 255
        Assert.Equal(255, reg.GetAddress(0, 0)[0]);
    }
}
