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
        {
            if (pen.Dashes != null)
            {
                // Walk arc-length, split into "on" sub-pieces, stroke
                // each as a fresh open sub-path with caps.
                foreach (var piece in DashSplit(points, closed, pen.Dashes, pen.DashOffset))
                    EmitOutline(outline, piece, closed: false, pen);
            }
            else
            {
                EmitOutline(outline, points, closed, pen);
            }
        }
        return VipsImageOps.FillPath(input, outline, pen.Brush, aa);
    }

    /// <summary>
    /// Walk the polyline by arc-length, slicing it where the dash
    /// cycle transitions on→off / off→on. Returns one polyline per
    /// "on" interval. For closed input the cycle continues across
    /// the closing edge from <c>points[^1]</c> back to
    /// <c>points[0]</c>.
    /// </summary>
    private static IEnumerable<List<(double x, double y)>> DashSplit(
        List<(double x, double y)> points, bool closed,
        double[] dashes, double dashOffset)
    {
        // Build the walk polyline (with the closing edge if applicable).
        var walk = new List<(double x, double y)>(points);
        if (closed) walk.Add(points[0]);

        // Sum of dash cycle.
        double cycle = 0;
        foreach (var d in dashes) cycle += d;

        // Initial state — phase the offset into the cycle.
        double phase = dashOffset % cycle;
        if (phase < 0) phase += cycle;
        // Find which dash element we're starting in, and how much
        // distance is left in it.
        int dashIdx = 0;
        double consumedInCycle = 0;
        while (consumedInCycle + dashes[dashIdx] <= phase + 1e-12)
        {
            consumedInCycle += dashes[dashIdx];
            dashIdx = (dashIdx + 1) % dashes.Length;
        }
        double distLeft = (consumedInCycle + dashes[dashIdx]) - phase;
        bool inOn = (dashIdx % 2) == 0;

        var current = new List<(double x, double y)>();
        if (inOn) current.Add(walk[0]);

        for (int i = 0; i < walk.Count - 1; i++)
        {
            double sx = walk[i].x, sy = walk[i].y;
            double ex = walk[i + 1].x, ey = walk[i + 1].y;
            double dx = ex - sx, dy = ey - sy;
            double segLen = Math.Sqrt(dx * dx + dy * dy);
            if (segLen < 1e-12) continue;
            double consumed = 0;

            while (consumed < segLen)
            {
                double available = segLen - consumed;
                if (distLeft >= available - 1e-12)
                {
                    // Whole remainder of this segment fits in the
                    // current dash element.
                    if (inOn) current.Add(walk[i + 1]);
                    distLeft -= available;
                    consumed = segLen;
                }
                else
                {
                    // Dash boundary lands inside this segment.
                    double t = (consumed + distLeft) / segLen;
                    double bx = sx + dx * t;
                    double by = sy + dy * t;
                    if (inOn)
                    {
                        current.Add((bx, by));
                        if (current.Count >= 2) yield return current;
                        current = new List<(double, double)>();
                    }
                    else
                    {
                        // Off → on transition: start a fresh polyline
                        // at the boundary point.
                        current.Add((bx, by));
                    }
                    consumed += distLeft;
                    // Advance to next dash element.
                    dashIdx = (dashIdx + 1) % dashes.Length;
                    inOn = !inOn;
                    distLeft = dashes[dashIdx];
                    // Skip zero-length dash entries (they cycle
                    // through immediately).
                    while (distLeft <= 1e-12)
                    {
                        dashIdx = (dashIdx + 1) % dashes.Length;
                        inOn = !inOn;
                        distLeft = dashes[dashIdx];
                    }
                }
            }
        }
        if (inOn && current.Count >= 2) yield return current;
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
    /// it to <paramref name="outline"/>. Honours the pen's cap, join
    /// and miter-limit settings.
    /// </summary>
    private static void EmitOutline(VipsPath outline,
        List<(double x, double y)> pts, bool closed, VipsPen pen)
    {
        int n = pts.Count;
        if (n < 2) return;
        double half = pen.Width / 2;

        // Per-segment forward unit vector and its right-hand perpendicular.
        var fwd = new (double dx, double dy)[n - 1];
        var perp = new (double nx, double ny)[n - 1];
        for (int i = 0; i < n - 1; i++)
        {
            double dx = pts[i + 1].x - pts[i].x;
            double dy = pts[i + 1].y - pts[i].y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9) { fwd[i] = (0, 0); perp[i] = (0, 0); continue; }
            fwd[i] = (dx / len, dy / len);
            perp[i] = (dy / len, -dx / len); // right-hand perpendicular
        }

        var right = new List<(double, double)>();
        var left = new List<(double, double)>();

        // Start cap.
        EmitStartCap(right, left, pts[0], fwd[0], perp[0], half, pen.Cap);

        // Interior joins.
        for (int i = 1; i < n - 1; i++)
            EmitJoin(right, left, pts[i], perp[i - 1], perp[i], fwd[i - 1], fwd[i],
                half, pen.Join, pen.MiterLimit);

        // End cap.
        EmitEndCap(right, left, pts[n - 1], fwd[n - 2], perp[n - 2], half, pen.Cap);

        // Build outline polygon: forward right + reversed left.
        outline.MoveTo(right[0].Item1, right[0].Item2);
        for (int i = 1; i < right.Count; i++) outline.LineTo(right[i].Item1, right[i].Item2);
        for (int i = left.Count - 1; i >= 0; i--) outline.LineTo(left[i].Item1, left[i].Item2);
        outline.Close();
    }

    private static void EmitStartCap(List<(double, double)> right, List<(double, double)> left,
        (double x, double y) p, (double dx, double dy) fwd, (double nx, double ny) perp,
        double half, VipsLineCap cap)
    {
        // Right and left base points (perpendicular offset at the endpoint).
        var rB = (p.x + perp.nx * half, p.y + perp.ny * half);
        var lB = (p.x - perp.nx * half, p.y - perp.ny * half);
        switch (cap)
        {
            case VipsLineCap.Butt:
                right.Add(rB);
                left.Add(lB);
                break;
            case VipsLineCap.Square:
                // Extend back along -fwd, then perpendicular.
                double bx = p.x - fwd.dx * half;
                double by = p.y - fwd.dy * half;
                right.Add((bx + perp.nx * half, by + perp.ny * half));
                left.Add((bx - perp.nx * half, by - perp.ny * half));
                break;
            case VipsLineCap.Round:
                // Emit a half-circle of points from left-base around (-fwd) back to right-base.
                // Tessellate with ~16 segments per half-turn.
                EmitRoundCap(right, left, p, fwd, perp, half, isStart: true);
                break;
        }
    }

    private static void EmitEndCap(List<(double, double)> right, List<(double, double)> left,
        (double x, double y) p, (double dx, double dy) fwd, (double nx, double ny) perp,
        double half, VipsLineCap cap)
    {
        var rB = (p.x + perp.nx * half, p.y + perp.ny * half);
        var lB = (p.x - perp.nx * half, p.y - perp.ny * half);
        switch (cap)
        {
            case VipsLineCap.Butt:
                right.Add(rB);
                left.Add(lB);
                break;
            case VipsLineCap.Square:
                double bx = p.x + fwd.dx * half;
                double by = p.y + fwd.dy * half;
                right.Add((bx + perp.nx * half, by + perp.ny * half));
                left.Add((bx - perp.nx * half, by - perp.ny * half));
                break;
            case VipsLineCap.Round:
                EmitRoundCap(right, left, p, fwd, perp, half, isStart: false);
                break;
        }
    }

    /// <summary>
    /// Emit a round end-cap. For start caps, we generate points
    /// along the half-circle from left-base, sweeping past the back
    /// (-fwd) of the endpoint, ending at right-base. For end caps,
    /// the arc sweeps from right-base past +fwd to left-base. ~16
    /// segments per half-turn keeps the arc visually smooth.
    /// </summary>
    private static void EmitRoundCap(List<(double, double)> right, List<(double, double)> left,
        (double x, double y) p, (double dx, double dy) fwd, (double nx, double ny) perp,
        double half, bool isStart)
    {
        const int n = 16;
        // Append the start-cap arc as right-side points (the arc
        // becomes part of the outline polygon); the actual left-side
        // base point is added separately.
        // The arc parameter t goes 0..1; angle goes from 0 to π.
        // At t=0 we're at the right-base direction; at t=1 we're at
        // the left-base direction; mid-arc bulges in the (-fwd) or
        // (+fwd) direction depending on isStart.
        if (isStart)
        {
            // Start cap: right side gets just the right-base; the
            // arc itself is appended to LEFT side (so when the
            // outline is later built as right-forward + left-reverse,
            // the arc shows up between left-base and right-base).
            // Actually simpler: emit arc points to a single side and
            // let the polygon close. We append the arc going from
            // right-base, sweeping back through -fwd, to left-base,
            // and put those points on the right side; on the left
            // side we just put left-base. The polygon body wraps
            // around correctly.
            for (int i = 0; i <= n; i++)
            {
                double t = i / (double)n;
                double a = Math.PI * t;            // 0..π
                double cos = Math.Cos(a);
                double sin = Math.Sin(a);
                // At t=0: cos=1, sin=0 → right-base (perp direction).
                // At t=0.5: cos=0, sin=1 → bulges in -fwd direction.
                // At t=1: cos=-1, sin=0 → left-base.
                double rx = perp.nx * cos - fwd.dx * sin;
                double ry = perp.ny * cos - fwd.dy * sin;
                right.Add((p.x + rx * half, p.y + ry * half));
            }
            // Add nothing to left — the arc above already spans both sides.
            // Place left base equal to last arc point so the polygon
            // continues cleanly.
            left.Add((p.x - perp.nx * half, p.y - perp.ny * half));
            // Remove the last right entry so it doesn't duplicate left-base.
            right.RemoveAt(right.Count - 1);
        }
        else
        {
            // End cap: emit an arc from right-base, sweeping through
            // +fwd, ending at left-base. Append to right side.
            for (int i = 0; i <= n; i++)
            {
                double t = i / (double)n;
                double a = Math.PI * t;
                double cos = Math.Cos(a);
                double sin = Math.Sin(a);
                double rx = perp.nx * cos + fwd.dx * sin;
                double ry = perp.ny * cos + fwd.dy * sin;
                right.Add((p.x + rx * half, p.y + ry * half));
            }
            left.Add((p.x - perp.nx * half, p.y - perp.ny * half));
            right.RemoveAt(right.Count - 1);
        }
    }

    /// <summary>
    /// Emit join points at an interior vertex. For Bevel, both
    /// offset points are added and FillPath bridges them with a
    /// straight line. For Miter, the outer-side intersection is
    /// computed; if it exceeds the miter limit, we fall back to
    /// Bevel. For Round, we emit an arc on the outer side.
    /// </summary>
    private static void EmitJoin(List<(double, double)> right, List<(double, double)> left,
        (double x, double y) p,
        (double nx, double ny) p0, (double nx, double ny) p1,
        (double dx, double dy) f0, (double dx, double dy) f1,
        double half, VipsLineJoin join, double miterLimit)
    {
        // The "incoming" offset points (end of previous segment).
        var rIn = (p.x + p0.nx * half, p.y + p0.ny * half);
        var lIn = (p.x - p0.nx * half, p.y - p0.ny * half);
        // The "outgoing" offset points (start of next segment).
        var rOut = (p.x + p1.nx * half, p.y + p1.ny * half);
        var lOut = (p.x - p1.nx * half, p.y - p1.ny * half);

        // Determine which side is "outside" of the bend — that's
        // where the join geometry is needed. With screen-y growing
        // downward and our right-hand perpendicular = (dy, -dx),
        // positive fwd-cross means the path turns clockwise on the
        // screen → outside is on the right (in the +perp direction).
        // Negative cross → outside on the left.
        double cross = f0.dx * f1.dy - f0.dy * f1.dx;
        bool rightOutside = cross > 0;

        if (join == VipsLineJoin.Bevel)
        {
            right.Add(rIn); right.Add(rOut);
            left.Add(lIn); left.Add(lOut);
            return;
        }

        if (join == VipsLineJoin.Miter)
        {
            // Compute miter point on outer side: intersection of
            // (rIn + t·f0) and (rOut - s·f1) — the two half-lines
            // along the outgoing forward direction from each offset.
            // Equivalent compact formula: M = P + (n0 + n1)·half /
            // (1 + n0·n1).
            double dot = p0.nx * p1.nx + p0.ny * p1.ny;
            double denom = 1 + dot;
            if (Math.Abs(denom) < 1e-9)
            {
                // 180° join — degenerate; fall back to bevel.
                right.Add(rIn); right.Add(rOut);
                left.Add(lIn); left.Add(lOut);
                return;
            }
            double mxR = p.x + (p0.nx + p1.nx) * half / denom;
            double myR = p.y + (p0.ny + p1.ny) * half / denom;
            double mxL = p.x - (p0.nx + p1.nx) * half / denom;
            double myL = p.y - (p0.ny + p1.ny) * half / denom;

            // Miter limit: |M - P| / half ≤ miterLimit. If exceeded,
            // bevel.
            double dx = mxR - p.x, dy = myR - p.y;
            double miterDist = Math.Sqrt(dx * dx + dy * dy);
            if (miterDist > miterLimit * half)
            {
                right.Add(rIn); right.Add(rOut);
                left.Add(lIn); left.Add(lOut);
                return;
            }

            // Use miter on the outside, bevel on the inside.
            if (rightOutside)
            {
                right.Add((mxR, myR));
                left.Add(lIn); left.Add(lOut);
            }
            else
            {
                right.Add(rIn); right.Add(rOut);
                left.Add((mxL, myL));
            }
            return;
        }

        // Round join: arc on the outside, bevel-ish on the inside.
        if (join == VipsLineJoin.Round)
        {
            const int arcSteps = 8;
            // Compute the angle between the two perpendicular
            // vectors (interior angle of the bend on the outside).
            double dot = p0.nx * p1.nx + p0.ny * p1.ny;
            dot = Math.Clamp(dot, -1, 1);
            double angle = Math.Acos(dot);
            int steps = Math.Max(1, (int)Math.Ceiling(angle * arcSteps / Math.PI));

            if (rightOutside)
            {
                // Arc from rIn to rOut on the right side.
                for (int s = 0; s <= steps; s++)
                {
                    double t = s / (double)steps;
                    double ang = (1 - t) * Math.Atan2(p0.ny, p0.nx) + t * Math.Atan2(p1.ny, p1.nx);
                    // Interpolating angles in this naïve way works
                    // when the swept angle is < π (the typical case
                    // for stroke joins). For larger sweeps the arc
                    // would go the wrong way; bevel fall-back covers
                    // the degenerate ≈180° case earlier.
                    right.Add((p.x + Math.Cos(ang) * half, p.y + Math.Sin(ang) * half));
                }
                left.Add(lIn); left.Add(lOut);
            }
            else
            {
                for (int s = 0; s <= steps; s++)
                {
                    double t = s / (double)steps;
                    double ang = (1 - t) * Math.Atan2(-p0.ny, -p0.nx) + t * Math.Atan2(-p1.ny, -p1.nx);
                    left.Add((p.x + Math.Cos(ang) * half, p.y + Math.Sin(ang) * half));
                }
                right.Add(rIn); right.Add(rOut);
            }
        }
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
