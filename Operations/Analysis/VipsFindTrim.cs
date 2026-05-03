using System;
using System.Buffers.Binary;

namespace CosmoImage.Operations.Analysis;

/// <summary>
/// Auto-find the bounding box of the non-background pixels — the
/// smallest rectangle that contains every pixel whose distance from
/// <see cref="VipsFindTrim.DefaultBackground(VipsImage)"/> exceeds
/// <c>threshold</c>. Mirrors libvips <c>vips_find_trim</c>.
///
/// <para>The standard auto-crop primitive: scan a flat-bordered scan
/// or rendered image, return the rect of just the content. UChar
/// 1- or 3-band; threshold defaults to 10 (catches anti-aliased
/// edges without being fooled by JPEG ringing).</para>
///
/// <para>Returns <c>(0, 0, 0, 0)</c> if every pixel is within
/// <c>threshold</c> of the background — i.e. the image is uniformly
/// background and there's nothing to crop to.</para>
/// </summary>
public static class VipsFindTrim
{
    /// <summary>
    /// Find the trim rectangle. <paramref name="background"/> defaults
    /// to the top-left pixel — the libvips convention; override for
    /// images with a different known background.
    /// </summary>
    public static VipsRect Compute(VipsImage input, int threshold = 10, double[]? background = null)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (input.BandFormat != VipsBandFormat.UChar)
            throw new ArgumentException("FindTrim requires UChar input.", nameof(input));

        byte[] pixels;
        if (input.Pixels is { } existing) pixels = existing;
        else
        {
            var sink = new MemorySink(input);
            sink.RunAsync().GetAwaiter().GetResult();
            pixels = sink.Pixels;
        }

        int W = input.Width, H = input.Height, bands = input.Bands;
        var bg = background ?? DefaultBackground(input);

        int left = W, right = -1, top = H, bottom = -1;
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int pelOff = (y * W + x) * bands;
                int maxDiff = 0;
                for (int bnd = 0; bnd < bands; bnd++)
                {
                    int d = Math.Abs(pixels[pelOff + bnd] - (int)bg[bnd]);
                    if (d > maxDiff) maxDiff = d;
                }
                if (maxDiff > threshold)
                {
                    if (x < left) left = x;
                    if (x > right) right = x;
                    if (y < top) top = y;
                    if (y > bottom) bottom = y;
                }
            }
        }
        if (right < 0) return new VipsRect(0, 0, 0, 0);
        return new VipsRect(left, top, right - left + 1, bottom - top + 1);
    }

    public static double[] DefaultBackground(VipsImage input)
    {
        // libvips uses the top-left pixel as the background reference.
        byte[] pixels;
        if (input.Pixels is { } existing) pixels = existing;
        else
        {
            var sink = new MemorySink(input);
            sink.RunAsync().GetAwaiter().GetResult();
            pixels = sink.Pixels;
        }
        var bg = new double[input.Bands];
        for (int bnd = 0; bnd < input.Bands; bnd++) bg[bnd] = pixels[bnd];
        return bg;
    }
}
