using System;
using Xunit;

namespace CosmoImage.Tests;

public class DrawingTests
{
    [Fact]
    public void DrawLine_SimpleImage_DrawsLine()
    {
        // Arrange: 10x10 black image
        var image = new VipsImage
        {
            Width = 10, Height = 10, Bands = 1, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++) {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++) addr[x] = 0;
                }
                return 0;
            }
        };

        // Act: Draw horizontal line at y=5 with ink 255
        var line = VipsImageOps.DrawLine(image, 0, 5, 9, 5, new byte[] { 255 });

        // Assert
        using var outRegion = new VipsRegion(line);
        outRegion.Prepare(new VipsRect(0, 0, 10, 10));

        Assert.Equal(255, outRegion.GetAddress(0, 5)[0]);
        Assert.Equal(255, outRegion.GetAddress(5, 5)[0]);
        Assert.Equal(255, outRegion.GetAddress(9, 5)[0]);
        Assert.Equal(0, outRegion.GetAddress(0, 0)[0]);
    }

    [Fact]
    public void DrawRect_SimpleImage_DrawsRect()
    {
        // Arrange: 10x10 black image
        var image = new VipsImage
        {
            Width = 10, Height = 10, Bands = 1, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++) {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++) addr[x] = 0;
                }
                return 0;
            }
        };

        // Act: Draw filled rectangle 4x4 at (2, 2)
        var rect = VipsImageOps.DrawRect(image, 2, 2, 4, 4, new byte[] { 255 }, fill: true);

        // Assert
        using var outRegion = new VipsRegion(rect);
        outRegion.Prepare(new VipsRect(0, 0, 10, 10));

        Assert.Equal(255, outRegion.GetAddress(2, 2)[0]);
        Assert.Equal(255, outRegion.GetAddress(5, 5)[0]);
        Assert.Equal(0, outRegion.GetAddress(0, 0)[0]);
        Assert.Equal(0, outRegion.GetAddress(7, 7)[0]);
    }

    [Fact]
    public void Text_ValidString_CreatesImage()
    {
        // Act
        var text = VipsImageOps.Text("Hello", fontSize: 12);

        // Assert
        Assert.True(text.Width > 0);
        Assert.True(text.Height > 0);
        Assert.Equal(4, text.Bands); // RGBA
    }
}
