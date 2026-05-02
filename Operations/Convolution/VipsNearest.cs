using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Convolution;

/// <summary>
/// Euclidean distance transform — for each pixel, the distance (in
/// pixels) to the nearest non-zero pixel in the input. Mirrors libvips
/// <c>vips_nearest</c>.
///
/// <para>Implementation: separable squared-EDT via the Felzenszwalb &amp;
/// Huttenlocher 2004 linear-time 1D parabola-envelope algorithm. The
/// 1D pass is run on rows then columns of the squared distance map;
/// the result is exact, not an approximation. Output is UChar
/// (clamped); for distances larger than 255 use a Float-output
/// variant later.</para>
///
/// <para>UChar 1-band input only. Foreground = non-zero, background =
/// zero — the libvips convention.</para>
/// </summary>
public class VipsNearest : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }

    public override int Build()
    {
        if (In == null) return -1;
        if (In.BandFormat != VipsBandFormat.UChar) return -1;
        if (In.Bands != 1) return -1;

        byte[] pixels;
        if (In.Pixels is { } existing) pixels = existing;
        else
        {
            var sink = new MemorySink(In);
            sink.RunAsync().GetAwaiter().GetResult();
            pixels = sink.Pixels;
        }

        int W = In.Width, H = In.Height;
        var dist = ComputeDistanceTransform(pixels, W, H);
        var distBytes = new byte[W * H];
        for (int i = 0; i < dist.Length; i++)
            distBytes[i] = (byte)Math.Clamp(Math.Sqrt(dist[i]), 0, 255);

        Out = new VipsImage
        {
            Width = W, Height = H, Bands = 1,
            BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In, ClientB = distBytes,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Nearest", RuntimeHelpers.GetHashCode(In));

    /// <summary>
    /// Felzenszwalb-Huttenlocher exact squared EDT in O(n) per row /
    /// column. Returns squared distances as long[] sized W*H.
    /// </summary>
    private static long[] ComputeDistanceTransform(byte[] pixels, int W, int H)
    {
        const long INF = long.MaxValue / 4;
        var sq = new long[W * H];

        // Initialise: foreground (non-zero) → 0, background → INF.
        for (int i = 0; i < W * H; i++) sq[i] = pixels[i] != 0 ? 0 : INF;

        // 1D pass over each row (X axis).
        var row = new long[W];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++) row[x] = sq[y * W + x];
            var rowDt = Edt1D(row, W);
            for (int x = 0; x < W; x++) sq[y * W + x] = rowDt[x];
        }

        // 1D pass over each column (Y axis).
        var col = new long[H];
        for (int x = 0; x < W; x++)
        {
            for (int y = 0; y < H; y++) col[y] = sq[y * W + x];
            var colDt = Edt1D(col, H);
            for (int y = 0; y < H; y++) sq[y * W + x] = colDt[y];
        }
        return sq;
    }

    /// <summary>
    /// Lower-envelope of parabolas algorithm. Given f, computes
    /// <c>D(p) = min_q (f(q) + (p − q)²)</c> in O(n). Uses doubles
    /// internally to keep the intersection-point formula simple; INF
    /// inputs are honoured (no parabola is added).
    /// </summary>
    private static long[] Edt1D(long[] f, int n)
    {
        const double INF = 1e18;
        var fd = new double[n];
        for (int i = 0; i < n; i++) fd[i] = f[i] >= long.MaxValue / 4 ? INF : f[i];

        var d = new long[n];
        var v = new int[n];        // locations of parabola apexes
        var z = new double[n + 1]; // intersection x-coordinates
        int k = -1;
        z[0] = double.NegativeInfinity;
        for (int q = 0; q < n; q++)
        {
            if (fd[q] >= INF) continue;
            double s;
            while (true)
            {
                if (k < 0) { k = 0; v[0] = q; z[0] = double.NegativeInfinity; z[1] = double.PositiveInfinity; break; }
                s = ((fd[q] + (double)q * q) - (fd[v[k]] + (double)v[k] * v[k])) / (2.0 * (q - v[k]));
                if (s <= z[k]) { k--; continue; }
                k++;
                v[k] = q;
                z[k] = s;
                z[k + 1] = double.PositiveInfinity;
                break;
            }
        }
        if (k < 0)
        {
            // No foreground anywhere: all distances infinite (clamped later).
            for (int q = 0; q < n; q++) d[q] = long.MaxValue / 4;
            return d;
        }
        int kk = 0;
        for (int q = 0; q < n; q++)
        {
            while (z[kk + 1] < q) kk++;
            d[q] = (long)(q - v[kk]) * (q - v[kk]) + (long)fd[v[kk]];
        }
        return d;
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var distBytes = (byte[])b!;
        VipsImage @out = outRegion.Image;
        VipsRect r = outRegion.Valid;
        int W = @out.Width;

        for (int y = 0; y < r.Height; y++)
        {
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            int srcRow = (r.Top + y) * W + r.Left;
            distBytes.AsSpan(srcRow, r.Width).CopyTo(outAddr);
        }
        return 0;
    }
}
