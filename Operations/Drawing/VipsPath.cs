using System;
using System.Collections.Generic;

namespace CosmoImage.Operations.Drawing;

public enum VipsPathSegmentKind
{
    MoveTo = 0,
    LineTo = 1,
    /// <summary>Cubic Bezier — two control points + endpoint.</summary>
    CubicTo = 2,
    /// <summary>Quadratic Bezier — one control point + endpoint.</summary>
    QuadraticTo = 3,
    /// <summary>Close the current sub-path back to the most recent MoveTo point.</summary>
    Close = 4,
}

/// <summary>
/// Single segment in a <see cref="VipsPath"/>. The shape is fixed by
/// <see cref="Kind"/>; extra fields hold control points or endpoint
/// coordinates as appropriate. Cheap value-type so a path is a flat
/// <see cref="List{VipsPathSegment}"/>.
/// </summary>
public readonly record struct VipsPathSegment(
    VipsPathSegmentKind Kind,
    double X1, double Y1,
    double X2 = 0, double Y2 = 0,
    double X3 = 0, double Y3 = 0);

/// <summary>
/// Builder for vector paths — sequence of move / line / Bezier / close
/// segments. Mirrors <c>SixLabors.ImageSharp.Drawing.PathBuilder</c>.
///
/// <para>Build a path with the fluent API; pass it to
/// <see cref="VipsImageOps.FillPath"/> with a brush. Curves are
/// flattened to line segments at fill time via recursive subdivision
/// — no need to specify a flatness tolerance up-front.</para>
///
/// <para>Common shapes are available as static factories:
/// <see cref="Rectangle"/>, <see cref="Polygon"/>, <see cref="Circle"/>,
/// <see cref="Ellipse"/>, <see cref="RegularPolygon"/>,
/// <see cref="Star"/>.</para>
/// </summary>
public sealed class VipsPath
{
    internal readonly List<VipsPathSegment> Segments = new();

    public VipsPath MoveTo(double x, double y)
    {
        Segments.Add(new VipsPathSegment(VipsPathSegmentKind.MoveTo, x, y));
        return this;
    }

    public VipsPath LineTo(double x, double y)
    {
        Segments.Add(new VipsPathSegment(VipsPathSegmentKind.LineTo, x, y));
        return this;
    }

    public VipsPath CubicTo(double cx1, double cy1, double cx2, double cy2, double x, double y)
    {
        Segments.Add(new VipsPathSegment(VipsPathSegmentKind.CubicTo, cx1, cy1, cx2, cy2, x, y));
        return this;
    }

    public VipsPath QuadraticTo(double cx, double cy, double x, double y)
    {
        Segments.Add(new VipsPathSegment(VipsPathSegmentKind.QuadraticTo, cx, cy, x, y));
        return this;
    }

    public VipsPath Close()
    {
        Segments.Add(new VipsPathSegment(VipsPathSegmentKind.Close, 0, 0));
        return this;
    }

    // ---- Shape factories ----

    public static VipsPath Rectangle(double x, double y, double w, double h)
        => new VipsPath().MoveTo(x, y).LineTo(x + w, y).LineTo(x + w, y + h).LineTo(x, y + h).Close();

    public static VipsPath Polygon(params (double x, double y)[] points)
    {
        if (points.Length < 3) throw new ArgumentException("Polygon needs ≥ 3 points");
        var p = new VipsPath().MoveTo(points[0].x, points[0].y);
        for (int i = 1; i < points.Length; i++) p.LineTo(points[i].x, points[i].y);
        return p.Close();
    }

    /// <summary>
    /// Circle approximated by 4 cubic Bezier arcs (the standard
    /// <c>k = 0.5522847498</c> kappa approximation; visually
    /// indistinguishable from a true circle).
    /// </summary>
    public static VipsPath Circle(double cx, double cy, double r)
        => Ellipse(cx, cy, r, r);

    public static VipsPath Ellipse(double cx, double cy, double rx, double ry)
    {
        const double k = 0.5522847498307933; // 4·(√2−1)/3
        double kx = k * rx, ky = k * ry;
        return new VipsPath()
            .MoveTo(cx + rx, cy)
            .CubicTo(cx + rx, cy + ky, cx + kx, cy + ry, cx, cy + ry)
            .CubicTo(cx - kx, cy + ry, cx - rx, cy + ky, cx - rx, cy)
            .CubicTo(cx - rx, cy - ky, cx - kx, cy - ry, cx, cy - ry)
            .CubicTo(cx + kx, cy - ry, cx + rx, cy - ky, cx + rx, cy)
            .Close();
    }

    /// <summary>
    /// Regular N-gon centred at <c>(cx, cy)</c>, vertex 0 at the top.
    /// </summary>
    public static VipsPath RegularPolygon(double cx, double cy, int vertices, double radius)
    {
        if (vertices < 3) throw new ArgumentException("Need ≥ 3 vertices");
        var p = new VipsPath();
        for (int i = 0; i < vertices; i++)
        {
            double a = -Math.PI / 2 + 2 * Math.PI * i / vertices;
            double x = cx + radius * Math.Cos(a);
            double y = cy + radius * Math.Sin(a);
            if (i == 0) p.MoveTo(x, y); else p.LineTo(x, y);
        }
        return p.Close();
    }

    /// <summary>
    /// N-pointed star: alternate outer-radius and inner-radius
    /// vertices around <c>(cx, cy)</c>.
    /// </summary>
    public static VipsPath Star(double cx, double cy, int points, double innerRadius, double outerRadius)
    {
        if (points < 3) throw new ArgumentException("Need ≥ 3 points");
        var p = new VipsPath();
        int total = points * 2;
        for (int i = 0; i < total; i++)
        {
            double r = (i & 1) == 0 ? outerRadius : innerRadius;
            double a = -Math.PI / 2 + 2 * Math.PI * i / total;
            double x = cx + r * Math.Cos(a);
            double y = cy + r * Math.Sin(a);
            if (i == 0) p.MoveTo(x, y); else p.LineTo(x, y);
        }
        return p.Close();
    }
}
