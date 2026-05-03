using System;
using CosmoImage.Operations.Drawing;
using Xunit;

namespace CosmoImage.Tests;

public class Round62Tests
{
    private static VipsImage RgbSolid(int w, int h, byte r, byte g, byte b)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 3, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? aa, object? bb, ref bool stop) => {
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

    private static byte[] ReadPel(VipsImage img, int x, int y)
    {
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, img.Width, img.Height));
        return reg.GetAddress(x, y).Slice(0, img.Bands).ToArray();
    }

    // ---- Pen ----

    [Fact]
    public void VipsPen_Solid_FactoryCarriesParameters()
    {
        var pen = VipsPen.Solid(255, 0, 0, width: 4);
        Assert.Equal(4.0, pen.Width);
        Assert.NotNull(pen.Brush);
    }

    [Fact]
    public void VipsPen_RejectsNonPositiveWidth()
    {
        Assert.Throws<ArgumentException>(() => VipsPen.Solid(0, 0, 0, width: 0));
        Assert.Throws<ArgumentException>(() => VipsPen.Solid(0, 0, 0, width: -1));
    }

    // ---- StrokeLine ----

    [Fact]
    public void StrokeLine_Horizontal_PaintsBand()
    {
        var bg = RgbSolid(20, 10, 0, 0, 0);
        var pen = VipsPen.Solid(255, 255, 255, width: 3);
        var painted = VipsImageOps.StrokeLine(bg, pen, 2, 5, 18, 5);
        // Centre of the band should be painted.
        var centre = ReadPel(painted, 10, 5);
        Assert.Equal(255, centre[0]);
        // Far above / below should be black.
        var above = ReadPel(painted, 10, 0);
        Assert.Equal(0, above[0]);
    }

    [Fact]
    public void StrokeLine_Vertical_PaintsBand()
    {
        var bg = RgbSolid(10, 20, 0, 0, 0);
        var pen = VipsPen.Solid(0, 255, 0, width: 2);
        var painted = VipsImageOps.StrokeLine(bg, pen, 5, 2, 5, 18);
        var centre = ReadPel(painted, 5, 10);
        Assert.Equal(255, centre[1]);
    }

    [Fact]
    public void StrokeLine_Diagonal_PaintsAlongPath()
    {
        var bg = RgbSolid(30, 30, 0, 0, 0);
        var pen = VipsPen.Solid(255, 0, 255, width: 2);
        var painted = VipsImageOps.StrokeLine(bg, pen, 5, 5, 25, 25);
        // Pixel on the diagonal should be painted.
        var diag = ReadPel(painted, 15, 15);
        Assert.Equal(255, diag[0]);
        Assert.Equal(255, diag[2]);
    }

    // ---- StrokeRectangle ----

    [Fact]
    public void StrokeRectangle_PaintsOutlineOnly()
    {
        var bg = RgbSolid(20, 20, 0, 0, 0);
        var pen = VipsPen.Solid(255, 255, 255, width: 2);
        var painted = VipsImageOps.StrokeRectangle(bg, pen, x: 5, y: 5, w: 10, h: 10);
        // A pixel near the rectangle top edge should be painted.
        var top = ReadPel(painted, 10, 5);
        Assert.Equal(255, top[0]);
        // The interior of the rectangle should NOT be painted.
        var interior = ReadPel(painted, 10, 10);
        Assert.Equal(0, interior[0]);
    }

    // ---- StrokeCircle ----

    [Fact]
    public void StrokeCircle_PaintsRingOnly()
    {
        var bg = RgbSolid(40, 40, 0, 0, 0);
        var pen = VipsPen.Solid(255, 255, 255, width: 3);
        var painted = VipsImageOps.StrokeCircle(bg, pen, cx: 20, cy: 20, radius: 12);
        // Pixel on the circle (12 px right of centre) should be painted.
        var ring = ReadPel(painted, 32, 20);
        Assert.Equal(255, ring[0]);
        // Centre is INSIDE the circle, so outside the stroke ring → black.
        var inside = ReadPel(painted, 20, 20);
        Assert.Equal(0, inside[0]);
        // Far outside is also black.
        var outside = ReadPel(painted, 0, 0);
        Assert.Equal(0, outside[0]);
    }

    // ---- StrokePolygon ----

    [Fact]
    public void StrokePolygon_TriangleOutline()
    {
        var bg = RgbSolid(30, 30, 0, 0, 0);
        var pen = VipsPen.Solid(255, 0, 0, width: 2);
        var painted = VipsImageOps.StrokePolygon(bg, pen,
            (15, 5), (25, 25), (5, 25));
        // Roughly midpoint of the bottom edge — should be painted.
        var bottomMid = ReadPel(painted, 15, 25);
        Assert.Equal(255, bottomMid[0]);
        // Centre of the triangle is interior — should NOT be painted.
        var interior = ReadPel(painted, 15, 18);
        Assert.Equal(0, interior[0]);
    }

    // ---- StrokePath with curves ----

    [Fact]
    public void StrokePath_QuadraticArc()
    {
        var bg = RgbSolid(40, 40, 0, 0, 0);
        var pen = VipsPen.Solid(255, 255, 255, width: 2);
        var path = new VipsPath()
            .MoveTo(5, 30)
            .QuadraticTo(20, 5, 35, 30);
        var painted = VipsImageOps.StrokePath(bg, path, pen);
        // Quadratic Bezier passes through (20, 17.5) at t=0.5
        // (B(0.5) = ¼·start + ½·ctrl + ¼·end). Pixel (20, 17) sits
        // inside the stroked band; centre coverage is close to 1 so
        // the brush colour comes through strongly even with AA.
        var peak = ReadPel(painted, 20, 17);
        Assert.True(peak[0] > 200);
    }

    [Fact]
    public void StrokePath_BrushColourReachesAllStrokedPixels()
    {
        var bg = RgbSolid(10, 10, 0, 0, 0);
        var pen = VipsPen.Solid(50, 100, 150, width: 2);
        var painted = VipsImageOps.StrokeLine(bg, pen, 2, 5, 8, 5);
        var p = ReadPel(painted, 5, 5);
        Assert.Equal(50, p[0]);
        Assert.Equal(100, p[1]);
        Assert.Equal(150, p[2]);
    }
}
