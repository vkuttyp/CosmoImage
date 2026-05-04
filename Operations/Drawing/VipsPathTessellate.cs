using System;
using System.Collections.Generic;
using System.Linq;

namespace CosmoImage.Operations.Drawing;

public sealed partial class VipsPath
{
    /// <summary>
    /// Triangulate this path into a flat list of triangle vertices —
    /// every 3 consecutive entries form one triangle (suitable for
    /// GPU vertex buffers / mesh export).
    ///
    /// <para>Implementation: ear-clipping (O(n²)) on the flattened
    /// polygon. Bezier segments subdivide to 0.25-px tolerance first.
    /// Each closed sub-path is triangulated independently — useful
    /// for simple polygons, but does NOT subtract holes (e.g., glyph
    /// 'O' produces triangles for both the outer ring AND the inner
    /// hole). For correct hole handling, run path booleans
    /// (<see cref="Subtract"/>) first or restrict to single-ring
    /// shapes. Open sub-paths are skipped — only closed regions are
    /// tessellated.</para>
    /// </summary>
    public List<(double x, double y)> Tessellate()
    {
        var output = new List<(double, double)>();
        foreach (var (poly, closed) in FlattenForSimplify())
        {
            if (!closed || poly.Count < 3) continue;
            EarClip(poly, output);
        }
        return output;
    }

    private static void EarClip(List<(double x, double y)> polyIn, List<(double x, double y)> output)
    {
        // Strip duplicate closing vertex if present (e.g., (0,0) appears at start and end).
        var poly = new List<(double, double)>(polyIn);
        if (poly.Count >= 2 &&
            Math.Abs(poly[0].Item1 - poly[^1].Item1) < 1e-9 &&
            Math.Abs(poly[0].Item2 - poly[^1].Item2) < 1e-9)
            poly.RemoveAt(poly.Count - 1);
        if (poly.Count < 3) return;

        // Ensure CCW winding (positive signed area). Reverse if CW.
        if (SignedArea(poly) < 0) poly.Reverse();

        var indices = Enumerable.Range(0, poly.Count).ToList();
        int safety = poly.Count * poly.Count + 1;
        while (indices.Count > 3 && safety-- > 0)
        {
            bool earFound = false;
            for (int i = 0; i < indices.Count; i++)
            {
                int prev = indices[(i - 1 + indices.Count) % indices.Count];
                int curr = indices[i];
                int next = indices[(i + 1) % indices.Count];
                var a = poly[prev]; var b = poly[curr]; var c = poly[next];
                // Convex (left turn) — required for an ear in CCW polygon.
                if (Cross(a, b, c) <= 0) continue;
                // Triangle must contain no other vertex.
                bool clear = true;
                for (int j = 0; j < indices.Count && clear; j++)
                {
                    if (j == i) continue;
                    int idx = indices[j];
                    if (idx == prev || idx == next) continue;
                    if (PointInTriangle(poly[idx], a, b, c)) clear = false;
                }
                if (!clear) continue;
                output.Add(a); output.Add(b); output.Add(c);
                indices.RemoveAt(i);
                earFound = true;
                break;
            }
            if (!earFound) break; // degenerate self-intersecting input
        }
        if (indices.Count == 3)
        {
            output.Add(poly[indices[0]]);
            output.Add(poly[indices[1]]);
            output.Add(poly[indices[2]]);
        }
    }

    private static double SignedArea(List<(double x, double y)> p)
    {
        double a = 0;
        for (int i = 0; i < p.Count; i++)
        {
            var u = p[i];
            var v = p[(i + 1) % p.Count];
            a += u.x * v.y - v.x * u.y;
        }
        return a / 2.0;
    }

    private static double Cross((double x, double y) a, (double x, double y) b, (double x, double y) c)
        => (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);

    private static bool PointInTriangle((double x, double y) p,
        (double x, double y) a, (double x, double y) b, (double x, double y) c)
    {
        double d1 = Cross(p, a, b);
        double d2 = Cross(p, b, c);
        double d3 = Cross(p, c, a);
        bool hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
        bool hasPos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(hasNeg && hasPos);
    }
}
