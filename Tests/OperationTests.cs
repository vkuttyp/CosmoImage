using System;
using Xunit;

namespace CosmoImage.Tests;

public class OperationTests
{
    [Fact]
    public void Invert_SimpleImage_InvertsPixels()
    {
        // Arrange: A 2x2 grayscale image where all pixels are 100
        var image = new VipsImage
        {
            Width = 2,
            Height = 2,
            Bands = 1,
            BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++) {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++) addr[x] = 100;
                }
                return 0;
            }
        };

        // Act
        var inverted = VipsImageOps.Invert(image);
        
        // Prepare a region to trigger lazy evaluation
        using var outRegion = new VipsRegion(inverted);
        outRegion.Prepare(new VipsRect(0, 0, 2, 2));

        // Assert
        var addr = outRegion.GetAddress(0, 0);
        Assert.Equal(155, addr[0]); // 255 - 100 = 155
        
        addr = outRegion.GetAddress(1, 1);
        Assert.Equal(155, addr[0]);
    }

    [Fact]
    public void ExtractArea_SimpleImage_ExtractsSubRegion()
    {
        // Arrange: 10x10 image where pixel value = x + y
        var image = new VipsImage
        {
            Width = 10,
            Height = 10,
            Bands = 1,
            BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++) {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++) {
                        addr[x] = (byte)(reg.Valid.Left + x + reg.Valid.Top + y);
                    }
                }
                return 0;
            }
        };

        // Act: Extract 2x2 area starting at (3, 4)
        var extracted = VipsImageOps.ExtractArea(image, 3, 4, 2, 2);

        // Assert
        Assert.Equal(2, extracted.Width);
        Assert.Equal(2, extracted.Height);

        using var outRegion = new VipsRegion(extracted);
        outRegion.Prepare(new VipsRect(0, 0, 2, 2));

        // Pixel at (0, 0) of extracted should be original (3, 4) => 3 + 4 = 7
        Assert.Equal(7, outRegion.GetAddress(0, 0)[0]);
        // Pixel at (1, 1) of extracted should be original (4, 5) => 4 + 5 = 9
        Assert.Equal(9, outRegion.GetAddress(1, 1)[0]);
    }

    [Fact]
    public void Flip_Vertical_FlipsImage()
    {
        // Arrange: 2x2 image, top row 10, bottom row 20
        var image = new VipsImage
        {
            Width = 2,
            Height = 2,
            Bands = 1,
            BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++) {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    byte val = (byte)(reg.Valid.Top + y == 0 ? 10 : 20);
                    for (int x = 0; x < reg.Valid.Width; x++) addr[x] = val;
                }
                return 0;
            }
        };

        // Act
        var flipped = VipsImageOps.Flip(image, VipsDirection.Vertical);

        // Assert
        using var outRegion = new VipsRegion(flipped);
        outRegion.Prepare(new VipsRect(0, 0, 2, 2));

        // Now top row should be 20, bottom row 10
        Assert.Equal(20, outRegion.GetAddress(0, 0)[0]);
        Assert.Equal(10, outRegion.GetAddress(0, 1)[0]);
    }

    [Fact]
    public void Rotate_90_RotatesImage()
    {
        // Arrange: 2x2 image
        // (0,0)=1, (1,0)=2
        // (0,1)=3, (1,1)=4
        var image = new VipsImage
        {
            Width = 2,
            Height = 2,
            Bands = 1,
            BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++) {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++) {
                        int gx = reg.Valid.Left + x;
                        int gy = reg.Valid.Top + y;
                        if (gx == 0 && gy == 0) addr[x] = 1;
                        else if (gx == 1 && gy == 0) addr[x] = 2;
                        else if (gx == 0 && gy == 1) addr[x] = 3;
                        else if (gx == 1 && gy == 1) addr[x] = 4;
                    }
                }
                return 0;
            }
        };

        // Act: 90 deg clockwise
        // (0,0) -> (1,0)
        // (1,0) -> (1,1)
        // (0,1) -> (0,0)
        // (1,1) -> (0,1)
        // So new (0,0) is old (0,1) = 3
        var rotated = VipsImageOps.Rotate(image, VipsAngle.D90);

        // Assert
        using var outRegion = new VipsRegion(rotated);
        outRegion.Prepare(new VipsRect(0, 0, 2, 2));

        Assert.Equal(3, outRegion.GetAddress(0, 0)[0]);
        Assert.Equal(1, outRegion.GetAddress(1, 0)[0]);
        Assert.Equal(4, outRegion.GetAddress(0, 1)[0]);
        Assert.Equal(2, outRegion.GetAddress(1, 1)[0]);
    }

    [Fact]
    public void Shrink_SimpleImage_AveragesPixels()
    {
        // Arrange: 4x4 image, all pixels in a 2x2 block are same
        // Block (0,0): 10, Block (2,0): 20
        // Block (0,2): 30, Block (2,2): 40
        var image = new VipsImage
        {
            Width = 4,
            Height = 4,
            Bands = 1,
            BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++) {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++) {
                        int gx = reg.Valid.Left + x;
                        int gy = reg.Valid.Top + y;
                        if (gx < 2 && gy < 2) addr[x] = 10;
                        else if (gx >= 2 && gy < 2) addr[x] = 20;
                        else if (gx < 2 && gy >= 2) addr[x] = 30;
                        else addr[x] = 40;
                    }
                }
                return 0;
            }
        };

        // Act: Shrink 2x2
        var shrunk = VipsImageOps.Shrink(image, 2, 2);

        // Assert
        Assert.Equal(2, shrunk.Width);
        Assert.Equal(2, shrunk.Height);

        using var outRegion = new VipsRegion(shrunk);
        outRegion.Prepare(new VipsRect(0, 0, 2, 2));

        Assert.Equal(10, outRegion.GetAddress(0, 0)[0]);
        Assert.Equal(20, outRegion.GetAddress(1, 0)[0]);
        Assert.Equal(30, outRegion.GetAddress(0, 1)[0]);
        Assert.Equal(40, outRegion.GetAddress(1, 1)[0]);
    }

    [Fact]
    public void Linear_SimpleImage_AppliesScaleAndOffset()
    {
        // Arrange: 1x1 image with value 100
        var image = new VipsImage
        {
            Width = 1, Height = 1, Bands = 1, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => { reg.GetAddress(0, 0)[0] = 100; return 0; }
        };

        // Act: out = 2.0 * in + 10
        var res = VipsImageOps.Linear(image, new double[] { 2.0 }, new double[] { 10.0 });

        // Assert
        using var outRegion = new VipsRegion(res);
        outRegion.Prepare(new VipsRect(0, 0, 1, 1));
        Assert.Equal(210, outRegion.GetAddress(0, 0)[0]);
    }

    [Fact]
    public void Gamma_SimpleImage_AppliesPowerLaw()
    {
        // Arrange: 1x1 image with value 128
        var image = new VipsImage
        {
            Width = 1, Height = 1, Bands = 1, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => { reg.GetAddress(0, 0)[0] = 128; return 0; }
        };

        // Act: exponent 2.2
        var res = VipsImageOps.Gamma(image, 2.2);

        // Assert
        using var outRegion = new VipsRegion(res);
        outRegion.Prepare(new VipsRect(0, 0, 1, 1));
        // (128/255)^(1/2.2) * 255 approx 186
        Assert.InRange(outRegion.GetAddress(0, 0)[0], 185, 187);
    }

    [Fact]
    public void Composite_AlphaOverlay_BlendsCorrectly()
    {
        // Arrange: Base image all 100, Overlay all 200 with 50% alpha (128)
        var baseImg = new VipsImage
        {
            Width = 2, Height = 2, Bands = 3, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => { 
                for (int y = 0; y < reg.Valid.Height; y++) {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width * 3; x++) addr[x] = 100;
                }
                return 0; 
            }
        };
        var overImg = new VipsImage
        {
            Width = 1, Height = 1, Bands = 4, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                var addr = reg.GetAddress(0, 0);
                addr[0] = 200; addr[1] = 200; addr[2] = 200; addr[3] = 128; // 50% alpha
                return 0;
            }
        };

        // Act: Composite overlay at (0, 0)
        var res = VipsImageOps.Composite(baseImg, overImg, 0, 0);

        // Assert
        using var outRegion = new VipsRegion(res);
        outRegion.Prepare(new VipsRect(0, 0, 2, 2));

        // Blended pixel at (0,0): 100 * 0.5 + 200 * 0.5 = 150
        Assert.Equal(150, outRegion.GetAddress(0, 0)[0]);
        // Unaffected pixel at (1,1): 100
        Assert.Equal(100, outRegion.GetAddress(1, 1)[0]);
    }

    [Fact]
    public void Resize_Bilinear_ScalesImage()
    {
        // Arrange: 2x2 image
        // (0,0)=10, (1,0)=20
        // (0,1)=30, (1,1)=40
        var image = new VipsImage
        {
            Width = 2,
            Height = 2,
            Bands = 1,
            BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++) {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++) {
                        int gx = reg.Valid.Left + x;
                        int gy = reg.Valid.Top + y;
                        if (gx == 0 && gy == 0) addr[x] = 10;
                        else if (gx == 1 && gy == 0) addr[x] = 20;
                        else if (gx == 0 && gy == 1) addr[x] = 30;
                        else if (gx == 1 && gy == 1) addr[x] = 40;
                    }
                }
                return 0;
            }
        };

        // Act: Upscale to 3x3
        var resized = VipsImageOps.Resize(image, 1.5);

        // Assert
        Assert.Equal(3, resized.Width);
        Assert.Equal(3, resized.Height);

        using var outRegion = new VipsRegion(resized);
        outRegion.Prepare(new VipsRect(0, 0, 3, 3));

        // Center pixel (1, 1) should be average of all 4: (10+20+30+40)/4 = 25
        Assert.Equal(25, outRegion.GetAddress(1, 1)[0]);
        }

        [Fact]
        public void Affine_Shear_TransformsImage()
        {
        // Arrange: 10x10 image with a vertical line at x=5
        var image = new VipsImage
        {
            Width = 10, Height = 10, Bands = 1, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++) {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++) {
                        addr[x] = (byte)(reg.Valid.Left + x == 5 ? 255 : 0);
                    }
                }
                return 0;
            }
        };

        // Act: Horizontal shear: out_x = in_x + in_y * 0.5 => in_x = out_x - out_y * 0.5
        // Matrix: A=1, B=-0.5, C=0, D=1
        var sheared = VipsImageOps.Affine(image, 1, -0.5, 0, 1, interpolate: VipsKernel.Nearest);

        // Assert
        using var outRegion = new VipsRegion(sheared);
        outRegion.Prepare(new VipsRect(0, 0, 10, 10));

        // Original line at x=5 should now be at x = 5 + y * 0.5
        // For y=0, x=5
        Assert.Equal(255, outRegion.GetAddress(5, 0)[0]);
        // For y=2, x=6 (5 + 2 * 0.5)
        Assert.Equal(255, outRegion.GetAddress(6, 2)[0]);
        // For y=4, x=7
        Assert.Equal(255, outRegion.GetAddress(7, 4)[0]);
    }

    [Fact]
    public void GaussBlur_UniformImage_RemainsUniform()
    {
        // Arrange: 10x10 uniform grayscale image (val 100)
        var image = new VipsImage
        {
            Width = 10, Height = 10, Bands = 1, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++) {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++) addr[x] = 100;
                }
                return 0;
            }
        };

        // Act
        var blurred = VipsImageOps.GaussBlur(image, 1.0);

        // Assert
        using var outRegion = new VipsRegion(blurred);
        outRegion.Prepare(new VipsRect(3, 3, 4, 4)); // Check central part to avoid edges
        Assert.Equal(100, outRegion.GetAddress(4, 4)[0]);
    }

    [Fact]
    public void UnsharpMask_StepEdge_EnhancesEdge()
    {
        // Arrange: 10x10 image with a step edge (0 at left, 100 at right)
        var image = new VipsImage
        {
            Width = 10, Height = 10, Bands = 1, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++) {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++) {
                        addr[x] = (byte)(reg.Valid.Left + x < 5 ? 0 : 100);
                    }
                }
                return 0;
            }
        };

        // Act
        var sharpened = VipsImageOps.UnsharpMask(image, 1.0, 1.0);

        // Assert
        using var outRegion = new VipsRegion(sharpened);
        outRegion.Prepare(new VipsRect(0, 0, 10, 10));

        // Edge at x=5. 
        // Original: 0, 0, 0, 0, 0, 100, 100, 100...
        // Blurred (approx): ..., 10, 25, 50, 75, 90, ...
        // Sharpened (original + 1.0 * (original - blurred)):
        // At x=4: 0 + 1.0 * (0 - 25) = -25 => 0 (clamped)
        // At x=5: 100 + 1.0 * (100 - 75) = 125
        
        // Let's just check that at x=5 it's > 100
        Assert.True(outRegion.GetAddress(5, 5)[0] > 100);
        // And at x=4 it's < 100 (well, it was 0, so it stays 0 or increases if blur spread)
    }

    [Fact]
    public void Colourspace_RgbToBw_ConvertsLuminance()
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

        // Act: RGB to BW
        var bw = VipsImageOps.Colourspace(image, VipsInterpretation.BW);

        // Assert
        Assert.Equal(1, bw.Bands);
        Assert.Equal(VipsInterpretation.BW, bw.Interpretation);

        using var outRegion = new VipsRegion(bw);
        outRegion.Prepare(new VipsRect(0, 0, 1, 1));
        // Rec.709 Red coefficient is 0.2126. 255 * 0.2126 = 54.213
        Assert.Equal(54, outRegion.GetAddress(0, 0)[0]);
    }

    [Fact]
    public void Thumbnail_LargeImage_DownscalesAndCrops()
    {
        // Arrange: 100x100 uniform image
        var image = new VipsImage
        {
            Width = 100, Height = 100, Bands = 1, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++) {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++) addr[x] = 50;
                }
                return 0;
            }
        };

        // Act: Thumbnail to 20x10 with crop
        var thumb = VipsImageOps.Thumbnail(image, 20, 10, true);

        // Assert
        Assert.Equal(20, thumb.Width);
        Assert.Equal(10, thumb.Height);

        using var outRegion = new VipsRegion(thumb);
        outRegion.Prepare(new VipsRect(0, 0, 20, 10));
        Assert.Equal(50, outRegion.GetAddress(0, 0)[0]);
    }
}





