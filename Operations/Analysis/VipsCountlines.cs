using System;

namespace CosmoImage.Operations.Analysis;

/// <summary>
/// Count black/white transitions per scanline and return the average.
/// Mirrors libvips <c>vips_countlines</c>. UChar 1-band only.
///
/// <para>Used in document layout: a page of horizontal text shows a
/// regular cadence of dark→light→dark transitions per row, and the
/// average count gives a coarse estimate of text-line density (and,
/// by inversion, line spacing).</para>
///
/// <para><see cref="VipsDirection.Horizontal"/> counts transitions per
/// row; <see cref="VipsDirection.Vertical"/> per column.</para>
/// </summary>
public static class VipsCountlines
{
    public static double Compute(VipsImage input, VipsDirection direction = VipsDirection.Horizontal)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (input.BandFormat != VipsBandFormat.UChar)
            throw new ArgumentException("Countlines requires UChar input.", nameof(input));
        if (input.Bands != 1)
            throw new ArgumentException("Countlines requires single-band input.", nameof(input));

        byte[] pixels;
        if (input.Pixels is { } existing) pixels = existing;
        else
        {
            var sink = new MemorySink(input);
            sink.RunAsync().GetAwaiter().GetResult();
            pixels = sink.Pixels;
        }

        int W = input.Width, H = input.Height;
        long transitions = 0;

        if (direction == VipsDirection.Horizontal)
        {
            for (int y = 0; y < H; y++)
            {
                int rowBase = y * W;
                byte prev = pixels[rowBase];
                for (int x = 1; x < W; x++)
                {
                    byte v = pixels[rowBase + x];
                    if ((prev >= 128) != (v >= 128)) transitions++;
                    prev = v;
                }
            }
            return H == 0 ? 0 : (double)transitions / H;
        }
        else
        {
            for (int x = 0; x < W; x++)
            {
                byte prev = pixels[x];
                for (int y = 1; y < H; y++)
                {
                    byte v = pixels[y * W + x];
                    if ((prev >= 128) != (v >= 128)) transitions++;
                    prev = v;
                }
            }
            return W == 0 ? 0 : (double)transitions / W;
        }
    }
}
