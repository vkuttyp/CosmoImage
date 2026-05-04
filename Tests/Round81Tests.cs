using System;
using CosmoImage.Operations.Geometric;
using Xunit;

namespace CosmoImage.Tests;

public class Round81Tests
{
    /// <summary>Solid RGB image of given size.</summary>
    private static VipsImage RgbSolid(int w, int h, byte r, byte g, byte b)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 3, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? bb, ref bool stop) => {
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

    /// <summary>Half-and-half image: left half red, right half green.</summary>
    private static VipsImage LeftRedRightGreen(int w, int h)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 3, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? bb, ref bool stop) => {
                int mid = w / 2;
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        int gx = reg.Valid.Left + x;
                        if (gx < mid) { addr[x * 3 + 0] = 255; addr[x * 3 + 1] = 0; addr[x * 3 + 2] = 0; }
                        else { addr[x * 3 + 0] = 0; addr[x * 3 + 1] = 255; addr[x * 3 + 2] = 0; }
                    }
                }
                return 0;
            }
        };

    private static byte[] ReadPel(VipsImage img, int x, int y)
    {
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, img.Width, img.Height));
        return reg.GetAddress(x, y).Slice(0, img.Bands).ToArray();
    }

    // ---- Output dimensions ----

    [Fact]
    public void Stretch_OutputExactlyTargetDims()
    {
        var input = RgbSolid(100, 80, 200, 0, 0);
        var output = VipsImageOps.Resize(input, new VipsResizeOptions {
            Width = 50, Height = 40, Mode = VipsResizeMode.Stretch,
        });
        Assert.Equal(50, output.Width);
        Assert.Equal(40, output.Height);
    }

    [Fact]
    public void Crop_OutputExactlyTargetDims()
    {
        // Source aspect 100×80 → target 60×60 (square). Cover scale = max(0.6, 0.75) = 0.75.
        // After resize: 75×60. Crop to 60×60.
        var input = RgbSolid(100, 80, 200, 0, 0);
        var output = VipsImageOps.Resize(input, new VipsResizeOptions {
            Width = 60, Height = 60, Mode = VipsResizeMode.Crop,
        });
        Assert.Equal(60, output.Width);
        Assert.Equal(60, output.Height);
    }

    [Fact]
    public void Pad_OutputExactlyTargetDims()
    {
        // Source 100×80 → target 200×100. Fit scale = min(2, 1.25) = 1.25.
        // After resize: 125×100. Pad to 200×100 → 200×100.
        var input = RgbSolid(100, 80, 200, 0, 0);
        var output = VipsImageOps.Resize(input, new VipsResizeOptions {
            Width = 200, Height = 100, Mode = VipsResizeMode.Pad,
            PadColor = new double[] { 0, 0, 255 },
        });
        Assert.Equal(200, output.Width);
        Assert.Equal(100, output.Height);
    }

    [Fact]
    public void Max_DoesNotEnlarge()
    {
        // Source 100×80 → target 200×200. Both dims fit → unchanged.
        var input = RgbSolid(100, 80, 200, 0, 0);
        var output = VipsImageOps.Resize(input, new VipsResizeOptions {
            Width = 200, Height = 200, Mode = VipsResizeMode.Max,
        });
        Assert.Equal(100, output.Width);
        Assert.Equal(80, output.Height);
    }

    [Fact]
    public void Max_ShrinksWhenLarger()
    {
        // Source 200×100 → target 100×100. Shrink to fit: scale = min(0.5, 1) = 0.5 → 100×50.
        var input = RgbSolid(200, 100, 200, 0, 0);
        var output = VipsImageOps.Resize(input, new VipsResizeOptions {
            Width = 100, Height = 100, Mode = VipsResizeMode.Max,
        });
        Assert.Equal(100, output.Width);
        Assert.Equal(50, output.Height);
    }

    [Fact]
    public void Min_DoesNotShrink()
    {
        // Source 200×100 → target 50×50. Both dims already cover → unchanged.
        var input = RgbSolid(200, 100, 200, 0, 0);
        var output = VipsImageOps.Resize(input, new VipsResizeOptions {
            Width = 50, Height = 50, Mode = VipsResizeMode.Min,
        });
        Assert.Equal(200, output.Width);
        Assert.Equal(100, output.Height);
    }

    [Fact]
    public void Min_GrowsWhenSmaller()
    {
        // Source 50×40 → target 100×100. Cover scale = max(2, 2.5) = 2.5 → 125×100.
        var input = RgbSolid(50, 40, 200, 0, 0);
        var output = VipsImageOps.Resize(input, new VipsResizeOptions {
            Width = 100, Height = 100, Mode = VipsResizeMode.Min,
        });
        Assert.Equal(125, output.Width);
        Assert.Equal(100, output.Height);
    }

    [Fact]
    public void BoxPad_DoesNotEnlarge()
    {
        // Source 50×40 → target 200×200. Source fits, no resize, just pad.
        var input = RgbSolid(50, 40, 200, 0, 0);
        var output = VipsImageOps.Resize(input, new VipsResizeOptions {
            Width = 200, Height = 200, Mode = VipsResizeMode.BoxPad,
            PadColor = new double[] { 0, 0, 0 },
        });
        Assert.Equal(200, output.Width);
        Assert.Equal(200, output.Height);
        // Centre pixel should be the source colour (since the source landed at centre).
        var pel = ReadPel(output, 100, 100);
        Assert.Equal(200, pel[0]);
        Assert.Equal(0, pel[1]);
        // Far corner should be pad colour.
        var corner = ReadPel(output, 5, 5);
        Assert.Equal(0, corner[0]);
        Assert.Equal(0, corner[1]);
    }

    // ---- Anchor positioning ----

    [Fact]
    public void Crop_NorthWest_KeepsLeftSide()
    {
        // 200×100 image: red(0..100), green(100..200). Crop to 100×100 at NW
        // should keep the left (red) half.
        var input = LeftRedRightGreen(200, 100);
        var output = VipsImageOps.Resize(input, new VipsResizeOptions {
            Width = 100, Height = 100, Mode = VipsResizeMode.Crop,
            Position = VipsCompass.NorthWest, Kernel = VipsKernel.Nearest,
        });
        // Cover scale = max(0.5, 1) = 1, no resize. Crop x=0..100 → all red.
        var pel = ReadPel(output, 50, 50);
        Assert.Equal(255, pel[0]);  // red
        Assert.Equal(0, pel[1]);
    }

    [Fact]
    public void Crop_NorthEast_KeepsRightSide()
    {
        var input = LeftRedRightGreen(200, 100);
        var output = VipsImageOps.Resize(input, new VipsResizeOptions {
            Width = 100, Height = 100, Mode = VipsResizeMode.Crop,
            Position = VipsCompass.NorthEast, Kernel = VipsKernel.Nearest,
        });
        var pel = ReadPel(output, 50, 50);
        Assert.Equal(0, pel[0]);
        Assert.Equal(255, pel[1]);  // green
    }

    [Fact]
    public void Crop_Centre_KeepsMiddle()
    {
        // Center crop: should land on the boundary, picking up a mix of both colours.
        // Concrete check: the crop window is x=50..150 of a 200-px wide source.
        var input = LeftRedRightGreen(200, 100);
        var output = VipsImageOps.Resize(input, new VipsResizeOptions {
            Width = 100, Height = 100, Mode = VipsResizeMode.Crop,
            Position = VipsCompass.Centre, Kernel = VipsKernel.Nearest,
        });
        // Left edge of output (x=0) corresponds to source x=50 → red.
        var l = ReadPel(output, 5, 50);
        Assert.Equal(255, l[0]);
        // Right edge of output (x=99) corresponds to source x=149 → green.
        var r = ReadPel(output, 95, 50);
        Assert.Equal(255, r[1]);
    }

    [Fact]
    public void Pad_PadColorFillsExtraSpace()
    {
        // 100×80 source → 200×100 target with blue pad.
        // After scale 1.25: 125×100. Pad at centre puts the image at x=37..162.
        var input = RgbSolid(100, 80, 200, 200, 0);  // yellow
        var output = VipsImageOps.Resize(input, new VipsResizeOptions {
            Width = 200, Height = 100, Mode = VipsResizeMode.Pad,
            PadColor = new double[] { 0, 0, 255 },  // blue
            Position = VipsCompass.Centre, Kernel = VipsKernel.Nearest,
        });
        // Far-left strip should be blue pad.
        var pad = ReadPel(output, 5, 50);
        Assert.Equal(0, pad[0]);
        Assert.Equal(0, pad[1]);
        Assert.Equal(255, pad[2]);
        // Centre should be image colour (yellow).
        var img = ReadPel(output, 100, 50);
        Assert.Equal(200, img[0]);
        Assert.Equal(200, img[1]);
        Assert.Equal(0, img[2]);
    }

    // ---- Validation ----

    [Fact]
    public void Resize_InvalidWidthThrows()
    {
        var input = RgbSolid(10, 10, 0, 0, 0);
        Assert.Throws<ArgumentException>(() => VipsImageOps.Resize(input, new VipsResizeOptions {
            Width = 0, Height = 10, Mode = VipsResizeMode.Stretch,
        }));
    }

    [Fact]
    public void Resize_InvalidHeightThrows()
    {
        var input = RgbSolid(10, 10, 0, 0, 0);
        Assert.Throws<ArgumentException>(() => VipsImageOps.Resize(input, new VipsResizeOptions {
            Width = 10, Height = -1, Mode = VipsResizeMode.Stretch,
        }));
    }
}
