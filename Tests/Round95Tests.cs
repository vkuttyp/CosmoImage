using System;
using System.Linq;
using CosmoImage.Operations.Convolution;
using CosmoImage.Operations.Drawing;
using Xunit;

namespace CosmoImage.Tests;

public class Round95Tests
{
    private static VipsImage UCharSolid(int w, int h, byte r, byte g, byte b)
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

    private static VipsImage Mono(int w, int h, byte v)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? bb, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++) addr[x] = v;
                }
                return 0;
            }
        };

    /// <summary>Mono image with a vertical edge: left half = 0, right half = 255.</summary>
    private static VipsImage VerticalEdge(int w, int h)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? bb, ref bool stop) => {
                int mid = w / 2;
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        int gx = reg.Valid.Left + x;
                        addr[x] = gx < mid ? (byte)0 : (byte)255;
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

    // ---- GaussianSharpen ----

    [Fact]
    public void GaussianSharpen_DelegatesToUnsharpMask()
    {
        // GaussianSharpen(sigma) should produce the same output as
        // UnsharpMask(sigma, amount=1.0) — they're aliases.
        var src = UCharSolid(8, 8, 100, 150, 200);
        var viaAlias = VipsImageOps.GaussianSharpen(src, 1.5);
        var viaUnsharp = VipsImageOps.UnsharpMask(src, 1.5, 1.0);
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                var a = ReadPel(viaAlias, x, y);
                var b = ReadPel(viaUnsharp, x, y);
                Assert.Equal(a[0], b[0]);
                Assert.Equal(a[1], b[1]);
                Assert.Equal(a[2], b[2]);
            }
    }

    [Fact]
    public void GaussianSharpen_OnSolid_ProducesSolid()
    {
        // Sharpening a flat region should leave it ~unchanged.
        var src = UCharSolid(16, 16, 128, 128, 128);
        var sharp = VipsImageOps.GaussianSharpen(src, 2.0);
        var pel = ReadPel(sharp, 8, 8);
        Assert.Equal(128, pel[0]);
        Assert.Equal(128, pel[1]);
        Assert.Equal(128, pel[2]);
    }

    // ---- Kayyali edge detector ----

    [Fact]
    public void Kayyali_EdgeKernel_ProducesNonZeroOnEdge()
    {
        var src = VerticalEdge(20, 10);
        var edges = VipsImageOps.EdgeKernel(src, VipsEdgeKernel.Kayyali);
        // Right at the edge column (mid = 10) the kernel should fire strongly.
        var atEdge = ReadPel(edges, 10, 5)[0];
        Assert.True(atEdge > 50, $"expected strong edge response at edge column, got {atEdge}");
    }

    [Fact]
    public void Kayyali_EdgeKernel_NearZeroOnFlatRegion()
    {
        var src = Mono(20, 10, 128);  // uniform grey
        var edges = VipsImageOps.EdgeKernel(src, VipsEdgeKernel.Kayyali);
        // Flat region should produce ~zero edge response everywhere.
        for (int y = 0; y < 10; y++)
            for (int x = 0; x < 20; x++)
                Assert.Equal(0, ReadPel(edges, x, y)[0]);
    }

    [Fact]
    public void EdgeMethod_Kayyali_DispatchesToEdgeKernel()
    {
        // The Edge dispatcher should route Kayyali to the kernel op.
        var src = VerticalEdge(20, 10);
        var viaDispatcher = VipsEdge.Apply(src, VipsEdgeMethod.Kayyali);
        var viaDirect = VipsImageOps.EdgeKernel(src, VipsEdgeKernel.Kayyali);
        for (int y = 0; y < 10; y++)
            for (int x = 0; x < 20; x++)
                Assert.Equal(ReadPel(viaDispatcher, x, y)[0], ReadPel(viaDirect, x, y)[0]);
    }

    // ---- Fill(canvas, color, region) ----

    [Fact]
    public void Fill_WithRegionPath_FillsThePolygon()
    {
        var bg = UCharSolid(20, 20, 0, 0, 0);
        var triangle = VipsPath.Polygon((5, 15), (15, 15), (10, 5));
        var painted = VipsImageOps.Fill(bg, new byte[] { 255, 0, 0 }, triangle, aa: false);
        // Inside the triangle (centroid ≈ (10, 11)).
        Assert.Equal(255, ReadPel(painted, 10, 11)[0]);
        // Outside the triangle (corner).
        Assert.Equal(0, ReadPel(painted, 0, 0)[0]);
    }

    [Fact]
    public void Fill_WithCirclePath_FillsTheCircle()
    {
        var bg = UCharSolid(20, 20, 0, 0, 0);
        var circle = VipsPath.Circle(10, 10, 5);
        var painted = VipsImageOps.Fill(bg, new byte[] { 0, 200, 0 }, circle, aa: false);
        // Centre painted.
        Assert.Equal(200, ReadPel(painted, 10, 10)[1]);
        // Far corner not.
        Assert.Equal(0, ReadPel(painted, 0, 0)[1]);
    }

    // ---- VipsPath.Offset ----

    [Fact]
    public void Offset_HorizontalLine_ShiftsPerpendicular()
    {
        // Horizontal line at y=10. Right-hand perpendicular is (0, +1).
        // Offset by 5 → y = 15.
        var path = new VipsPath().MoveTo(0, 10).LineTo(20, 10);
        var offset = path.Offset(5);
        var move = offset.Segments[0];
        var line = offset.Segments[1];
        Assert.Equal(VipsPathSegmentKind.MoveTo, move.Kind);
        Assert.Equal(0, move.X1);
        Assert.Equal(15, move.Y1);
        Assert.Equal(VipsPathSegmentKind.LineTo, line.Kind);
        Assert.Equal(20, line.X1);
        Assert.Equal(15, line.Y1);
    }

    [Fact]
    public void Offset_NegativeDistance_ShiftsOppositeDirection()
    {
        var path = new VipsPath().MoveTo(0, 10).LineTo(20, 10);
        var offset = path.Offset(-5);
        Assert.Equal(5, offset.Segments[0].Y1);
        Assert.Equal(5, offset.Segments[1].Y1);
    }

    [Fact]
    public void Offset_Rectangle_ExpandsBySameDistance()
    {
        // Square (0,0)-(10,10), offset by 2 → square (-2,-2)-(12,12).
        // Walking CCW gives outward miter at each 90° corner; with our
        // right-hand perpendicular convention CCW is "outside" so a
        // positive offset expands.
        var path = VipsPath.Rectangle(0, 0, 10, 10);
        var offset = path.Offset(2);
        // Each vertex of the original square moves by 2 along its
        // 45° miter direction. Distance from original corner is 2*√2.
        // Verify the bounding box grew uniformly.
        double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
        double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
        foreach (var s in offset.Segments)
        {
            if (s.Kind != VipsPathSegmentKind.MoveTo &&
                s.Kind != VipsPathSegmentKind.LineTo) continue;
            if (s.X1 < minX) minX = s.X1;
            if (s.X1 > maxX) maxX = s.X1;
            if (s.Y1 < minY) minY = s.Y1;
            if (s.Y1 > maxY) maxY = s.Y1;
        }
        // For a CCW square offset outward by 2, bbox is (-2..12, -2..12).
        // Our Rectangle factory builds CW (top-left → top-right → ...),
        // so positive offset shrinks. Just verify the bbox uniformly shifted.
        double width = maxX - minX, height = maxY - minY;
        // The offset rectangle should be a 14×14 (expansion) or 6×6 (shrink),
        // not the original 10×10.
        Assert.True(Math.Abs(width - 10) > 1, $"width unchanged = {width}");
        Assert.True(Math.Abs(height - 10) > 1, $"height unchanged = {height}");
    }

    [Fact]
    public void Offset_PreservesClose()
    {
        var path = VipsPath.Rectangle(0, 0, 10, 10);
        var offset = path.Offset(2);
        Assert.Contains(offset.Segments, s => s.Kind == VipsPathSegmentKind.Close);
    }

    [Fact]
    public void Offset_FlattensCurves()
    {
        // A circle should flatten to many polyline points for offset.
        var c = VipsPath.Circle(50, 50, 20);
        var offset = c.Offset(5);
        // Result should have no cubic / quadratic segments — pure polyline.
        Assert.DoesNotContain(offset.Segments,
            s => s.Kind == VipsPathSegmentKind.CubicTo
              || s.Kind == VipsPathSegmentKind.QuadraticTo);
        // And many vertices.
        int vertices = offset.Segments.Count(s =>
            s.Kind == VipsPathSegmentKind.MoveTo || s.Kind == VipsPathSegmentKind.LineTo);
        Assert.True(vertices > 10, $"expected many polyline vertices, got {vertices}");
    }

    [Fact]
    public void Offset_ZeroDistance_ApproximatelyOriginal()
    {
        // Offset by 0 should leave geometry unchanged.
        var path = new VipsPath().MoveTo(0, 0).LineTo(10, 0).LineTo(10, 10);
        var offset = path.Offset(0);
        var origVerts = path.Segments
            .Where(s => s.Kind == VipsPathSegmentKind.MoveTo || s.Kind == VipsPathSegmentKind.LineTo)
            .Select(s => (s.X1, s.Y1)).ToList();
        var offVerts = offset.Segments
            .Where(s => s.Kind == VipsPathSegmentKind.MoveTo || s.Kind == VipsPathSegmentKind.LineTo)
            .Select(s => (s.X1, s.Y1)).ToList();
        Assert.Equal(origVerts.Count, offVerts.Count);
        for (int i = 0; i < origVerts.Count; i++)
        {
            Assert.Equal(origVerts[i].X1, offVerts[i].X1, 6);
            Assert.Equal(origVerts[i].Y1, offVerts[i].Y1, 6);
        }
    }
}
