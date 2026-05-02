using System;

namespace CosmoImage.Operations.Analysis;

/// <summary>
/// Find the pixel-value threshold below which <paramref name="percent"/>
/// percent of the (aggregate, all-band) histogram lies. Mirrors libvips
/// <c>vips_percent</c>. UChar only.
///
/// <para>Common use is auto-threshold: <c>Percent(image, 5)</c> returns
/// the bin at which 5% of the image is darker, useful as a low-end
/// black-point estimate. <c>Percent(image, 95)</c> picks a high-end
/// white-point. Combine with <c>Linear</c>/<c>Cast</c> to do a
/// percentile-based stretch.</para>
/// </summary>
public static class VipsPercent
{
    public static int Compute(VipsImage input, double percent)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (input.BandFormat != VipsBandFormat.UChar)
            throw new ArgumentException("Percent requires UChar input.", nameof(input));
        if (percent < 0 || percent > 100)
            throw new ArgumentOutOfRangeException(nameof(percent), "Must be in [0, 100].");

        byte[] pixels;
        if (input.Pixels is { } existing) pixels = existing;
        else
        {
            var sink = new MemorySink(input);
            sink.RunAsync().GetAwaiter().GetResult();
            pixels = sink.Pixels;
        }

        int W = input.Width, H = input.Height, bands = input.Bands;
        var hist = new long[256];
        for (int y = 0; y < H; y++)
        {
            int rowBase = y * W * bands;
            for (int x = 0; x < W; x++)
                for (int bnd = 0; bnd < bands; bnd++)
                    hist[pixels[rowBase + x * bands + bnd]]++;
        }

        long total = (long)W * H * bands;
        long target = (long)Math.Round(percent / 100.0 * total);

        long acc = 0;
        for (int i = 0; i < 256; i++)
        {
            acc += hist[i];
            if (acc >= target) return i;
        }
        return 255;
    }
}
