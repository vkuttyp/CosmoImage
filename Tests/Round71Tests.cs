using System;
using System.Linq;
using CosmoImage.Operations.Drawing;
using Xunit;

namespace CosmoImage.Tests;

public class Round71Tests
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

    private static int CountVertices(VipsPath p)
        => p.Segments.Count(s => s.Kind == VipsPathSegmentKind.MoveTo
                              || s.Kind == VipsPathSegmentKind.LineTo);

    private static int CountSubpaths(VipsPath p)
        => p.Segments.Count(s => s.Kind == VipsPathSegmentKind.MoveTo);

    // ---- Outline ----

    [Fact]
    public void Outline_OpenLine_ProducesQuadOutline()
    {
        // Open horizontal line with butt caps → 4 vertices outlining the rectangle of stroke.
        var line = new VipsPath().MoveTo(0, 5).LineTo(10, 5);
        var outline = line.Outline(2);
        Assert.True(CountVertices(outline) >= 4);
    }

    [Fact]
    public void Outline_FilledOutlineMatchesDirectStroke()
    {
        // Stroke directly vs (build outline → fill) should produce the same image.
        var bg = RgbSolid(20, 20, 0, 0, 0);
        var line = new VipsPath().MoveTo(2, 10).LineTo(18, 10);
        var pen = VipsPen.Solid(255, 255, 255, 3);

        var direct = VipsImageOps.StrokePath(bg, line, pen, aa: false);
        var outline = line.Outline(3);
        var indirect = VipsImageOps.FillPath(bg, outline, pen.Brush, aa: false);

        for (int y = 0; y < 20; y++)
            for (int x = 0; x < 20; x++)
                Assert.Equal(ReadPel(direct, x, y)[0], ReadPel(indirect, x, y)[0]);
    }

    [Fact]
    public void Outline_ClosedShape_ProducesPolygonAroundOriginal()
    {
        // Outlining a 4-vertex rectangle should produce more than 4
        // vertices (outer side + reversed inner side = ~10 points).
        var rect = VipsPath.Rectangle(5, 5, 10, 10);
        var outline = rect.Outline(2);
        Assert.True(CountVertices(outline) > 4);
    }

    [Fact]
    public void Outline_RejectsZeroWidth()
    {
        var line = new VipsPath().MoveTo(0, 0).LineTo(10, 0);
        Assert.Throws<ArgumentException>(() => line.Outline(0));
    }

    // ---- Simplify ----

    [Fact]
    public void Simplify_ColinearMidpointsRemoved()
    {
        // 3 colinear points: simplify should drop the middle one.
        var p = new VipsPath().MoveTo(0, 0).LineTo(5, 0).LineTo(10, 0);
        var s = p.Simplify(0.5);
        Assert.Equal(2, CountVertices(s));
    }

    [Fact]
    public void Simplify_ZeroTolerance_KeepsAllMeaningfulVertices()
    {
        // A zigzag where every point is meaningful (deviates from chord).
        var p = new VipsPath()
            .MoveTo(0, 0).LineTo(5, 5).LineTo(10, 0).LineTo(15, 5).LineTo(20, 0);
        var s = p.Simplify(0);
        // All 5 vertices should survive.
        Assert.Equal(5, CountVertices(s));
    }

    [Fact]
    public void Simplify_HighTolerance_CollapsesToEndpoints()
    {
        // Same zigzag but with tolerance > the zigzag amplitude → reduces to start+end.
        var p = new VipsPath()
            .MoveTo(0, 0).LineTo(5, 5).LineTo(10, 0).LineTo(15, 5).LineTo(20, 0);
        var s = p.Simplify(100);
        Assert.Equal(2, CountVertices(s));
    }

    [Fact]
    public void Simplify_PreservesClose()
    {
        var p = VipsPath.Rectangle(0, 0, 10, 10);
        var s = p.Simplify(0.5);
        Assert.Contains(s.Segments, seg => seg.Kind == VipsPathSegmentKind.Close);
    }

    [Fact]
    public void Simplify_FlattensCurves()
    {
        // A circle is 4 cubics — Simplify flattens to many polyline vertices,
        // then DP can reduce them based on tolerance.
        var c = VipsPath.Circle(50, 50, 20);
        var sLow = c.Simplify(0.1);
        var sHigh = c.Simplify(5);
        // Higher tolerance → fewer surviving vertices.
        Assert.True(CountVertices(sHigh) < CountVertices(sLow));
        // After simplification the result is pure polyline (no curve segments).
        Assert.DoesNotContain(sHigh.Segments,
            seg => seg.Kind == VipsPathSegmentKind.CubicTo
                || seg.Kind == VipsPathSegmentKind.QuadraticTo);
    }

    [Fact]
    public void Simplify_RejectsNegativeTolerance()
    {
        var p = new VipsPath().MoveTo(0, 0).LineTo(10, 0);
        Assert.Throws<ArgumentException>(() => p.Simplify(-1));
    }

    // ---- Composition ----

    [Fact]
    public void OutlineThenFill_WithDifferentBrush_Works()
    {
        // Practical use case: outline a path then fill it with a gradient brush.
        var bg = RgbSolid(40, 40, 0, 0, 0);
        var path = new VipsPath().MoveTo(5, 20).LineTo(35, 20);
        var outline = path.Outline(4);
        var grad = new VipsLinearGradientBrush(5, 20, 35, 20,
            new byte[] { 255, 0, 0 }, new byte[] { 0, 0, 255 });
        var painted = VipsImageOps.FillPath(bg, outline, grad, aa: false);
        // Left side red-dominant.
        var l = ReadPel(painted, 8, 20);
        Assert.True(l[0] > l[2]);
        // Right side blue-dominant.
        var r = ReadPel(painted, 32, 20);
        Assert.True(r[2] > r[0]);
    }
}
