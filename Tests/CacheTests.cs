using System;
using System.Runtime.CompilerServices;
using Xunit;

namespace CosmoImage.Tests;

public class CacheTests
{
    [Fact]
    public void Run_SameOperation_ReturnsSameInstance()
    {
        // Arrange
        var image = new VipsImage { Width = 10, Height = 10, Bands = 1, BandFormat = VipsBandFormat.UChar };
        
        // Act
        var res1 = VipsImageOps.Invert(image);
        var res2 = VipsImageOps.Invert(image);
        
        // Assert
        Assert.Same(res1, res2);
    }

    [Fact]
    public void Run_DifferentOperation_ReturnsDifferentInstance()
    {
        // Arrange
        var image = new VipsImage { Width = 10, Height = 10, Bands = 1, BandFormat = VipsBandFormat.UChar };
        
        // Act
        var res1 = VipsImageOps.Invert(image);
        var res2 = VipsImageOps.Flip(image, VipsDirection.Vertical);
        
        // Assert
        Assert.NotSame(res1, res2);
    }

    [Fact]
    public void Run_SameOperationDifferentInput_ReturnsDifferentInstance()
    {
        // Arrange
        var image1 = new VipsImage { Width = 10, Height = 10, Bands = 1, BandFormat = VipsBandFormat.UChar };
        var image2 = new VipsImage { Width = 10, Height = 10, Bands = 1, BandFormat = VipsBandFormat.UChar };
        
        // Act
        var res1 = VipsImageOps.Invert(image1);
        var res2 = VipsImageOps.Invert(image2);
        
        // Assert
        Assert.NotSame(res1, res2);
    }
}
