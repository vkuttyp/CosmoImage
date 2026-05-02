using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Convolution;

/// <summary>
/// Statistical differencing — local-contrast enhancement that
/// renormalises every pixel against its local mean and standard
/// deviation. For each pixel:
/// <code>out = (in − μ) · (a · σ_target / σ_local) + μ_target</code>
/// where μ and σ_local are computed over an
/// <see cref="WindowWidth"/>×<see cref="WindowHeight"/> neighbourhood.
/// Mirrors libvips <c>vips_stdif</c>.
///
/// <para>UChar 1-band only — the algorithm is dominated by the
/// per-window stats, so we use integer summed-area tables for
/// O(1) per-pixel mean and stddev.</para>
///
/// <para>Useful for document images where global contrast is uneven:
/// <c>Stdif(11, 11, sigmaTarget: 50, meanTarget: 128)</c> turns a
/// scan with shadows into something with consistent local
/// contrast.</para>
/// </summary>
public class VipsStdif : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public int WindowWidth { get; set; } = 11;
    public int WindowHeight { get; set; } = 11;
    /// <summary>Target stddev. Defaults to 50.</summary>
    public double SigmaTarget { get; set; } = 50;
    /// <summary>Target mean. Defaults to 128.</summary>
    public double MeanTarget { get; set; } = 128;
    /// <summary>Stretch coefficient — how much of the target stddev to apply. 0..1.</summary>
    public double A { get; set; } = 0.5;

    public override int Build()
    {
        if (In == null) return -1;
        if (In.BandFormat != VipsBandFormat.UChar) return -1;
        if (In.Bands != 1) return -1;
        if (WindowWidth < 1 || WindowHeight < 1) return -1;
        if ((WindowWidth & 1) == 0 || (WindowHeight & 1) == 0) return -1; // odd only

        // Materialise + build summed-area tables for sum and sum-of-squares.
        byte[] pixels;
        if (In.Pixels is { } existing) pixels = existing;
        else
        {
            var sink = new MemorySink(In);
            sink.RunAsync().GetAwaiter().GetResult();
            pixels = sink.Pixels;
        }
        int W = In.Width, H = In.Height;
        var output = new byte[W * H];
        ApplyStdif(pixels, output, W, H,
            WindowWidth, WindowHeight, SigmaTarget, MeanTarget, A);

        Out = new VipsImage
        {
            Width = W, Height = H, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = In.Interpretation,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In, ClientB = output,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Stdif", RuntimeHelpers.GetHashCode(In),
            WindowWidth, WindowHeight, SigmaTarget, MeanTarget, A);

    private static void ApplyStdif(byte[] src, byte[] dst, int W, int H,
        int ww, int wh, double sigT, double meanT, double a)
    {
        // Summed-area tables (1-pixel padded). sat[(y+1)*(W+1) + (x+1)] is the
        // sum over [0..x] × [0..y] inclusive.
        var sat = new long[(W + 1) * (H + 1)];
        var satSq = new long[(W + 1) * (H + 1)];
        for (int y = 0; y < H; y++)
        {
            long rowSum = 0, rowSumSq = 0;
            for (int x = 0; x < W; x++)
            {
                byte v = src[y * W + x];
                rowSum += v; rowSumSq += (long)v * v;
                int idx = (y + 1) * (W + 1) + (x + 1);
                sat[idx] = sat[idx - (W + 1)] + rowSum;
                satSq[idx] = satSq[idx - (W + 1)] + rowSumSq;
            }
        }

        int hw = ww / 2, hh = wh / 2;

        for (int y = 0; y < H; y++)
        {
            int y0 = Math.Max(0, y - hh);
            int y1 = Math.Min(H - 1, y + hh);
            for (int x = 0; x < W; x++)
            {
                int x0 = Math.Max(0, x - hw);
                int x1 = Math.Min(W - 1, x + hw);
                long count = (long)(x1 - x0 + 1) * (y1 - y0 + 1);
                long sum = SatRectSum(sat, W, x0, y0, x1, y1);
                long sumSq = SatRectSum(satSq, W, x0, y0, x1, y1);
                double mean = (double)sum / count;
                double variance = (double)sumSq / count - mean * mean;
                if (variance < 0) variance = 0;
                double sd = Math.Sqrt(variance);
                double scale = sd > 0 ? a * sigT / sd : 0;
                double v = (src[y * W + x] - mean) * scale + meanT;
                dst[y * W + x] = (byte)Math.Clamp(v, 0, 255);
            }
        }
    }

    private static long SatRectSum(long[] sat, int W, int x0, int y0, int x1, int y1)
    {
        // sum over inclusive box (x0..x1, y0..y1).
        int stride = W + 1;
        return sat[(y1 + 1) * stride + (x1 + 1)]
             - sat[y0 * stride + (x1 + 1)]
             - sat[(y1 + 1) * stride + x0]
             + sat[y0 * stride + x0];
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var output = (byte[])b!;
        VipsImage @out = outRegion.Image;
        VipsRect r = outRegion.Valid;
        int W = @out.Width;
        for (int y = 0; y < r.Height; y++)
        {
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            int srcRow = (r.Top + y) * W + r.Left;
            output.AsSpan(srcRow, r.Width).CopyTo(outAddr);
        }
        return 0;
    }
}
