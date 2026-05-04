using System;
using System.Linq;
using CosmoImage.Operations.Drawing;
using Xunit;

namespace CosmoImage.Tests;

public class Round76Tests
{
    private static double TriArea((double x, double y) a, (double x, double y) b, (double x, double y) c)
        => Math.Abs((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x)) / 2.0;

    private static double TotalArea(System.Collections.Generic.List<(double x, double y)> tris)
    {
        double total = 0;
        for (int i = 0; i + 2 < tris.Count; i += 3)
            total += TriArea(tris[i], tris[i + 1], tris[i + 2]);
        return total;
    }

    private static int TriangleCount(System.Collections.Generic.List<(double x, double y)> tris)
        => tris.Count / 3;

    // ---- Counts ----

    [Fact]
    public void Triangle_TessellatesToOneTriangle()
    {
        var tri = VipsPath.Polygon((0, 0), (10, 0), (5, 10));
        var t = tri.Tessellate();
        Assert.Equal(1, TriangleCount(t));
        Assert.Equal(3, t.Count);
    }

    [Fact]
    public void Rectangle_TessellatesToTwoTriangles()
    {
        var r = VipsPath.Rectangle(0, 0, 10, 10);
        var t = r.Tessellate();
        Assert.Equal(2, TriangleCount(t));
    }

    [Fact]
    public void Pentagon_TessellatesToThreeTriangles()
    {
        // Regular pentagon: N-2 = 3 triangles.
        var p = VipsPath.RegularPolygon(50, 50, 5, 30);
        var t = p.Tessellate();
        Assert.Equal(3, TriangleCount(t));
    }

    [Fact]
    public void Octagon_TessellatesToSixTriangles()
    {
        var p = VipsPath.RegularPolygon(50, 50, 8, 30);
        var t = p.Tessellate();
        Assert.Equal(6, TriangleCount(t));
    }

    // ---- Area conservation ----

    [Fact]
    public void RectangleTessellation_TotalAreaMatchesRectangle()
    {
        var r = VipsPath.Rectangle(0, 0, 10, 20);
        var t = r.Tessellate();
        Assert.Equal(200, TotalArea(t), 6);
    }

    [Fact]
    public void RegularPolygon_TessellationAreaMatchesGeometricArea()
    {
        // Hexagon area = (3√3 / 2) · r²
        double r = 30;
        var hex = VipsPath.RegularPolygon(50, 50, 6, r);
        var t = hex.Tessellate();
        double expected = 3 * Math.Sqrt(3) / 2 * r * r;
        double actual = TotalArea(t);
        Assert.Equal(expected, actual, 1);
    }

    // ---- Concave polygon ----

    [Fact]
    public void ConcavePolygon_StarTessellatesCorrectly()
    {
        // 5-pointed star: 10 vertices; ear-clipping should produce 8 triangles.
        var star = VipsPath.Star(50, 50, 5, 10, 30);
        var t = star.Tessellate();
        Assert.Equal(8, TriangleCount(t));
        // Total triangle area should be positive and reasonable.
        double area = TotalArea(t);
        Assert.True(area > 0, $"star area should be positive, got {area}");
    }

    // ---- Curves are flattened first ----

    [Fact]
    public void Circle_TessellatesToManyTriangles()
    {
        // Circle is 4 cubic Beziers — flattens to many polyline vertices,
        // tessellating to ~ N - 2 triangles.
        var c = VipsPath.Circle(50, 50, 30);
        var t = c.Tessellate();
        // Should be many triangles (typically 30+ depending on flatten resolution).
        Assert.True(TriangleCount(t) > 10,
            $"circle should produce many triangles, got {TriangleCount(t)}");
        // Area approximates π·r²
        double expected = Math.PI * 30 * 30;
        double actual = TotalArea(t);
        Assert.True(Math.Abs(actual - expected) / expected < 0.05,
            $"circle area should be close to π·r²: expected {expected:F1}, got {actual:F1}");
    }

    // ---- Open sub-paths skipped ----

    [Fact]
    public void OpenSubpath_NotTessellated()
    {
        // Open polyline (no Close) — should produce no triangles.
        var open = new VipsPath().MoveTo(0, 0).LineTo(10, 0).LineTo(5, 10);
        var t = open.Tessellate();
        Assert.Empty(t);
    }

    // ---- Edge cases ----

    [Fact]
    public void TooFewVertices_ReturnsEmpty()
    {
        // Two vertices and Close — ear-clipping needs ≥ 3.
        var p = new VipsPath().MoveTo(0, 0).LineTo(10, 0).Close();
        var t = p.Tessellate();
        Assert.Empty(t);
    }

    [Fact]
    public void TriangleVertices_AppearInResult()
    {
        // For a triangle, the 3 output vertices should match the 3 input vertices.
        var tri = VipsPath.Polygon((0, 0), (10, 0), (5, 10));
        var t = tri.Tessellate();
        var inputSet = new[] { (0.0, 0.0), (10.0, 0.0), (5.0, 10.0) };
        Assert.All(inputSet, v => Assert.Contains(v, t));
    }
}
