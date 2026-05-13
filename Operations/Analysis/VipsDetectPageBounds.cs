using System;
using CosmoImage.Core;
using CosmoImage.Operations.Geometric;

namespace CosmoImage.Operations.Analysis;

/// <summary>
/// Locate the rectangular page within a scan image by projecting the count
/// of pixels that are clearly *not* bed-coloured — i.e. either paper-bright
/// (above <see cref="DefaultBrightThreshold"/>) or content-dark (below
/// <see cref="DefaultDarkThreshold"/>) — per row and per column. Designed
/// to be called <em>after</em> <see cref="VipsDeskew"/> so the page is
/// approximately axis-aligned.
///
/// <para>The key insight: scan beds sit in the medium-luma band (~200
/// on the Xerox WC 3025's near-white cover; darker on others) and have
/// essentially no pixels in either tail. Paper rows always contain
/// *something* outside the bed band — bright pixels (white margins,
/// page background between text) or dark pixels (text strokes,
/// photos, solid colour blocks like coloured footer bars). Either way,
/// real page rows accumulate many "non-bed" pixels; pure-bed rows
/// don't. Counting them per row/column gives a clean step at the actual
/// page boundary regardless of how content-heavy the page is.</para>
///
/// <para>Algorithm:</para>
/// <list type="number">
///   <item>Convert to single-band UChar, downscale to <see cref="DefaultAnalysisMaxDim"/>.</item>
///   <item>For each row, count pixels with luma > <see cref="DefaultBrightThreshold"/>
///     OR luma &lt; <see cref="DefaultDarkThreshold"/>. Same for each column.</item>
///   <item>Find the largest contiguous block of rows whose non-bed count
///     exceeds <see cref="DefaultMinNonBedFraction"/> of the perpendicular
///     dimension, with small gaps absorbed up to
///     <see cref="DefaultGapTolerance"/>. Same for columns.</item>
///   <item>Inset by <see cref="DefaultInsetFraction"/> per side to clear
///     the page-edge shadow line.</item>
/// </list>
///
/// <para>Failure mode: a page filled entirely with mid-luma content
/// (a uniform gray rectangle at exactly bed-luma — vanishingly rare in
/// practice) is indistinguishable from the bed and the detector falls
/// back to the full-image rect ("don't crop").</para>
/// </summary>
public static class VipsDetectPageBounds
{
    /// <summary>Long-side cap for the analysis copy. Cost is O(W·H); 1000 px
    /// puts the whole detector under 30 ms.</summary>
    public const int DefaultAnalysisMaxDim = 1000;

    /// <summary>Luma above this counts as "paper-bright". 230 sits well
    /// above the brightest scan-bed gray we've measured (~215 on the
    /// Xerox WC 3025) and well below paper white (250+), so the cut is
    /// crisp on the typical document scan.</summary>
    public const int DefaultBrightThreshold = 230;

    /// <summary>Luma below this counts as "content-dark". 130 sits well
    /// above the darkest text/photo/coloured-block pixels (≤100) and well
    /// below the darkest typical bed values (~150), so it catches solid
    /// dark content blocks (like a coloured footer bar) without admitting
    /// bed pixels.</summary>
    public const int DefaultDarkThreshold = 130;

    /// <summary>A row must accumulate this fraction of the image width in
    /// non-bed pixels (bright or dark) to count as "page". 10% on a 1000-px
    /// scan = 100 non-bed pixels — well above what bed-banding contributes
    /// and well below what any real page row contains.</summary>
    public const double DefaultMinNonBedFraction = 0.10;

    /// <summary>Per-side inset as a fraction of the bbox dimension. Trims
    /// the page-edge shadow that the bright-pixel scan sometimes catches
    /// just outside the paper.</summary>
    public const double DefaultInsetFraction = 0.01;

    /// <summary>Maximum gap (in analysis pixels) between passing rows/columns
    /// that still counts as the same block. Lets the largest-block selector
    /// span content-dense rows that dip below the bright threshold (e.g.
    /// a dark photo or a solid coloured footer bar) without splitting the
    /// page into multiple pieces. 100 px at the 1000-px analysis cap =
    /// 10% of the long side — comfortably wider than any typical content
    /// block, comfortably narrower than the dead-bed gap between a
    /// corner-placed page and scanner-edge calibration artefacts.</summary>
    public const int DefaultGapTolerance = 100;

    /// <summary>
    /// Find the page bounding rectangle. Returns <c>VipsRect(0, 0, W, H)</c>
    /// if no row or column accumulates enough non-bed pixels.
    /// </summary>
    public static VipsRect Compute(VipsImage input,
        int brightThreshold = DefaultBrightThreshold,
        int darkThreshold = DefaultDarkThreshold,
        double minNonBedFraction = DefaultMinNonBedFraction,
        double insetFraction = DefaultInsetFraction,
        int gapTolerance = DefaultGapTolerance)
    {
        ArgumentNullException.ThrowIfNull(input);

        int origW = input.Width, origH = input.Height;
        var fallback = new VipsRect(0, 0, origW, origH);

        // ---- 1. Reduce to UChar single-band -------------------------------
        var gray = input;
        if (gray.Bands != 1 || gray.Interpretation != VipsInterpretation.BW)
            gray = VipsImageOps.Colourspace(gray, VipsInterpretation.BW);
        if (gray.BandFormat != VipsBandFormat.UChar)
            gray = VipsImageOps.Cast(gray, VipsBandFormat.UChar);

        // ---- 2. Downscale --------------------------------------------------
        double scale = 1.0;
        int maxDim = Math.Max(gray.Width, gray.Height);
        if (maxDim > DefaultAnalysisMaxDim)
        {
            scale = (double)DefaultAnalysisMaxDim / maxDim;
            gray = VipsImageOps.Resize(gray, scale);
        }

        // ---- 3. Materialise pixels ----------------------------------------
        byte[] pixels;
        if (gray.Pixels is { } existing) pixels = existing;
        else
        {
            var sink = new MemorySink(gray);
            sink.RunAsync().GetAwaiter().GetResult();
            pixels = sink.Pixels;
        }

        int W = gray.Width, H = gray.Height;
        byte brightLimit = (byte)Math.Clamp(brightThreshold, 0, 255);
        byte darkLimit   = (byte)Math.Clamp(darkThreshold,   0, 255);

        // ---- 4. Per-row / per-column non-bed pixel counts ------------------
        // A pixel is "non-bed" if it's clearly paper-bright OR clearly
        // content-dark; bed luma sits between the two thresholds.
        var rowNonBed = new int[H];
        var colNonBed = new int[W];
        for (int y = 0; y < H; y++)
        {
            int rowOff = y * W;
            for (int x = 0; x < W; x++)
            {
                byte v = pixels[rowOff + x];
                if (v > brightLimit || v < darkLimit)
                {
                    rowNonBed[y]++;
                    colNonBed[x]++;
                }
            }
        }

        // ---- 5. Largest contiguous block of passing rows / columns ---------
        // Scanner edge calibration sometimes produces a few sporadic
        // passing rows at the image extremes that fool a naive first/last
        // scan. The actual page is a long *contiguous* run of passing
        // rows; sparse artefact rows aren't. Pick the longest run,
        // tolerating small interior gaps (mid-luma blocks that aren't
        // quite bright and aren't quite dark).
        int rowMin = Math.Max(1, (int)Math.Ceiling(W * minNonBedFraction));
        int colMin = Math.Max(1, (int)Math.Ceiling(H * minNonBedFraction));

        var (top,   bottom) = LargestRun(rowNonBed, rowMin, gapTolerance);
        var (left,  right)  = LargestRun(colNonBed, colMin, gapTolerance);

        if (top < 0 || bottom < 0 || left < 0 || right < 0) return fallback;
        if (bottom <= top || right <= left) return fallback;

        // ---- 6. Scale back to original-image coordinates ------------------
        int t = (int)Math.Max(0,     Math.Round(top    / scale));
        int b = (int)Math.Min(origH, Math.Round(bottom / scale) + 1);
        int l = (int)Math.Max(0,     Math.Round(left   / scale));
        int r = (int)Math.Min(origW, Math.Round(right  / scale) + 1);

        // ---- 7. Inset to drop the page-shadow line ------------------------
        int hInset = (int)Math.Round((r - l) * insetFraction);
        int vInset = (int)Math.Round((b - t) * insetFraction);
        t += vInset; b -= vInset;
        l += hInset; r -= hInset;

        if (r <= l || b <= t) return fallback;
        return new VipsRect(l, t, r - l, b - t);
    }

    /// <summary>
    /// Scan <paramref name="counts"/>, identify all runs of indices whose
    /// value is at least <paramref name="threshold"/>, merge runs separated
    /// by gaps of at most <paramref name="gapTolerance"/> indices, and
    /// return the (start, end) of the longest merged run. Returns
    /// (-1, -1) when no index clears the threshold.
    /// </summary>
    private static (int Start, int End) LargestRun(int[] counts, int threshold, int gapTolerance)
    {
        int bestStart = -1, bestEnd = -1, bestLen = 0;
        int runStart = -1, runEnd = -1;
        int gap = 0;
        for (int i = 0; i < counts.Length; i++)
        {
            if (counts[i] >= threshold)
            {
                if (runStart < 0) runStart = i;
                runEnd = i;
                gap = 0;
            }
            else if (runStart >= 0)
            {
                gap++;
                if (gap > gapTolerance)
                {
                    int len = runEnd - runStart + 1;
                    if (len > bestLen) { bestStart = runStart; bestEnd = runEnd; bestLen = len; }
                    runStart = -1;
                    gap = 0;
                }
            }
        }
        if (runStart >= 0)
        {
            int len = runEnd - runStart + 1;
            if (len > bestLen) { bestStart = runStart; bestEnd = runEnd; }
        }
        return (bestStart, bestEnd);
    }
}
