using System;
using System.Linq;
using CosmoImage.Operations.Drawing;
using Xunit;

namespace CosmoImage.Tests;

public class Round68Tests
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

    private static int CountClosedSubpaths(VipsPath p)
        => p.Segments.Count(s => s.Kind == VipsPathSegmentKind.Close);

    // ---- Geometry: overlap rects ----

    [Fact]
    public void Intersect_TwoOverlappingRects_GivesOverlapRect()
    {
        // A = (0,0)-(10,10), B = (5,5)-(15,15). Overlap = (5,5)-(10,10).
        var a = VipsPath.Rectangle(0, 0, 10, 10);
        var b = VipsPath.Rectangle(5, 5, 10, 10);
        var inter = a.Intersect(b);
        // One closed sub-path with 4 unique corner vertices.
        Assert.Equal(1, CountClosedSubpaths(inter));
        var corners = inter.Segments
            .Where(s => s.Kind == VipsPathSegmentKind.MoveTo || s.Kind == VipsPathSegmentKind.LineTo)
            .Select(s => (s.X1, s.Y1)).ToHashSet();
        Assert.Contains((5.0, 5.0), corners);
        Assert.Contains((10.0, 5.0), corners);
        Assert.Contains((10.0, 10.0), corners);
        Assert.Contains((5.0, 10.0), corners);
    }

    [Fact]
    public void Union_TwoOverlappingRects_GivesLShape()
    {
        var a = VipsPath.Rectangle(0, 0, 10, 10);
        var b = VipsPath.Rectangle(5, 5, 10, 10);
        var u = a.Union(b);
        Assert.Equal(1, CountClosedSubpaths(u));
        // L-shape has 8 unique vertices.
        var verts = u.Segments
            .Where(s => s.Kind == VipsPathSegmentKind.MoveTo || s.Kind == VipsPathSegmentKind.LineTo)
            .Select(s => (s.X1, s.Y1)).ToHashSet();
        Assert.Equal(8, verts.Count);
    }

    [Fact]
    public void Subtract_TwoOverlappingRects_GivesNotchedShape()
    {
        // A - B should leave A with its bottom-right (5,5)-(10,10) corner cut out.
        var a = VipsPath.Rectangle(0, 0, 10, 10);
        var b = VipsPath.Rectangle(5, 5, 10, 10);
        var d = a.Subtract(b);
        Assert.Equal(1, CountClosedSubpaths(d));
        var verts = d.Segments
            .Where(s => s.Kind == VipsPathSegmentKind.MoveTo || s.Kind == VipsPathSegmentKind.LineTo)
            .Select(s => (s.X1, s.Y1)).ToHashSet();
        // Notched-L has 6 unique vertices.
        Assert.Equal(6, verts.Count);
        Assert.Contains((0.0, 0.0), verts);
        Assert.Contains((10.0, 0.0), verts);
        Assert.Contains((10.0, 5.0), verts);
        Assert.Contains((5.0, 5.0), verts);
        Assert.Contains((5.0, 10.0), verts);
        Assert.Contains((0.0, 10.0), verts);
    }

    // ---- Disjoint rects ----

    [Fact]
    public void Intersect_DisjointRects_GivesEmptyPath()
    {
        var a = VipsPath.Rectangle(0, 0, 5, 5);
        var b = VipsPath.Rectangle(20, 20, 5, 5);
        var inter = a.Intersect(b);
        Assert.Empty(inter.Segments);
    }

    [Fact]
    public void Union_DisjointRects_GivesTwoSubpaths()
    {
        var a = VipsPath.Rectangle(0, 0, 5, 5);
        var b = VipsPath.Rectangle(20, 20, 5, 5);
        var u = a.Union(b);
        Assert.Equal(2, CountClosedSubpaths(u));
    }

    [Fact]
    public void Subtract_DisjointRects_GivesSubjectUnchanged()
    {
        var a = VipsPath.Rectangle(0, 0, 5, 5);
        var b = VipsPath.Rectangle(20, 20, 5, 5);
        var d = a.Subtract(b);
        Assert.Equal(1, CountClosedSubpaths(d));
        var verts = d.Segments
            .Where(s => s.Kind == VipsPathSegmentKind.MoveTo || s.Kind == VipsPathSegmentKind.LineTo)
            .Select(s => (s.X1, s.Y1)).ToHashSet();
        Assert.Equal(4, verts.Count);
        Assert.Contains((0.0, 0.0), verts);
        Assert.Contains((5.0, 5.0), verts);
    }

    // ---- One fully inside the other ----

    [Fact]
    public void Intersect_SubjectInsideClip_GivesSubject()
    {
        var inner = VipsPath.Rectangle(2, 2, 4, 4);
        var outer = VipsPath.Rectangle(0, 0, 10, 10);
        var inter = inner.Intersect(outer);
        Assert.Equal(1, CountClosedSubpaths(inter));
        var verts = inter.Segments
            .Where(s => s.Kind == VipsPathSegmentKind.MoveTo || s.Kind == VipsPathSegmentKind.LineTo)
            .Select(s => (s.X1, s.Y1)).ToHashSet();
        Assert.Contains((2.0, 2.0), verts);
        Assert.Contains((6.0, 6.0), verts);
    }

    [Fact]
    public void Subtract_ClipInsideSubject_GivesDonut()
    {
        // Outer minus inner: subject preserved + inner reversed as a hole sub-path.
        var outer = VipsPath.Rectangle(0, 0, 10, 10);
        var inner = VipsPath.Rectangle(3, 3, 4, 4);
        var d = outer.Subtract(inner);
        Assert.Equal(2, CountClosedSubpaths(d));
    }

    // ---- Rendered correctness ----

    [Fact]
    public void Intersect_RenderedFill_ExactlyCoversOverlap()
    {
        // Render the intersection of two rectangles and check pixel coverage.
        var bg = RgbSolid(20, 20, 0, 0, 0);
        var brush = new VipsSolidBrush(255, 0, 0);
        var a = VipsPath.Rectangle(2, 2, 10, 10);
        var b = VipsPath.Rectangle(7, 7, 10, 10);
        var inter = a.Intersect(b);
        var painted = VipsImageOps.FillPath(bg, inter, brush, aa: false);
        // Inside overlap (7,7)-(12,12).
        Assert.Equal(255, ReadPel(painted, 9, 9)[0]);
        Assert.Equal(255, ReadPel(painted, 11, 11)[0]);
        // Inside A but outside overlap.
        Assert.Equal(0, ReadPel(painted, 4, 4)[0]);
        // Inside B but outside overlap.
        Assert.Equal(0, ReadPel(painted, 15, 15)[0]);
    }

    [Fact]
    public void Subtract_RenderedDonut_HasHoleInMiddle()
    {
        // outer - inner using even-odd fill should produce a donut.
        var bg = RgbSolid(20, 20, 0, 0, 0);
        var brush = new VipsSolidBrush(255, 0, 0);
        var outer = VipsPath.Rectangle(2, 2, 16, 16);
        var inner = VipsPath.Rectangle(7, 7, 6, 6);
        var d = outer.Subtract(inner);
        var painted = VipsImageOps.FillPath(bg, d, brush, aa: false);
        // Outside donut entirely → unpainted.
        Assert.Equal(0, ReadPel(painted, 0, 0)[0]);
        // In donut "ring" → painted.
        Assert.Equal(255, ReadPel(painted, 4, 10)[0]);
        Assert.Equal(255, ReadPel(painted, 15, 10)[0]);
        // Inside hole → unpainted.
        Assert.Equal(0, ReadPel(painted, 10, 10)[0]);
    }

    // ---- Curves are flattened first ----

    [Fact]
    public void Intersect_AcceptsBezierShapes()
    {
        // A circle intersected with a rect — both flatten to polylines first.
        var circle = VipsPath.Circle(10, 10, 5);
        var rect = VipsPath.Rectangle(10, 5, 10, 10);
        var inter = circle.Intersect(rect);
        Assert.NotEmpty(inter.Segments);
        Assert.Equal(1, CountClosedSubpaths(inter));
    }

    // ---- Input validation ----

    [Fact]
    public void Boolean_RejectsMultiSubpathSubject()
    {
        var multi = new VipsPath()
            .MoveTo(0, 0).LineTo(5, 0).LineTo(5, 5).Close()
            .MoveTo(10, 0).LineTo(15, 0).LineTo(15, 5).Close();
        var single = VipsPath.Rectangle(0, 0, 10, 10);
        Assert.Throws<ArgumentException>(() => multi.Intersect(single));
    }
}
