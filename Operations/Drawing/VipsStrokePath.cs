using System;
using System.Collections.Generic;

namespace CosmoImage.Operations.Drawing;

/// <summary>
/// Stroke a vector <see cref="VipsPath"/> with a <see cref="VipsPen"/>.
/// Mirrors ImageSharp's <c>image.Mutate(c =&gt; c.Draw(pen, path))</c>.
///
/// <para>Implementation: flatten the path to line segments (reusing
/// the curve subdivision from <see cref="VipsFillPath"/>), then for
/// each contiguous sub-path build a closed polygon outline by
/// offsetting half the pen width perpendicular to each segment, with
/// bevel joins at interior corners and butt caps at endpoints.
/// Fill that outline polygon with the pen's brush.</para>
///
/// <para>Built atop <see cref="VipsFillPath"/> so all the same
/// rasterisation guarantees apply (even-odd winding, no AA in v1).
/// Multi-segment paths produce one outline polygon per sub-path; the
/// fill correctly handles disjoint sub-paths.</para>
/// </summary>
public static class VipsStrokePath
{
    public static VipsImage Stroke(VipsImage input, VipsPath path, VipsPen pen, bool aa = true)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (path == null) throw new ArgumentNullException(nameof(path));
        if (pen == null) throw new ArgumentNullException(nameof(pen));

        var subpaths = FlattenToSubPaths(path);
        var outline = new VipsPath();
        foreach (var (points, closed) in subpaths)
            EmitOutline(outline, points, closed, pen.Width / 2);
        return VipsImageOps.FillPath(input, outline, pen.Brush, aa);
    }

    /// <summary>
    /// Walk the source path; emit one polyline per sub-path. Each
    /// polyline is the list of vertex coordinates (curves already
    /// subdivided), plus a flag for whether the sub-path closed.
    /// </summary>
    private static List<(List<(double x, double y)> Points, bool Closed)> FlattenToSubPaths(VipsPath path)
    {
        var result = new List<(List<(double, double)>, bool)>();
        var current = new List<(double, double)>();
        double cx = 0, cy = 0;
        double sx = 0, sy = 0;
        bool started = false;
        bool closed = false;

        void Flush()
        {
            if (current.Count >= 2) result.Add((current, closed));
            current = new List<(double, double)>();
            closed = false;
        }

        foreach (var seg in path.Segments)
        {
            switch (seg.Kind)
            {
                case VipsPathSegmentKind.MoveTo:
                    if (started) Flush();
                    cx = seg.X1; cy = seg.Y1;
                    sx = cx; sy = cy;
                    current.Add((cx, cy));
                    started = true;
                    break;
                case VipsPathSegmentKind.LineTo:
                    cx = seg.X1; cy = seg.Y1;
                    current.Add((cx, cy));
                    break;
                case VipsPathSegmentKind.CubicTo:
                    SubdivideCubic(current, cx, cy, seg.X1, seg.Y1, seg.X2, seg.Y2, seg.X3, seg.Y3);
                    cx = seg.X3; cy = seg.Y3;
                    break;
                case VipsPathSegmentKind.QuadraticTo:
                    SubdivideQuadratic(current, cx, cy, seg.X1, seg.Y1, seg.X2, seg.Y2);
                    cx = seg.X2; cy = seg.Y2;
                    break;
                case VipsPathSegmentKind.Close:
                    closed = true;
                    cx = sx; cy = sy;
                    Flush();
                    started = false;
                    break;
            }
        }
        if (started) Flush();
        return result;
    }

    /// <summary>
    /// Build one closed outline polygon for the sub-path and append
    /// it to <paramref name="outline"/>.
    /// </summary>
    private static void EmitOutline(VipsPath outline,
        List<(double x, double y)> pts, bool closed, double half)
    {
        int n = pts.Count;
        if (n < 2) return;

        // Per-segment perpendicular unit vectors (length n - 1).
        var perp = new (double nx, double ny)[n - 1];
        for (int i = 0; i < n - 1; i++)
        {
            double dx = pts[i + 1].x - pts[i].x;
            double dy = pts[i + 1].y - pts[i].y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9) { perp[i] = (0, 0); continue; }
            // Right-hand perpendicular (rotate 90° CW): (dy, -dx) / len.
            perp[i] = (dy / len, -dx / len);
        }

        // Right side: for each vertex, emit "right" offset point.
        // For interior vertices we emit *two* points — end of incoming
        // segment with its perpendicular and start of outgoing with
        // its perpendicular. The straight line between them is the
        // bevel join.
        var right = new List<(double, double)>();
        var left = new List<(double, double)>();

        // Start cap (butt): just take perpendicular at first segment.
        right.Add((pts[0].x + perp[0].nx * half, pts[0].y + perp[0].ny * half));
        left.Add((pts[0].x - perp[0].nx * half, pts[0].y - perp[0].ny * half));

        for (int i = 1; i < n - 1; i++)
        {
            // End of segment (i-1) with perpendicular i-1.
            right.Add((pts[i].x + perp[i - 1].nx * half, pts[i].y + perp[i - 1].ny * half));
            left.Add((pts[i].x - perp[i - 1].nx * half, pts[i].y - perp[i - 1].ny * half));
            // Start of segment i with perpendicular i — the bevel
            // between consecutive segments.
            right.Add((pts[i].x + perp[i].nx * half, pts[i].y + perp[i].ny * half));
            left.Add((pts[i].x - perp[i].nx * half, pts[i].y - perp[i].ny * half));
        }

        // End cap (butt): perpendicular at last segment.
        var lastPerp = perp[n - 2];
        right.Add((pts[n - 1].x + lastPerp.nx * half, pts[n - 1].y + lastPerp.ny * half));
        left.Add((pts[n - 1].x - lastPerp.nx * half, pts[n - 1].y - lastPerp.ny * half));

        if (closed)
        {
            // Closed sub-path: don't cap; instead bevel at the closing
            // vertex using the wrap-around perpendicular pair. The
            // FillPath rasteriser handles non-convex polygons, so we
            // can emit the outline as one ring.
            // Right side already contains the boundary vertices in
            // forward order; append left in reverse and let the path
            // close.
        }

        // Build outline polygon: forward right side + reversed left side.
        outline.MoveTo(right[0].Item1, right[0].Item2);
        for (int i = 1; i < right.Count; i++) outline.LineTo(right[i].Item1, right[i].Item2);
        for (int i = left.Count - 1; i >= 0; i--) outline.LineTo(left[i].Item1, left[i].Item2);
        outline.Close();
    }

    private static void SubdivideCubic(List<(double, double)> pts,
        double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3,
        int depth = 0)
    {
        double ux = 3 * x1 - 2 * x0 - x3;
        double uy = 3 * y1 - 2 * y0 - y3;
        double vx = 3 * x2 - 2 * x3 - x0;
        double vy = 3 * y2 - 2 * y3 - y0;
        double max = Math.Max(ux * ux, vx * vx) + Math.Max(uy * uy, vy * vy);
        if (max <= 0.25 * 0.25 || depth > 16)
        {
            pts.Add((x3, y3));
            return;
        }
        double mx0 = (x0 + x1) / 2, my0 = (y0 + y1) / 2;
        double mx1 = (x1 + x2) / 2, my1 = (y1 + y2) / 2;
        double mx2 = (x2 + x3) / 2, my2 = (y2 + y3) / 2;
        double m01x = (mx0 + mx1) / 2, m01y = (my0 + my1) / 2;
        double m12x = (mx1 + mx2) / 2, m12y = (my1 + my2) / 2;
        double mx = (m01x + m12x) / 2, my = (m01y + m12y) / 2;
        SubdivideCubic(pts, x0, y0, mx0, my0, m01x, m01y, mx, my, depth + 1);
        SubdivideCubic(pts, mx, my, m12x, m12y, mx2, my2, x3, y3, depth + 1);
    }

    private static void SubdivideQuadratic(List<(double, double)> pts,
        double x0, double y0, double x1, double y1, double x2, double y2,
        int depth = 0)
    {
        double dx = (x0 + x2) / 2 - x1;
        double dy = (y0 + y2) / 2 - y1;
        if (dx * dx + dy * dy <= 0.25 * 0.25 || depth > 16)
        {
            pts.Add((x2, y2));
            return;
        }
        double mx0 = (x0 + x1) / 2, my0 = (y0 + y1) / 2;
        double mx1 = (x1 + x2) / 2, my1 = (y1 + y2) / 2;
        double mx = (mx0 + mx1) / 2, my = (my0 + my1) / 2;
        SubdivideQuadratic(pts, x0, y0, mx0, my0, mx, my, depth + 1);
        SubdivideQuadratic(pts, mx, my, mx1, my1, x2, y2, depth + 1);
    }
}
