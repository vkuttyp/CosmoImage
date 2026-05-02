using System;

namespace CosmoImage.Operations.Analysis;

/// <summary>
/// Shannon entropy of an image's pixel-value distribution, per band.
/// <c>H = -Σ p_i log₂ p_i</c> where <c>p_i</c> is the fraction of
/// pixels falling in bin <c>i</c>. Mirrors libvips
/// <c>vips_hist_entropy</c>; UChar only, since the 256-bin
/// histogram is fundamentally UChar-shaped.
///
/// <para>Useful as a flatness/complexity probe (low entropy → mostly
/// uniform, high entropy → wide tonal spread). Single-band UChar gives
/// values in <c>[0, 8]</c> bits.</para>
/// </summary>
public static class VipsHistEntropy
{
    /// <summary>
    /// Returns one entropy value per band, plus an aggregate (sum of
    /// per-band counts before computing) at index <c>Bands</c> — same
    /// shape convention as <see cref="VipsStatsResult"/>.
    /// </summary>
    public static double[] Compute(VipsImage input)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (input.BandFormat != VipsBandFormat.UChar)
            throw new ArgumentException("HistEntropy requires UChar input.", nameof(input));

        byte[] pixels;
        if (input.Pixels is { } existing) pixels = existing;
        else
        {
            var sink = new MemorySink(input);
            sink.RunAsync().GetAwaiter().GetResult();
            pixels = sink.Pixels;
        }

        int W = input.Width, H = input.Height, bands = input.Bands;
        var hist = new long[bands + 1, 256];

        for (int y = 0; y < H; y++)
        {
            int rowBase = y * W * bands;
            for (int x = 0; x < W; x++)
            {
                for (int bnd = 0; bnd < bands; bnd++)
                {
                    byte v = pixels[rowBase + x * bands + bnd];
                    hist[bnd, v]++;
                    hist[bands, v]++;
                }
            }
        }

        var result = new double[bands + 1];
        for (int bnd = 0; bnd <= bands; bnd++)
        {
            long total = 0;
            for (int i = 0; i < 256; i++) total += hist[bnd, i];
            if (total == 0) continue;

            double h = 0;
            for (int i = 0; i < 256; i++)
            {
                long c = hist[bnd, i];
                if (c == 0) continue;
                double p = (double)c / total;
                h -= p * Math.Log2(p);
            }
            result[bnd] = h;
        }
        return result;
    }
}
