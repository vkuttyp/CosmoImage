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
public sealed partial class VipsPath
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

    /// <summary>
    /// Append an SVG-style elliptical arc segment from the current pen
    /// position to (<paramref name="x"/>, <paramref name="y"/>). Mirrors
    /// the SVG <c>A</c> path command and ImageSharp's
    /// <c>PathBuilder.AddArc</c>.
    ///
    /// <para>Internally converted to a sequence of cubic Beziers
    /// (≤90° per piece) using the standard algorithm from the SVG
    /// implementation notes. This means transforms, stroking, dashing,
    /// and clipping all work on arc segments without further code.</para>
    /// </summary>
    /// <param name="rx">Ellipse x-radius.</param>
    /// <param name="ry">Ellipse y-radius.</param>
    /// <param name="xRotationDegrees">Rotation of the ellipse's x-axis,
    /// in degrees.</param>
    /// <param name="largeArc">If <c>true</c>, picks the &gt;180° arc
    /// (the "long way" around).</param>
    /// <param name="sweep">If <c>true</c>, sweeps in the positive-angle
    /// direction (clockwise on screen with y-down).</param>
    public VipsPath ArcTo(double rx, double ry, double xRotationDegrees,
        bool largeArc, bool sweep, double x, double y)
    {
        var (x0, y0) = CurrentPoint();
        // SVG: if start == end, the arc is omitted.
        if (x0 == x && y0 == y) return this;
        // Degenerate radii → straight line.
        if (rx == 0 || ry == 0) return LineTo(x, y);

        rx = Math.Abs(rx);
        ry = Math.Abs(ry);
        double phi = xRotationDegrees * Math.PI / 180.0;
        double cosPhi = Math.Cos(phi), sinPhi = Math.Sin(phi);

        // Endpoints in the ellipse-local frame (centred on the chord
        // midpoint, axis-aligned with the ellipse).
        double dx = (x0 - x) / 2, dy = (y0 - y) / 2;
        double x1p = cosPhi * dx + sinPhi * dy;
        double y1p = -sinPhi * dx + cosPhi * dy;

        // Scale up radii if too small to span the chord.
        double lam = (x1p * x1p) / (rx * rx) + (y1p * y1p) / (ry * ry);
        if (lam > 1)
        {
            double s = Math.Sqrt(lam);
            rx *= s; ry *= s;
        }

        // Centre in local frame.
        double sign = (largeArc == sweep) ? -1 : 1;
        double num = rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p;
        double den = rx * rx * y1p * y1p + ry * ry * x1p * x1p;
        double coef = sign * Math.Sqrt(Math.Max(0, num) / den);
        double cxp = coef * (rx * y1p / ry);
        double cyp = coef * -(ry * x1p / rx);

        // Centre in user frame.
        double cx = cosPhi * cxp - sinPhi * cyp + (x0 + x) / 2;
        double cy = sinPhi * cxp + cosPhi * cyp + (y0 + y) / 2;

        // Start angle and signed sweep.
        double startVx = (x1p - cxp) / rx, startVy = (y1p - cyp) / ry;
        double endVx = (-x1p - cxp) / rx, endVy = (-y1p - cyp) / ry;
        double theta1 = AngleBetween(1, 0, startVx, startVy);
        double dtheta = AngleBetween(startVx, startVy, endVx, endVy);
        if (!sweep && dtheta > 0) dtheta -= 2 * Math.PI;
        else if (sweep && dtheta < 0) dtheta += 2 * Math.PI;

        // Split into ≤90° pieces; each becomes a cubic via the
        // standard tan(α/4) approximation. (For α=π/2 this reduces to
        // the same kappa = 0.5523 used in the Ellipse factory.)
        int n = Math.Max(1, (int)Math.Ceiling(Math.Abs(dtheta) / (Math.PI / 2)));
        double seg = dtheta / n;
        double t = 4.0 / 3.0 * Math.Tan(seg / 4);

        double a = theta1;
        for (int i = 0; i < n; i++)
        {
            double b = a + seg;
            double cosA = Math.Cos(a), sinA = Math.Sin(a);
            double cosB = Math.Cos(b), sinB = Math.Sin(b);

            // Unit-ellipse control points + endpoint.
            double c1ux = cosA - t * sinA, c1uy = sinA + t * cosA;
            double c2ux = cosB + t * sinB, c2uy = sinB - t * cosB;

            // Map unit ellipse → world: scale by (rx, ry), rotate by
            // phi, translate by (cx, cy).
            (double mx, double my) Map(double ux, double uy)
            {
                double sx = ux * rx, sy = uy * ry;
                return (cosPhi * sx - sinPhi * sy + cx,
                        sinPhi * sx + cosPhi * sy + cy);
            }

            var (c1x, c1y) = Map(c1ux, c1uy);
            var (c2x, c2y) = Map(c2ux, c2uy);
            var (ex, ey) = Map(cosB, sinB);
            CubicTo(c1x, c1y, c2x, c2y, ex, ey);
            a = b;
        }
        return this;
    }

    private static double AngleBetween(double ux, double uy, double vx, double vy)
    {
        double dot = ux * vx + uy * vy;
        double len = Math.Sqrt((ux * ux + uy * uy) * (vx * vx + vy * vy));
        double cosAng = Math.Clamp(dot / len, -1, 1);
        double a = Math.Acos(cosAng);
        return ux * vy - uy * vx < 0 ? -a : a;
    }

    /// <summary>
    /// Look up the current pen position by inspecting the most recent
    /// segment. Used by <see cref="ArcTo"/> to know where the arc
    /// should start. After a <see cref="Close"/>, the pen is at the
    /// sub-path's most recent <see cref="MoveTo"/> point.
    /// </summary>
    private (double x, double y) CurrentPoint()
    {
        for (int i = Segments.Count - 1; i >= 0; i--)
        {
            var s = Segments[i];
            switch (s.Kind)
            {
                case VipsPathSegmentKind.MoveTo: return (s.X1, s.Y1);
                case VipsPathSegmentKind.LineTo: return (s.X1, s.Y1);
                case VipsPathSegmentKind.CubicTo: return (s.X3, s.Y3);
                case VipsPathSegmentKind.QuadraticTo: return (s.X2, s.Y2);
                case VipsPathSegmentKind.Close:
                    for (int j = i - 1; j >= 0; j--)
                        if (Segments[j].Kind == VipsPathSegmentKind.MoveTo)
                            return (Segments[j].X1, Segments[j].Y1);
                    return (0, 0);
            }
        }
        return (0, 0);
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

    // ---- Affine transforms ----

    /// <summary>
    /// Apply a 2D affine transform to every coordinate in the path
    /// (endpoints and Bezier control points alike). Returns a new
    /// path; the receiver is unchanged. Matrix form:
    /// <code>
    /// x' = a · x + b · y + tx
    /// y' = c · x + d · y + ty
    /// </code>
    /// </summary>
    public VipsPath Transform(double a, double b, double c, double d, double tx, double ty)
    {
        var result = new VipsPath();
        foreach (var seg in Segments)
        {
            switch (seg.Kind)
            {
                case VipsPathSegmentKind.MoveTo:
                    result.MoveTo(a * seg.X1 + b * seg.Y1 + tx, c * seg.X1 + d * seg.Y1 + ty);
                    break;
                case VipsPathSegmentKind.LineTo:
                    result.LineTo(a * seg.X1 + b * seg.Y1 + tx, c * seg.X1 + d * seg.Y1 + ty);
                    break;
                case VipsPathSegmentKind.CubicTo:
                    result.CubicTo(
                        a * seg.X1 + b * seg.Y1 + tx, c * seg.X1 + d * seg.Y1 + ty,
                        a * seg.X2 + b * seg.Y2 + tx, c * seg.X2 + d * seg.Y2 + ty,
                        a * seg.X3 + b * seg.Y3 + tx, c * seg.X3 + d * seg.Y3 + ty);
                    break;
                case VipsPathSegmentKind.QuadraticTo:
                    result.QuadraticTo(
                        a * seg.X1 + b * seg.Y1 + tx, c * seg.X1 + d * seg.Y1 + ty,
                        a * seg.X2 + b * seg.Y2 + tx, c * seg.X2 + d * seg.Y2 + ty);
                    break;
                case VipsPathSegmentKind.Close:
                    result.Close();
                    break;
            }
        }
        return result;
    }

    /// <summary>Translate the path by (<paramref name="dx"/>, <paramref name="dy"/>).</summary>
    public VipsPath Translate(double dx, double dy)
        => Transform(1, 0, 0, 1, dx, dy);

    /// <summary>Scale about the origin.</summary>
    public VipsPath Scale(double sx, double sy)
        => Transform(sx, 0, 0, sy, 0, 0);

    /// <summary>Rotate by <paramref name="degrees"/> about the origin
    /// (positive = counter-clockwise in math coords, clockwise on
    /// screen with y-down).</summary>
    public VipsPath Rotate(double degrees)
    {
        double rad = degrees * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        return Transform(cos, -sin, sin, cos, 0, 0);
    }

    /// <summary>Rotate by <paramref name="degrees"/> about
    /// (<paramref name="cx"/>, <paramref name="cy"/>).</summary>
    public VipsPath RotateAround(double degrees, double cx, double cy)
    {
        double rad = degrees * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        // (x − cx)·cos − (y − cy)·sin + cx, etc.
        return Transform(cos, -sin, sin, cos,
            cx - cos * cx + sin * cy,
            cy - sin * cx - cos * cy);
    }
}
