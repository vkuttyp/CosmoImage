using System;
using CosmoImage.Operations.Drawing;
using Xunit;

namespace CosmoImage.Tests;

public class Round61Tests
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

    // ---- Path builder ----

    [Fact]
    public void VipsPath_RectangleHasFourLineSegments()
    {
        var p = VipsPath.Rectangle(1, 2, 3, 4);
        Assert.Equal(VipsPathSegmentKind.MoveTo, p.Segments[0].Kind);
        Assert.Equal(VipsPathSegmentKind.Close, p.Segments[^1].Kind);
        // Move + 3 line + close = 5 segments.
        Assert.Equal(5, p.Segments.Count);
    }

    [Fact]
    public void VipsPath_CircleUsesCubicBeziers()
    {
        var p = VipsPath.Circle(50, 50, 25);
        // Move + 4 cubic + close.
        Assert.Equal(VipsPathSegmentKind.MoveTo, p.Segments[0].Kind);
        Assert.Equal(VipsPathSegmentKind.CubicTo, p.Segments[1].Kind);
        Assert.Equal(6, p.Segments.Count);
    }

    [Fact]
    public void VipsPath_RegularPolygon_NVertices()
    {
        var p = VipsPath.RegularPolygon(0, 0, 6, 10);
        // Hex: 1 move + 5 line + close = 7.
        Assert.Equal(7, p.Segments.Count);
    }

    // ---- FillPath ----

    [Fact]
    public void FillRectangle_PaintsInteriorOnly()
    {
        var bg = RgbSolid(10, 10, 0, 0, 0);
        var brush = new VipsSolidBrush(255, 200, 100);
        var painted = VipsImageOps.Fill(bg, brush, x: 2, y: 2, w: 4, h: 4);
        // Inside rect: brush colour. Outside: black.
        var inside = ReadPel(painted, 4, 4);
        Assert.Equal(255, inside[0]); Assert.Equal(200, inside[1]); Assert.Equal(100, inside[2]);
        var outside = ReadPel(painted, 0, 0);
        Assert.Equal(0, outside[0]);
    }

    [Fact]
    public void FillCircle_PaintsInteriorPixels()
    {
        var bg = RgbSolid(20, 20, 0, 0, 0);
        var brush = new VipsSolidBrush(255, 0, 0);
        var painted = VipsImageOps.FillCircle(bg, brush, cx: 10, cy: 10, radius: 6);
        // Centre pixel is inside the circle.
        var centre = ReadPel(painted, 10, 10);
        Assert.Equal(255, centre[0]);
        // 8 px from centre (outside radius 6) is unpainted.
        var outside = ReadPel(painted, 18, 10);
        Assert.Equal(0, outside[0]);
    }

    [Fact]
    public void FillPolygon_Triangle()
    {
        var bg = RgbSolid(10, 10, 0, 0, 0);
        var brush = new VipsSolidBrush(255, 255, 0);
        var painted = VipsImageOps.FillPolygon(bg, brush,
            (5, 1), (9, 8), (1, 8));
        // The triangle's centroid (5, 5.5) should be painted.
        var inside = ReadPel(painted, 5, 5);
        Assert.Equal(255, inside[0]); Assert.Equal(255, inside[1]); Assert.Equal(0, inside[2]);
        // Outside the triangle (corner) is unpainted.
        var corner = ReadPel(painted, 0, 0);
        Assert.Equal(0, corner[0]);
    }

    [Fact]
    public void FillPath_CubicCurve_PaintsAlongCurve()
    {
        var bg = RgbSolid(20, 20, 0, 0, 0);
        var brush = new VipsSolidBrush(255, 255, 255);
        // Cubic-bounded "blob": top arc + bottom arc, clearly enclosing
        // the centre. Both cubics bulge outward so the centroid is
        // firmly inside the painted region.
        var p = new VipsPath()
            .MoveTo(2, 10)
            .CubicTo(2, 2, 18, 2, 18, 10)   // top arc
            .CubicTo(18, 18, 2, 18, 2, 10)  // bottom arc
            .Close();
        var painted = VipsImageOps.FillPath(bg, p, brush);
        var centre = ReadPel(painted, 10, 10);
        Assert.Equal(255, centre[0]);
    }

    // ---- Brushes ----

    [Fact]
    public void SolidBrush_PaintsAllPixelsSameColor()
    {
        var bg = RgbSolid(8, 8, 0, 0, 0);
        var brush = new VipsSolidBrush(50, 100, 150);
        var painted = VipsImageOps.Fill(bg, brush, 1, 1, 6, 6);
        var p1 = ReadPel(painted, 2, 2);
        var p2 = ReadPel(painted, 5, 5);
        Assert.Equal(p1, p2);
        Assert.Equal(50, p1[0]);
    }

    [Fact]
    public void LinearGradientBrush_InterpolatesAlongAxis()
    {
        var bg = RgbSolid(20, 4, 0, 0, 0);
        // Gradient from black at x=0 to white at x=19.
        var brush = new VipsLinearGradientBrush(0, 0, 19, 0,
            colorStart: new byte[] { 0, 0, 0 },
            colorEnd: new byte[] { 255, 255, 255 });
        var painted = VipsImageOps.Fill(bg, brush, 0, 0, 20, 4);
        // Mid-x should be ~mid grey.
        var mid = ReadPel(painted, 10, 2);
        Assert.InRange(mid[0], 120, 140);
        // Far-right should be near white.
        var right = ReadPel(painted, 19, 2);
        Assert.True(right[0] > 240);
    }

    [Fact]
    public void RadialGradientBrush_BrightAtCentre()
    {
        var bg = RgbSolid(20, 20, 0, 0, 0);
        var brush = new VipsRadialGradientBrush(cx: 10, cy: 10, radius: 8,
            colorCentre: new byte[] { 255, 255, 255 },
            colorEdge: new byte[] { 0, 0, 0 });
        var painted = VipsImageOps.Fill(bg, brush, 0, 0, 20, 20);
        var centre = ReadPel(painted, 10, 10);
        var edge = ReadPel(painted, 18, 10);
        // Centre brighter than edge.
        Assert.True(centre[0] > edge[0]);
        Assert.Equal(255, centre[0]);
    }
}
