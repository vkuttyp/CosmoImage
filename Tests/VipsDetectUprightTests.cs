using System;
using CosmoImage.Operations.Analysis;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Synthetic-image checks for <see cref="VipsDetectUpright"/>. We build a
/// pattern that mimics the asymmetry of upright Latin text: bands of
/// "ink" rows where the lower half is denser than the upper half
/// (baseline-heavy, ascender-sparse). Flipping vertically swaps the
/// asymmetry; the detector should report opposite signs.
/// </summary>
public class VipsDetectUprightTests
{
    /// <summary>
    /// Build a 300×600 page with 5 "text lines". Each line is a 30-row band
    /// of horizontal stripes whose ink density rises toward the bottom
    /// (the baseline). Returns a UChar 1-band image where ink = 0 and
    /// background = 255.
    /// </summary>
    private static VipsImage BuildPageLikeText(bool upsideDown = false)
    {
        const int W = 300, H = 600;
        const int LineHeight = 30, LineGap = 30;  // 600 / (30+30) = 10 lines
        const int LineCount = 5;
        var pixels = new byte[W * H];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = 255;

        for (int line = 0; line < LineCount; line++)
        {
            int bandTop = 60 + line * (LineHeight + LineGap);  // skip top margin
            // Within each band, set rows to ink (0) with a strong density
            // gradient — top is almost empty (ascender zone), bottom is
            // packed (baseline). The bigger the asymmetry, the cleaner
            // the test signal; we exaggerate beyond a real document to
            // produce a deterministic, well-above-threshold result.
            for (int rowInBand = 0; rowInBand < LineHeight; rowInBand++)
            {
                int y = bandTop + rowInBand;
                if (y < 0 || y >= H) continue;
                double rel = rowInBand / (double)LineHeight;
                int xCount;
                if      (rel < 0.25) xCount = W / 20;    // very sparse top
                else if (rel < 0.75) xCount = W / 2;     // medium middle
                else                 xCount = (W * 19) / 20; // very dense bottom
                for (int x = 0; x < xCount; x += 6)
                    for (int dx = 0; dx < 3 && x + dx < W; dx++)
                        pixels[y * W + x + dx] = 0;
            }
        }

        if (upsideDown)
        {
            // In-place vertical flip.
            var flipped = new byte[pixels.Length];
            for (int y = 0; y < H; y++)
                Array.Copy(pixels, y * W, flipped, (H - 1 - y) * W, W);
            pixels = flipped;
        }

        return new VipsImage
        {
            Width = W, Height = H, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            PixelsLazy = new Lazy<byte[]>(() => pixels),
        };
    }

    [Fact]
    public void Compute_UprightPage_GivesPositiveSignal()
    {
        var page = BuildPageLikeText(upsideDown: false);
        var signal = VipsDetectUpright.Compute(page);
        Assert.True(signal > 0.05, $"Expected positive upright signal, got {signal:F4}");
    }

    [Fact]
    public void Compute_FlippedPage_GivesNegativeSignal()
    {
        var page = BuildPageLikeText(upsideDown: true);
        var signal = VipsDetectUpright.Compute(page);
        Assert.True(signal < -0.05, $"Expected negative upright signal, got {signal:F4}");
    }

    [Fact]
    public void IsUpsideDown_TriggersOnFlippedPage()
    {
        Assert.False(VipsDetectUpright.IsUpsideDown(BuildPageLikeText(upsideDown: false)));
        Assert.True (VipsDetectUpright.IsUpsideDown(BuildPageLikeText(upsideDown: true)));
    }

    [Fact]
    public void Compute_BlankPage_ReturnsZero()
    {
        // No ink → no signal.
        var pixels = new byte[300 * 300];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = 255;
        var blank = new VipsImage
        {
            Width = 300, Height = 300, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            PixelsLazy = new Lazy<byte[]>(() => pixels),
        };
        Assert.Equal(0.0, VipsDetectUpright.Compute(blank));
    }
}
