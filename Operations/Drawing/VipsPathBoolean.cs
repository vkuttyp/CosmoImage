using System;
using System.Collections.Generic;

namespace CosmoImage.Operations.Drawing;

/// <summary>
/// Path-vs-path boolean operations. Mirrors ImageSharp's
/// <c>IPath.Clip(other)</c> family.
/// </summary>
public enum VipsBooleanOp
{
    /// <summary>Region covered by BOTH subject AND clip.</summary>
    Intersect = 0,
    /// <summary>Region covered by EITHER subject OR clip.</summary>
    Union = 1,
    /// <summary>Region in subject but NOT in clip (subject minus clip).</summary>
    Subtract = 2,
}

public sealed partial class VipsPath
{
    /// <summary>
    /// Region covered by both <c>this</c> and <paramref name="other"/>.
    /// Curves are flattened first; result is a polyline path.
    /// </summary>
    public VipsPath Intersect(VipsPath other) => Boolean(this, other, VipsBooleanOp.Intersect);

    /// <summary>
    /// Region covered by either <c>this</c> or <paramref name="other"/>.
    /// Curves are flattened first; result is a polyline path.
    /// </summary>
    public VipsPath Union(VipsPath other) => Boolean(this, other, VipsBooleanOp.Union);

    /// <summary>
    /// Region in <c>this</c> but not in <paramref name="other"/>
    /// (i.e., <c>this − other</c>). Curves are flattened first.
    /// </summary>
    public VipsPath Subtract(VipsPath other) => Boolean(this, other, VipsBooleanOp.Subtract);

    /// <summary>
    /// Polygon-vs-polygon boolean using Greiner-Hormann clipping.
    /// Inputs must each be a single closed sub-path (Bezier curves
    /// allowed — they're flattened to polylines first). Vertex-on-edge
    /// and coincident-edge degeneracies are not handled — keep inputs
    /// in general position.
    /// </summary>
    public static VipsPath Boolean(VipsPath subject, VipsPath clip, VipsBooleanOp op)
    {
        var subjPolys = FlattenToPolygons(subject);
        var clipPolys = FlattenToPolygons(clip);
        if (subjPolys.Count != 1)
            throw new ArgumentException("Boolean: subject must be a single closed sub-path", nameof(subject));
        if (clipPolys.Count != 1)
            throw new ArgumentException("Boolean: clip must be a single closed sub-path", nameof(clip));

        var results = GreinerHormann.Clip(subjPolys[0], clipPolys[0], op);
        var path = new VipsPath();
        foreach (var poly in results)
        {
            if (poly.Count < 3) continue;
            path.MoveTo(poly[0].x, poly[0].y);
            for (int i = 1; i < poly.Count; i++) path.LineTo(poly[i].x, poly[i].y);
            path.Close();
        }
        return path;
    }

    /// <summary>
    /// Flatten a path into a list of closed polygon vertex loops.
    /// Bezier segments are subdivided to 0.25-px tolerance.
    /// </summary>
    private static List<List<(double x, double y)>> FlattenToPolygons(VipsPath path)
    {
        var result = new List<List<(double x, double y)>>();
        var current = new List<(double x, double y)>();
        double cx = 0, cy = 0;
        bool started = false;

        void EndSubpath()
        {
            if (current.Count >= 2)
            {
                if (Math.Abs(current[0].x - current[^1].x) < 1e-9 &&
                    Math.Abs(current[0].y - current[^1].y) < 1e-9)
                    current.RemoveAt(current.Count - 1);
                if (current.Count >= 3) result.Add(current);
            }
            current = new List<(double x, double y)>();
            started = false;
        }

        foreach (var seg in path.Segments)
        {
            switch (seg.Kind)
            {
                case VipsPathSegmentKind.MoveTo:
                    if (started) EndSubpath();
                    cx = seg.X1; cy = seg.Y1;
                    current.Add((cx, cy));
                    started = true;
                    break;
                case VipsPathSegmentKind.LineTo:
                    if (!started) { current.Add((seg.X1, seg.Y1)); started = true; }
                    current.Add((seg.X1, seg.Y1));
                    cx = seg.X1; cy = seg.Y1;
                    break;
                case VipsPathSegmentKind.CubicTo:
                    FlattenCubicAdaptive(current, cx, cy, seg.X1, seg.Y1, seg.X2, seg.Y2, seg.X3, seg.Y3);
                    cx = seg.X3; cy = seg.Y3;
                    break;
                case VipsPathSegmentKind.QuadraticTo:
                    FlattenQuadraticAdaptive(current, cx, cy, seg.X1, seg.Y1, seg.X2, seg.Y2);
                    cx = seg.X2; cy = seg.Y2;
                    break;
                case VipsPathSegmentKind.Close:
                    EndSubpath();
                    break;
            }
        }
        if (started) EndSubpath();
        return result;
    }

    private static void FlattenCubicAdaptive(List<(double x, double y)> dst,
        double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3,
        int depth = 0)
    {
        double ux = 3 * x1 - 2 * x0 - x3;
        double uy = 3 * y1 - 2 * y0 - y3;
        double vx = 3 * x2 - 2 * x3 - x0;
        double vy = 3 * y2 - 2 * y3 - y0;
        double max = Math.Max(ux * ux, vx * vx) + Math.Max(uy * uy, vy * vy);
        if (max <= 0.25 * 0.25 || depth > 16) { dst.Add((x3, y3)); return; }
        double mx0 = (x0 + x1) / 2, my0 = (y0 + y1) / 2;
        double mx1 = (x1 + x2) / 2, my1 = (y1 + y2) / 2;
        double mx2 = (x2 + x3) / 2, my2 = (y2 + y3) / 2;
        double m01x = (mx0 + mx1) / 2, m01y = (my0 + my1) / 2;
        double m12x = (mx1 + mx2) / 2, m12y = (my1 + my2) / 2;
        double mx = (m01x + m12x) / 2, my = (m01y + m12y) / 2;
        FlattenCubicAdaptive(dst, x0, y0, mx0, my0, m01x, m01y, mx, my, depth + 1);
        FlattenCubicAdaptive(dst, mx, my, m12x, m12y, mx2, my2, x3, y3, depth + 1);
    }

    private static void FlattenQuadraticAdaptive(List<(double x, double y)> dst,
        double x0, double y0, double x1, double y1, double x2, double y2, int depth = 0)
    {
        double dx = (x0 + x2) / 2 - x1;
        double dy = (y0 + y2) / 2 - y1;
        double max = dx * dx + dy * dy;
        if (max <= 0.25 * 0.25 || depth > 16) { dst.Add((x2, y2)); return; }
        double mx0 = (x0 + x1) / 2, my0 = (y0 + y1) / 2;
        double mx1 = (x1 + x2) / 2, my1 = (y1 + y2) / 2;
        double mx = (mx0 + mx1) / 2, my = (my0 + my1) / 2;
        FlattenQuadraticAdaptive(dst, x0, y0, mx0, my0, mx, my, depth + 1);
        FlattenQuadraticAdaptive(dst, mx, my, mx1, my1, x2, y2, depth + 1);
    }
}

/// <summary>
/// Greiner-Hormann polygon clipping. Builds doubly-linked vertex
/// lists for both polygons, finds all edge intersections, classifies
/// each as entry or exit, then walks the linked structure to emit
/// output polygons.
///
/// <para>Supports the standard non-degenerate case. Vertex-on-edge or
/// coincident-edge configurations are not handled.</para>
///
/// <para>Convention used by this implementation:
/// • <see cref="VipsBooleanOp.Intersect"/> — no flag flipping
/// • <see cref="VipsBooleanOp.Union"/> — flip both subject and clip
/// • <see cref="VipsBooleanOp.Subtract"/> — flip subject only.
/// (Walk-forward at entry, walk-backward at exit; jump to neighbour
/// after each intersection.)</para>
/// </summary>
internal static class GreinerHormann
{
    private sealed class Vertex
    {
        public double X, Y;
        public Vertex Next = null!, Prev = null!;
        public bool IsIntersect;
        public bool IsEntry;
        public Vertex? Neighbour;
        public bool Visited;
        public double Alpha;
        public Vertex(double x, double y) { X = x; Y = y; }
    }

    public static List<List<(double x, double y)>> Clip(
        List<(double x, double y)> subject, List<(double x, double y)> clip, VipsBooleanOp op)
    {
        var p = BuildList(subject);
        var q = BuildList(clip);
        var pInts = new List<Vertex>();
        var qInts = new List<Vertex>();
        FindIntersections(p, q, pInts, qInts);

        if (pInts.Count == 0)
            return HandleNoIntersection(subject, clip, op);

        bool subjStartInClip = PointInPolygon(subject[0], clip);
        bool clipStartInSubj = PointInPolygon(clip[0], subject);
        WalkAndMark(p, !subjStartInClip);
        WalkAndMark(q, !clipStartInSubj);

        if (op == VipsBooleanOp.Union) { FlipEntryExit(p); FlipEntryExit(q); }
        else if (op == VipsBooleanOp.Subtract) FlipEntryExit(p);

        var result = new List<List<(double x, double y)>>();
        foreach (var v in pInts)
        {
            if (v.Visited) continue;
            var poly = TraceOutput(v);
            if (poly.Count >= 3) result.Add(poly);
        }
        return result;
    }

    private static Vertex BuildList(List<(double x, double y)> poly)
    {
        var first = new Vertex(poly[0].x, poly[0].y);
        var prev = first;
        for (int i = 1; i < poly.Count; i++)
        {
            var v = new Vertex(poly[i].x, poly[i].y);
            prev.Next = v; v.Prev = prev;
            prev = v;
        }
        prev.Next = first; first.Prev = prev;
        return first;
    }

    /// <summary>
    /// Snapshot a polygon's original (non-intersection) vertices in
    /// traversal order. We need this because intersection insertions
    /// mutate the linked-list .Next chains; iterating the live chain
    /// would re-visit inserted intersections.
    /// </summary>
    private static List<Vertex> SnapshotOriginal(Vertex first)
    {
        var list = new List<Vertex>();
        var cur = first;
        do { list.Add(cur); cur = cur.Next; } while (cur != first);
        return list;
    }

    private static void FindIntersections(Vertex p, Vertex q,
        List<Vertex> pInts, List<Vertex> qInts)
    {
        var pVerts = SnapshotOriginal(p);
        var qVerts = SnapshotOriginal(q);
        // For each original edge in P × each original edge in Q.
        for (int i = 0; i < pVerts.Count; i++)
        {
            var pa = pVerts[i];
            var pb = pVerts[(i + 1) % pVerts.Count];
            for (int j = 0; j < qVerts.Count; j++)
            {
                var qa = qVerts[j];
                var qb = qVerts[(j + 1) % qVerts.Count];
                if (!LineIntersect(pa.X, pa.Y, pb.X, pb.Y, qa.X, qa.Y, qb.X, qb.Y,
                                    out double t, out double u)) continue;
                double ix = pa.X + t * (pb.X - pa.X);
                double iy = pa.Y + t * (pb.Y - pa.Y);
                var ip = new Vertex(ix, iy) { IsIntersect = true, Alpha = t };
                var iq = new Vertex(ix, iy) { IsIntersect = true, Alpha = u };
                ip.Neighbour = iq; iq.Neighbour = ip;
                InsertSorted(pa, pb, ip);
                InsertSorted(qa, qb, iq);
                pInts.Add(ip);
                qInts.Add(iq);
            }
        }
    }

    /// <summary>
    /// Insert <paramref name="newV"/> into the linked list between the
    /// edge endpoints <paramref name="a"/> and <paramref name="b"/>.
    /// Other intersections may already sit on this edge — skip past
    /// the ones with smaller alpha so insertions preserve parametric
    /// order along the edge.
    /// </summary>
    private static void InsertSorted(Vertex a, Vertex b, Vertex newV)
    {
        var cur = a.Next;
        while (cur != b && cur.IsIntersect && cur.Alpha < newV.Alpha)
            cur = cur.Next;
        newV.Prev = cur.Prev;
        newV.Next = cur;
        cur.Prev.Next = newV;
        cur.Prev = newV;
    }

    /// <summary>
    /// Strict segment-segment intersection. Returns <c>true</c> only
    /// for proper interior crossings (0 &lt; t &lt; 1 and 0 &lt; u &lt; 1)
    /// — vertex-on-edge contacts are excluded by design.
    /// </summary>
    private static bool LineIntersect(double x1, double y1, double x2, double y2,
        double x3, double y3, double x4, double y4, out double t, out double u)
    {
        double dx1 = x2 - x1, dy1 = y2 - y1;
        double dx2 = x4 - x3, dy2 = y4 - y3;
        double denom = dx1 * dy2 - dy1 * dx2;
        if (Math.Abs(denom) < 1e-12) { t = u = 0; return false; }
        t = ((x3 - x1) * dy2 - (y3 - y1) * dx2) / denom;
        u = ((x3 - x1) * dy1 - (y3 - y1) * dx1) / denom;
        return t > 1e-9 && t < 1 - 1e-9 && u > 1e-9 && u < 1 - 1e-9;
    }

    /// <summary>Standard ray-casting point-in-polygon test.</summary>
    private static bool PointInPolygon((double x, double y) p, List<(double x, double y)> poly)
    {
        bool inside = false;
        int n = poly.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double xi = poly[i].x, yi = poly[i].y;
            double xj = poly[j].x, yj = poly[j].y;
            if (yi > p.y != yj > p.y &&
                p.x < (xj - xi) * (p.y - yi) / (yj - yi) + xi)
                inside = !inside;
        }
        return inside;
    }

    /// <summary>
    /// Walk the polygon in order, alternating each intersection's
    /// entry/exit flag starting from <paramref name="firstIsEntry"/>.
    /// </summary>
    private static void WalkAndMark(Vertex start, bool firstIsEntry)
    {
        bool isEntry = firstIsEntry;
        var cur = start;
        do
        {
            if (cur.IsIntersect) { cur.IsEntry = isEntry; isEntry = !isEntry; }
            cur = cur.Next;
        } while (cur != start);
    }

    private static void FlipEntryExit(Vertex start)
    {
        var cur = start;
        do
        {
            if (cur.IsIntersect) cur.IsEntry = !cur.IsEntry;
            cur = cur.Next;
        } while (cur != start);
    }

    /// <summary>
    /// Trace one output polygon starting from the unvisited
    /// intersection <paramref name="start"/>. At entry intersections,
    /// walk forward; at exits, walk backward; switch to the other
    /// polygon by following the cross-pointer at every intersection.
    /// </summary>
    private static List<(double x, double y)> TraceOutput(Vertex start)
    {
        var result = new List<(double x, double y)>();
        var cur = start;
        do
        {
            cur.Visited = true;
            bool forward = cur.IsEntry;
            // Walk along the current polygon until the next intersection.
            while (true)
            {
                cur = forward ? cur.Next : cur.Prev;
                result.Add((cur.X, cur.Y));
                if (cur.IsIntersect) break;
            }
            cur.Visited = true;
            if (cur.Neighbour == null) break;
            cur = cur.Neighbour;
            cur.Visited = true;
        } while (cur != start);
        return result;
    }

    private static List<List<(double x, double y)>> HandleNoIntersection(
        List<(double x, double y)> subject, List<(double x, double y)> clip, VipsBooleanOp op)
    {
        bool subjInClip = PointInPolygon(subject[0], clip);
        bool clipInSubj = PointInPolygon(clip[0], subject);
        var result = new List<List<(double x, double y)>>();
        switch (op)
        {
            case VipsBooleanOp.Intersect:
                if (subjInClip) result.Add(subject);
                else if (clipInSubj) result.Add(clip);
                break;
            case VipsBooleanOp.Union:
                if (subjInClip) result.Add(clip);
                else if (clipInSubj) result.Add(subject);
                else { result.Add(subject); result.Add(clip); }
                break;
            case VipsBooleanOp.Subtract:
                if (subjInClip) { /* subject fully removed */ }
                else if (clipInSubj)
                {
                    // Donut: outer subject + reversed clip as hole (even-odd fill).
                    result.Add(subject);
                    var reversed = new List<(double x, double y)>(clip);
                    reversed.Reverse();
                    result.Add(reversed);
                }
                else result.Add(subject);
                break;
        }
        return result;
    }
}
