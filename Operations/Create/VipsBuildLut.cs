using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Create;

/// <summary>
/// Build a single-row LUT from a list of (x, y) anchor points by
/// linear interpolation. Points are sorted by x, the LUT spans
/// <c>[0, max(x)]</c>, and bands &gt; 1 are produced when each
/// row in <see cref="Points"/> carries multiple y values
/// (<c>{x, y₀, y₁, …}</c>).
///
/// <para>Mirrors libvips <c>vips_buildlut</c>. The canonical use is
/// designing tone curves: <c>{0, 0}, {64, 30}, {128, 128}, {192, 220},
/// {255, 255}</c> gives an S-curve that lifts shadows and highlights.
/// Output is UChar, 1 row tall, width = <c>max(x) + 1</c>.</para>
/// </summary>
public class VipsBuildLut : VipsOperation
{
    public VipsImage? Out { get; set; }
    /// <summary>Each row: <c>{x, y0[, y1, ...]}</c>. Anchor x must be ≥ 0.</summary>
    public double[,]? Points { get; set; }

    public override int Build()
    {
        if (Points == null) return -1;
        int n = Points.GetLength(0);
        int cols = Points.GetLength(1);
        if (n < 2 || cols < 2) return -1;

        int outBands = cols - 1;
        // Sort rows by x ascending — copy out, sort, validate non-negative xs.
        var rows = new (double x, double[] ys)[n];
        for (int i = 0; i < n; i++)
        {
            if (Points[i, 0] < 0) return -1;
            var ys = new double[outBands];
            for (int j = 0; j < outBands; j++) ys[j] = Points[i, j + 1];
            rows[i] = (Points[i, 0], ys);
        }
        Array.Sort(rows, (p, q) => p.x.CompareTo(q.x));

        int outWidth = (int)Math.Round(rows[n - 1].x) + 1;
        var lut = new byte[outWidth * outBands];

        // For each output column x, find segment (rows[i].x ≤ x ≤ rows[i+1].x)
        // and interpolate. Beyond the right anchor, hold last value.
        int seg = 0;
        for (int x = 0; x < outWidth; x++)
        {
            while (seg < n - 1 && x > rows[seg + 1].x) seg++;
            double x0 = rows[seg].x;
            double x1 = seg < n - 1 ? rows[seg + 1].x : x0;
            double t = x1 > x0 ? (x - x0) / (x1 - x0) : 0;
            for (int bnd = 0; bnd < outBands; bnd++)
            {
                double y0 = rows[seg].ys[bnd];
                double y1 = seg < n - 1 ? rows[seg + 1].ys[bnd] : y0;
                double v = y0 + (y1 - y0) * t;
                lut[x * outBands + bnd] = (byte)Math.Clamp(Math.Round(v), 0, 255);
            }
        }

        Out = new VipsImage
        {
            Width = outWidth, Height = 1, Bands = outBands,
            BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.Histogram,
            XRes = 1, YRes = 1,
            GenerateFn = Generate, ClientB = lut,
        };
        Out.SetPipeline(VipsDemandStyle.Any);
        return 0;
    }

    public override int GetCacheKey()
    {
        var h = new HashCode();
        h.Add("BuildLut");
        if (Points != null) foreach (var v in Points) h.Add(v);
        return h.ToHashCode();
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var lut = (byte[])b!;
        VipsRect r = outRegion.Valid;
        int bands = outRegion.Image.Bands;
        var outAddr = outRegion.GetAddress(r.Left, 0);
        lut.AsSpan(r.Left * bands, r.Width * bands).CopyTo(outAddr);
        return 0;
    }
}
