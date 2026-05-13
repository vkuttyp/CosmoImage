using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using CosmoImage.Core;

namespace CosmoImage.Operations.Analysis;

/// <summary>
/// Detect whether a document image is upside-down (rotated 180°) by
/// looking at the asymmetry of ink distribution within each text line.
///
/// <para>The cue: in Latin / Arabic scripts, most character cells have
/// more ink near the <em>baseline</em> than near the cap-zone — every
/// letter ends at the baseline, but only some letters reach the
/// ascender/cap height. So in upright text, each text-line band has
/// more dark pixels in its bottom half than its top half. Flipping
/// 180° inverts that asymmetry. Averaged across all line bands on
/// the page, the sign of the imbalance gives a confidence score.</para>
///
/// <para>Output convention: <see cref="Compute"/> returns a signed
/// signal in [-1, 1]. Positive ⇒ upright; negative ⇒ upside-down;
/// near-zero ⇒ ambiguous (e.g. a CJK page where character cells are
/// roughly symmetric, or a page with no clear text bands at all).
/// <see cref="IsUpsideDown"/> wraps this with a configurable threshold
/// so callers can choose how confident they want to be before flipping.</para>
///
/// <para>Failure mode: when the image has no detectable text lines —
/// too few horizontal bands of ink, or the contrast is too low to
/// threshold cleanly — the signal collapses to 0 and the page is
/// reported as already-upright.</para>
/// </summary>
public static class VipsDetectUpright
{
    /// <summary>Long-side cap for the analysis copy. Row projection is O(W·H)
    /// — 1000 px keeps the whole detector well under 100 ms.</summary>
    public const int DefaultAnalysisMaxDim = 1000;

    /// <summary>Binary threshold for "is this pixel ink?" on the inverted image.
    /// 128 catches anything darker than mid-gray as text.</summary>
    public const int DefaultInkThreshold = 128;

    /// <summary>Row-sum fraction (of the max row) above which a row is
    /// considered part of a text band. 0.2 picks up text lines while
    /// rejecting paper-noise rows.</summary>
    public const double DefaultBandSumFraction = 0.2;

    /// <summary>Minimum band height (px in analysis coords) for a band to
    /// contribute to the signal. Filters out single-row noise bands that
    /// would dominate with garbage data.</summary>
    public const int DefaultMinBandHeight = 8;

    /// <summary>
    /// Default decision threshold for <see cref="IsUpsideDown"/>. The signal
    /// is in [-1, 1]; we require &lt; -0.02 (a clear lean) before flipping,
    /// so an ambiguous page stays put rather than being rotated by mistake.
    /// </summary>
    public const double DefaultFlipDecisionThreshold = -0.02;

    /// <summary>
    /// Return the upright-confidence signal. Positive ⇒ upright,
    /// negative ⇒ upside-down, magnitude ⇒ how clear the cue is.
    /// </summary>
    public static double Compute(VipsImage input,
        int inkThreshold = DefaultInkThreshold,
        double bandSumFraction = DefaultBandSumFraction,
        int minBandHeight = DefaultMinBandHeight)
    {
        ArgumentNullException.ThrowIfNull(input);

        // ---- 1. Single-band UChar -----------------------------------------
        var gray = input;
        if (gray.Bands != 1 || gray.Interpretation != VipsInterpretation.BW)
            gray = VipsImageOps.Colourspace(gray, VipsInterpretation.BW);
        if (gray.BandFormat != VipsBandFormat.UChar)
            gray = VipsImageOps.Cast(gray, VipsBandFormat.UChar);

        // ---- 2. Downscale --------------------------------------------------
        int maxDim = Math.Max(gray.Width, gray.Height);
        if (maxDim > DefaultAnalysisMaxDim)
            gray = VipsImageOps.Resize(gray, (double)DefaultAnalysisMaxDim / maxDim);

        // ---- 3. Invert + threshold so ink = 255 ---------------------------
        var inverted = VipsImageOps.Invert(gray);
        var binary = VipsImageOps.Threshold(inverted, inkThreshold);

        // ---- 4. Row projection (sums per row, Float) ----------------------
        var (_, rows) = VipsImageOps.Project(binary);
        byte[] rowBytes;
        if (rows.Pixels is { } existing) rowBytes = existing;
        else
        {
            var sink = new MemorySink(rows);
            sink.RunAsync().GetAwaiter().GetResult();
            rowBytes = sink.Pixels;
        }
        int H = binary.Height;
        var rowSums = new double[H];
        double maxSum = 0;
        for (int y = 0; y < H; y++)
        {
            float v = BinaryPrimitives.ReadSingleLittleEndian(rowBytes.AsSpan(y * 4, 4));
            rowSums[y] = v;
            if (v > maxSum) maxSum = v;
        }
        if (maxSum <= 0) return 0.0;  // empty / pure-background page

        // ---- 5. Find text-line bands as contiguous rows of high ink -------
        double bandThreshold = maxSum * bandSumFraction;
        var bands = new List<(int Start, int End)>();
        int bandStart = -1;
        for (int y = 0; y < H; y++)
        {
            bool inBand = rowSums[y] >= bandThreshold;
            if (inBand)
            {
                if (bandStart < 0) bandStart = y;
            }
            else if (bandStart >= 0)
            {
                if (y - bandStart >= minBandHeight) bands.Add((bandStart, y - 1));
                bandStart = -1;
            }
        }
        if (bandStart >= 0 && H - bandStart >= minBandHeight) bands.Add((bandStart, H - 1));
        if (bands.Count == 0) return 0.0;

        // ---- 6. Per-band top-half vs bottom-half asymmetry ----------------
        // Each character bottoms out at the baseline, but only some reach
        // the cap-line — so a band's lower half holds more ink than its
        // upper half when the page is upright. We aggregate the normalised
        // (bot − top)/(bot + top) imbalance over all bands.
        double signal = 0;
        int counted = 0;
        foreach (var (start, end) in bands)
        {
            int height = end - start + 1;
            if (height < minBandHeight) continue;
            int mid = start + height / 2;
            double topSum = 0, botSum = 0;
            for (int y = start; y < mid; y++) topSum += rowSums[y];
            for (int y = mid; y <= end; y++) botSum += rowSums[y];
            double total = topSum + botSum;
            if (total < 1) continue;
            signal += (botSum - topSum) / total;
            counted++;
        }
        return counted > 0 ? signal / counted : 0.0;
    }

    /// <summary>
    /// Convenience: true if the upright-confidence signal is below
    /// <paramref name="decisionThreshold"/> (default -0.02). Callers
    /// that want a one-call "should I rotate?" can use this directly.
    /// </summary>
    public static bool IsUpsideDown(VipsImage input,
        double decisionThreshold = DefaultFlipDecisionThreshold)
        => Compute(input) < decisionThreshold;
}
