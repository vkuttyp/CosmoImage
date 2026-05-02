using System;
using System.Collections.Generic;

namespace CosmoImage.Operations.Analysis;

/// <summary>
/// Per-band stats for an image. <see cref="Min"/>, <see cref="Max"/>,
/// <see cref="Avg"/>, <see cref="Deviate"/> are arrays of length
/// <c>Bands + 1</c>: indices 0..Bands-1 are per-band, the last entry is the
/// aggregate over all bands. Deviate is the population standard deviation.
/// Mirrors libvips <c>vips_stats</c> shape.
/// </summary>
public sealed class VipsStatsResult
{
    public double[] Min { get; init; } = Array.Empty<double>();
    public double[] Max { get; init; } = Array.Empty<double>();
    public double[] Avg { get; init; } = Array.Empty<double>();
    public double[] Deviate { get; init; } = Array.Empty<double>();
}

/// <summary>
/// Materializing reduction over an image. Walks every pixel exactly once;
/// uses <see cref="MemorySink"/> when the input is not already memory-backed.
/// </summary>
public static class VipsStats
{
    public static VipsStatsResult Compute(VipsImage input)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));

        byte[] pixels;
        if (input.Pixels is { } existing)
        {
            pixels = existing;
        }
        else
        {
            var sink = new MemorySink(input);
            sink.RunAsync().GetAwaiter().GetResult();
            pixels = sink.Pixels;
        }

        int W = input.Width;
        int H = input.Height;
        int bands = input.Bands;
        int pelSize = input.SizeOfPel;
        long pixelsPerBand = (long)W * H;

        var min = new double[bands + 1];
        var max = new double[bands + 1];
        var sum = new double[bands + 1];
        var sumSq = new double[bands + 1];
        for (int i = 0; i <= bands; i++) { min[i] = double.PositiveInfinity; max[i] = double.NegativeInfinity; }

        for (int y = 0; y < H; y++)
        {
            int rowBase = y * W * pelSize;
            for (int x = 0; x < W; x++)
            {
                int pelBase = rowBase + x * pelSize;
                for (int bnd = 0; bnd < bands; bnd++)
                {
                    double v = pixels[pelBase + bnd];
                    if (v < min[bnd]) min[bnd] = v;
                    if (v > max[bnd]) max[bnd] = v;
                    sum[bnd] += v;
                    sumSq[bnd] += v * v;
                }
            }
        }

        // Aggregate row.
        for (int bnd = 0; bnd < bands; bnd++)
        {
            if (min[bnd] < min[bands]) min[bands] = min[bnd];
            if (max[bnd] > max[bands]) max[bands] = max[bnd];
            sum[bands] += sum[bnd];
            sumSq[bands] += sumSq[bnd];
        }

        var avg = new double[bands + 1];
        var dev = new double[bands + 1];
        for (int i = 0; i <= bands; i++)
        {
            long n = i < bands ? pixelsPerBand : pixelsPerBand * bands;
            if (n == 0) { avg[i] = 0; dev[i] = 0; continue; }
            avg[i] = sum[i] / n;
            // population variance = E[x²] − E[x]²
            double var = sumSq[i] / n - avg[i] * avg[i];
            dev[i] = var > 0 ? Math.Sqrt(var) : 0;
        }

        return new VipsStatsResult { Min = min, Max = max, Avg = avg, Deviate = dev };
    }
}
