using System;
using CosmoImage.Operations.Drawing;
using Xunit;

namespace CosmoImage.Tests;

public class Round66Tests
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

    // ---- Affine transforms ----

    [Fact]
    public void Translate_ShiftsAllSegments()
    {
        var src = VipsPath.Rectangle(0, 0, 4, 4);
        var moved = src.Translate(10, 20);
        // First segment is MoveTo at (0, 0) → should now be (10, 20).
        Assert.Equal(VipsPathSegmentKind.MoveTo, moved.Segments[0].Kind);
        Assert.Equal(10, moved.Segments[0].X1);
        Assert.Equal(20, moved.Segments[0].Y1);
        // Source unchanged.
        Assert.Equal(0, src.Segments[0].X1);
    }

    [Fact]
    public void Scale_AppliesToAllCoordinates()
    {
        var src = VipsPath.Rectangle(2, 3, 4, 5);
        var scaled = src.Scale(2, 3);
        // (2, 3) → (4, 9), (6, 8) → (12, 24).
        Assert.Equal(4, scaled.Segments[0].X1);
        Assert.Equal(9, scaled.Segments[0].Y1);
        // The diagonal corner should also scale.
        Assert.Equal(12, scaled.Segments[2].X1);
        Assert.Equal(24, scaled.Segments[2].Y1);
    }

    [Fact]
    public void Rotate90_AboutOrigin_SwapsAxes()
    {
        // (10, 0) rotated 90° (math: ccw) about origin → (0, 10).
        // With our matrix [[cos, -sin], [sin, cos]] and θ=90°:
        // x' = 0·10 + (-1)·0 = 0; y' = 1·10 + 0·0 = 10.
        var src = new VipsPath().MoveTo(10, 0);
        var rotated = src.Rotate(90);
        Assert.Equal(0, rotated.Segments[0].X1, 6);
        Assert.Equal(10, rotated.Segments[0].Y1, 6);
    }

    [Fact]
    public void RotateAround_CentreIsFixed()
    {
        // Rotating any path about (5, 5) by any angle leaves (5, 5) at (5, 5).
        var src = new VipsPath().MoveTo(5, 5).LineTo(10, 10);
        var rotated = src.RotateAround(45, 5, 5);
        Assert.Equal(5, rotated.Segments[0].X1, 6);
        Assert.Equal(5, rotated.Segments[0].Y1, 6);
    }

    [Fact]
    public void Transform_PreservesBezierControlPoints()
    {
        var src = new VipsPath()
            .MoveTo(0, 0)
            .CubicTo(2, 4, 6, 8, 10, 12);
        var moved = src.Translate(100, 200);
        var seg = moved.Segments[1];
        Assert.Equal(VipsPathSegmentKind.CubicTo, seg.Kind);
        Assert.Equal(102, seg.X1); Assert.Equal(204, seg.Y1);  // c1
        Assert.Equal(106, seg.X2); Assert.Equal(208, seg.Y2);  // c2
        Assert.Equal(110, seg.X3); Assert.Equal(212, seg.Y3);  // end
    }

    // ---- Rendered transforms ----

    [Fact]
    public void TranslatedRectangle_PaintsAtNewLocation()
    {
        var bg = RgbSolid(40, 40, 0, 0, 0);
        var brush = new VipsSolidBrush(255, 255, 255);
        var rect = VipsPath.Rectangle(0, 0, 10, 10).Translate(15, 15);
        var painted = VipsImageOps.FillPath(bg, rect, brush, aa: false);
        // Original rect at (0..9, 0..9) is empty; translated rect at (15..24, 15..24) is painted.
        Assert.Equal(0, ReadPel(painted, 5, 5)[0]);
        Assert.Equal(255, ReadPel(painted, 20, 20)[0]);
    }

    // ---- Clipping ----

    [Fact]
    public void FillPath_ClipRect_LimitsPaintToRectangle()
    {
        var bg = RgbSolid(40, 40, 0, 0, 0);
        var brush = new VipsSolidBrush(255, 0, 0);
        // Big rectangle covers most of the canvas.
        var rect = VipsPath.Rectangle(2, 2, 36, 36);
        // Clip to a 10×10 box in the upper-left.
        var clip = new VipsRect(5, 5, 10, 10);
        var painted = VipsImageOps.FillPath(bg, rect, brush, aa: false, clipRect: clip);

        // Inside the clip → painted.
        Assert.Equal(255, ReadPel(painted, 8, 8)[0]);
        // Outside the clip but inside the original rect → NOT painted.
        Assert.Equal(0, ReadPel(painted, 25, 25)[0]);
        // Outside both → 0 (background).
        Assert.Equal(0, ReadPel(painted, 0, 0)[0]);
    }

    [Fact]
    public void StrokePath_ClipRect_LimitsStroke()
    {
        var bg = RgbSolid(40, 40, 0, 0, 0);
        var pen = VipsPen.Solid(255, 255, 255, width: 2);
        var clip = new VipsRect(0, 0, 20, 40);  // left half only
        var painted = VipsImageOps.StrokeLine(bg, pen, 2, 20, 38, 20, aa: false,
            clipRect: clip);
        // Left half of the line is painted.
        Assert.Equal(255, ReadPel(painted, 10, 20)[0]);
        // Right half is not.
        Assert.Equal(0, ReadPel(painted, 30, 20)[0]);
    }

    [Fact]
    public void FillPath_NullClipRect_PaintsEverywhere()
    {
        var bg = RgbSolid(40, 40, 0, 0, 0);
        var brush = new VipsSolidBrush(200, 200, 200);
        var rect = VipsPath.Rectangle(2, 2, 36, 36);
        // Without a clip, the whole rect should paint.
        var painted = VipsImageOps.FillPath(bg, rect, brush, aa: false, clipRect: null);
        Assert.Equal(200, ReadPel(painted, 5, 5)[0]);
        Assert.Equal(200, ReadPel(painted, 30, 30)[0]);
    }

    [Fact]
    public void FillPath_EmptyClipRect_PaintsNothing()
    {
        var bg = RgbSolid(20, 20, 0, 0, 0);
        var brush = new VipsSolidBrush(255, 0, 0);
        var rect = VipsPath.Rectangle(0, 0, 20, 20);
        // Clip rect with zero size — nothing should paint.
        var clip = new VipsRect(5, 5, 0, 0);
        var painted = VipsImageOps.FillPath(bg, rect, brush, aa: false, clipRect: clip);
        for (int y = 0; y < 20; y++)
            for (int x = 0; x < 20; x++)
                Assert.Equal(0, ReadPel(painted, x, y)[0]);
    }
}
