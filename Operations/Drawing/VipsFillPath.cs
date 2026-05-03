using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Drawing;

/// <summary>
/// Fill a vector <see cref="VipsPath"/> with an
/// <see cref="IVipsBrush"/>. Mirrors ImageSharp's
/// <c>image.Mutate(c =&gt; c.Fill(brush, path))</c>.
///
/// <para>Implementation: classic scanline polygon fill with the
/// even-odd winding rule. Curves are flattened to line segments
/// via recursive subdivision (flat-enough threshold = 0.25 px).</para>
///
/// <para><see cref="Antialiased"/> defaults to <c>true</c>: each
/// output row is supersampled at 4 vertical positions and accumulated
/// against analytic horizontal coverage, then the brush colour is
/// alpha-blended with the base using the resulting per-pixel coverage
/// value. Set to <c>false</c> for the fast hard-edge fill (no AA).</para>
///
/// <para>UChar input only. Output keeps the input's band format and
/// dimensions.</para>
/// </summary>
public class VipsFillPath : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public VipsPath? Path { get; set; }
    public IVipsBrush? Brush { get; set; }
    /// <summary>Apply 4× vertical supersample AA. Default true.</summary>
    public bool Antialiased { get; set; } = true;
    /// <summary>
    /// Optional rectangular clip — drawing is restricted to this rect
    /// (in image coords). <c>null</c> means no clipping (paint
    /// everywhere the path covers).
    /// </summary>
    public VipsRect? ClipRect { get; set; }

    public override int Build()
    {
        if (In == null || Path == null || Brush == null) return -1;
        if (In.BandFormat != VipsBandFormat.UChar) return -1;

        // Flatten the path into a list of (x0, y0) → (x1, y1) edges.
        var edges = FlattenPath(Path);

        Out = new VipsImage
        {
            Width = In.Width, Height = In.Height,
            Bands = In.Bands, BandFormat = VipsBandFormat.UChar,
            Interpretation = In.Interpretation,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In, ClientB = (edges, Brush, Antialiased, ClipRect),
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
    {
        var h = new HashCode();
        h.Add("FillPath"); h.Add(RuntimeHelpers.GetHashCode(In));
        if (Path != null) foreach (var s in Path.Segments) h.Add(s);
        h.Add(RuntimeHelpers.GetHashCode(Brush));
        return h.ToHashCode();
    }

    private const int AaSubSamples = 4;

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inReg = (VipsRegion)seq!;
        var (edges, brush, aa, clip) = ((List<Edge>, IVipsBrush, bool, VipsRect?))b!;
        VipsImage @in = inReg.Image;
        VipsRect r = outRegion.Valid;

        if (inReg.Prepare(r) != 0) return -1;

        // Copy input verbatim, then overwrite painted pixels.
        int pelSize = @in.SizeOfPel;
        int rowBytes = r.Width * pelSize;
        for (int y = 0; y < r.Height; y++)
            inReg.GetAddress(r.Left, r.Top + y).Slice(0, rowBytes)
                .CopyTo(outRegion.GetAddress(r.Left, r.Top + y));

        // If a clip rect is provided, intersect with the request rect
        // and only paint within that region. Otherwise paint freely
        // throughout the request.
        VipsRect paintRect = r;
        if (clip is VipsRect cr)
        {
            paintRect = VipsRect.Intersect(r, cr);
            if (paintRect.IsEmpty) return 0;
        }

        if (aa)
            FillAntialiased(outRegion, edges, brush, paintRect, pelSize);
        else
            FillHard(outRegion, edges, brush, paintRect, pelSize);
        return 0;
    }

    /// <summary>Hard-edge fill — one scan per pixel row; fast but stair-stepped.</summary>
    private static void FillHard(VipsRegion outRegion, List<Edge> edges, IVipsBrush brush,
        VipsRect r, int pelSize)
    {
        var crossings = new List<double>();
        for (int yy = 0; yy < r.Height; yy++)
        {
            double scan = r.Top + yy + 0.5;
            crossings.Clear();
            foreach (var e in edges)
                if ((e.Y0 <= scan && e.Y1 > scan) || (e.Y1 <= scan && e.Y0 > scan))
                {
                    double t = (scan - e.Y0) / (e.Y1 - e.Y0);
                    crossings.Add(e.X0 + t * (e.X1 - e.X0));
                }
            if (crossings.Count < 2) continue;
            crossings.Sort();
            for (int i = 0; i + 1 < crossings.Count; i += 2)
            {
                int x0 = (int)Math.Ceiling(crossings[i] - 0.5);
                int x1 = (int)Math.Floor(crossings[i + 1] - 0.5);
                if (x0 < r.Left) x0 = r.Left;
                if (x1 >= r.Left + r.Width) x1 = r.Left + r.Width - 1;
                if (x1 < x0) continue;
                var addr = outRegion.GetAddress(x0, r.Top + yy);
                for (int xx = x0; xx <= x1; xx++)
                    brush.SampleAt(xx, r.Top + yy, addr.Slice((xx - x0) * pelSize, pelSize));
            }
        }
    }

    /// <summary>
    /// AA fill: K vertical sub-rows per output row, each with analytic
    /// horizontal coverage on its pair of crossings. Coverage = (sum
    /// across sub-rows) / K. Brush is alpha-blended with the existing
    /// pixel using coverage.
    /// </summary>
    private static void FillAntialiased(VipsRegion outRegion, List<Edge> edges, IVipsBrush brush,
        VipsRect r, int pelSize)
    {
        var coverage = new double[r.Width];
        var crossings = new List<double>();
        Span<byte> sample = stackalloc byte[pelSize];

        for (int yy = 0; yy < r.Height; yy++)
        {
            Array.Clear(coverage, 0, coverage.Length);
            for (int sub = 0; sub < AaSubSamples; sub++)
            {
                double scan = r.Top + yy + (sub + 0.5) / AaSubSamples;
                crossings.Clear();
                foreach (var e in edges)
                    if ((e.Y0 <= scan && e.Y1 > scan) || (e.Y1 <= scan && e.Y0 > scan))
                    {
                        double t = (scan - e.Y0) / (e.Y1 - e.Y0);
                        crossings.Add(e.X0 + t * (e.X1 - e.X0));
                    }
                if (crossings.Count < 2) continue;
                crossings.Sort();
                for (int i = 0; i + 1 < crossings.Count; i += 2)
                {
                    double x0 = crossings[i], x1 = crossings[i + 1];
                    int firstP = Math.Max((int)Math.Floor(x0), r.Left);
                    int lastP = Math.Min((int)Math.Floor(x1), r.Left + r.Width - 1);
                    for (int p = firstP; p <= lastP; p++)
                    {
                        double leftEdge = Math.Max(p, x0);
                        double rightEdge = Math.Min(p + 1, x1);
                        if (rightEdge > leftEdge)
                            coverage[p - r.Left] += rightEdge - leftEdge;
                    }
                }
            }

            var rowAddr = outRegion.GetAddress(r.Left, r.Top + yy);
            for (int xx = 0; xx < r.Width; xx++)
            {
                double cov = coverage[xx] / AaSubSamples;
                if (cov <= 0) continue;
                int px = r.Left + xx;
                int dstOff = xx * pelSize;
                if (cov >= 0.999)
                {
                    brush.SampleAt(px, r.Top + yy, rowAddr.Slice(dstOff, pelSize));
                }
                else
                {
                    brush.SampleAt(px, r.Top + yy, sample);
                    for (int bnd = 0; bnd < pelSize; bnd++)
                    {
                        double baseV = rowAddr[dstOff + bnd];
                        double brushV = sample[bnd];
                        rowAddr[dstOff + bnd] = (byte)Math.Round(baseV * (1 - cov) + brushV * cov);
                    }
                }
            }
        }
    }

    private readonly record struct Edge(double X0, double Y0, double X1, double Y1);

    /// <summary>
    /// Walk a path's segments, expand curves into line segments, and
    /// produce the flat edge list the scanline filler consumes.
    /// </summary>
    private static List<Edge> FlattenPath(VipsPath path)
    {
        var edges = new List<Edge>();
        double cx = 0, cy = 0;       // current pen position
        double sx = 0, sy = 0;       // current sub-path start (for Close)
        bool started = false;

        foreach (var seg in path.Segments)
        {
            switch (seg.Kind)
            {
                case VipsPathSegmentKind.MoveTo:
                    cx = seg.X1; cy = seg.Y1;
                    sx = cx; sy = cy;
                    started = true;
                    break;
                case VipsPathSegmentKind.LineTo:
                    if (!started) { sx = seg.X1; sy = seg.Y1; started = true; }
                    edges.Add(new Edge(cx, cy, seg.X1, seg.Y1));
                    cx = seg.X1; cy = seg.Y1;
                    break;
                case VipsPathSegmentKind.CubicTo:
                    FlattenCubic(edges, cx, cy, seg.X1, seg.Y1, seg.X2, seg.Y2, seg.X3, seg.Y3);
                    cx = seg.X3; cy = seg.Y3;
                    break;
                case VipsPathSegmentKind.QuadraticTo:
                    FlattenQuadratic(edges, cx, cy, seg.X1, seg.Y1, seg.X2, seg.Y2);
                    cx = seg.X2; cy = seg.Y2;
                    break;
                case VipsPathSegmentKind.Close:
                    if (started)
                    {
                        edges.Add(new Edge(cx, cy, sx, sy));
                        cx = sx; cy = sy;
                    }
                    break;
            }
        }
        return edges;
    }

    /// <summary>
    /// Recursively subdivide a cubic Bezier until each sub-segment is
    /// flat enough (control-polygon length ≈ chord length within
    /// 0.25 px), then emit straight edges.
    /// </summary>
    private static void FlattenCubic(List<Edge> edges,
        double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3,
        int depth = 0)
    {
        // Flatness via standard control-point-to-chord distance test.
        double ux = 3 * x1 - 2 * x0 - x3;
        double uy = 3 * y1 - 2 * y0 - y3;
        double vx = 3 * x2 - 2 * x3 - x0;
        double vy = 3 * y2 - 2 * y3 - y0;
        double max = Math.Max(ux * ux, vx * vx) + Math.Max(uy * uy, vy * vy);
        if (max <= 0.25 * 0.25 || depth > 16)
        {
            edges.Add(new Edge(x0, y0, x3, y3));
            return;
        }
        // de Casteljau split at t = 0.5.
        double mx0 = (x0 + x1) / 2, my0 = (y0 + y1) / 2;
        double mx1 = (x1 + x2) / 2, my1 = (y1 + y2) / 2;
        double mx2 = (x2 + x3) / 2, my2 = (y2 + y3) / 2;
        double m01x = (mx0 + mx1) / 2, m01y = (my0 + my1) / 2;
        double m12x = (mx1 + mx2) / 2, m12y = (my1 + my2) / 2;
        double mx = (m01x + m12x) / 2, my = (m01y + m12y) / 2;
        FlattenCubic(edges, x0, y0, mx0, my0, m01x, m01y, mx, my, depth + 1);
        FlattenCubic(edges, mx, my, m12x, m12y, mx2, my2, x3, y3, depth + 1);
    }

    private static void FlattenQuadratic(List<Edge> edges,
        double x0, double y0, double x1, double y1, double x2, double y2,
        int depth = 0)
    {
        double dx = (x0 + x2) / 2 - x1;
        double dy = (y0 + y2) / 2 - y1;
        double max = dx * dx + dy * dy;
        if (max <= 0.25 * 0.25 || depth > 16)
        {
            edges.Add(new Edge(x0, y0, x2, y2));
            return;
        }
        double mx0 = (x0 + x1) / 2, my0 = (y0 + y1) / 2;
        double mx1 = (x1 + x2) / 2, my1 = (y1 + y2) / 2;
        double mx = (mx0 + mx1) / 2, my = (my0 + my1) / 2;
        FlattenQuadratic(edges, x0, y0, mx0, my0, mx, my, depth + 1);
        FlattenQuadratic(edges, mx, my, mx1, my1, x2, y2, depth + 1);
    }
}
