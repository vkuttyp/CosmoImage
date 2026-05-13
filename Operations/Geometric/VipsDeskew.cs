using System;
using System.Buffers.Binary;
using CosmoImage.Core;
using CosmoImage.Operations.Analysis;

namespace CosmoImage.Operations.Geometric;

/// <summary>
/// Auto-deskew: detects the dominant text/line orientation and rotates the
/// image so that it runs horizontally. The standard fix-up for documents
/// scanned slightly off-axis.
///
/// <para>Algorithm — a marginalised line Hough transform on the binarised
/// content image:</para>
/// <list type="number">
///   <item>Reduce to a single UChar band via
///     <see cref="VipsImageOps.Colourspace"/>(<see cref="VipsInterpretation.BW"/>).</item>
///   <item>Downscale so the longer side is at most
///     <see cref="DefaultAnalysisMaxDimension"/> px — Hough is O(W·H·θ),
///     so a 1000-px copy keeps detection well under a second on a typical
///     scan.</item>
///   <item>Invert (so dark text becomes the bright "voters") and threshold
///     at 128 to get a clean binary mask.</item>
///   <item>Run <see cref="VipsHoughLine"/>. Marginalise the (ρ, θ)
///     accumulator over ρ to get a 1-D θ histogram, then take the argmax
///     within ±<paramref name="maxAngleDegrees"/> of θ = 90° — the bin a
///     true-horizontal line would peak at.</item>
///   <item>Sub-bin parabolic refinement around the argmax for a more
///     accurate angle than the bin resolution would otherwise allow.</item>
///   <item>Rotate the original (full-resolution) input by <c>−skew</c>.</item>
/// </list>
///
/// <para>Notes / failure modes: assumes a content image (text, lines, or
/// strong page edges). An entirely blank or noise-only image returns a
/// skew of 0. The search is constrained to ±<paramref name="maxAngleDegrees"/>;
/// don't widen it without thought — beyond ~15° a flipped page or table
/// gridline at the wrong orientation can outvote the text baselines.</para>
/// </summary>
public static class VipsDeskew
{
    /// <summary>
    /// Longer-side cap (in pixels) for the analysis copy. The full-resolution
    /// input is still what we rotate; this only affects the Hough work.
    /// </summary>
    public const int DefaultAnalysisMaxDimension = 1200;

    /// <summary>Number of θ bins for the Hough transform — 0.5° per bin.</summary>
    public const int DefaultThetaBins = 360;

    /// <summary>Number of ρ bins. Affects peak sharpness, not orientation accuracy.</summary>
    public const int DefaultRhoBins = 400;

    /// <summary>Default ±range searched around horizontal.</summary>
    public const double DefaultMaxAngleDegrees = 10.0;

    /// <summary>
    /// Per-band absolute-difference threshold used by the post-rotate
    /// auto-crop pass. The page only survives if its pixels lie more
    /// than this many levels away from the rotation pad (black). 230
    /// cleanly separates a paper-white (≈250) page from a scan-bed
    /// gray that can range up to ~215. The pre-blur pass dims true
    /// noise pixels well below this, so the threshold can sit close
    /// to paper white without losing the boundary.
    /// </summary>
    public const int DefaultAutoCropThreshold = 230;

    /// <summary>
    /// Detect the skew angle and rotate the input to correct it, then
    /// optionally crop to just the page. The crop pass uses
    /// <see cref="VipsFindTrim"/> against a black background — both the
    /// rotation pad (filled with zero by <see cref="VipsAffine"/>) and the
    /// scan-bed gray (typically ~150) fall below
    /// <paramref name="autoCropThreshold"/>, leaving only the bright paper.
    /// </summary>
    /// <param name="input">Source image.</param>
    /// <param name="maxAngleDegrees">Half-window (±) around horizontal that
    /// the search will consider. Skews outside this range are clamped to
    /// the closest representable bin.</param>
    /// <param name="minAngleToCorrect">Below this, the rotation is skipped.
    /// The crop pass still runs (a non-rotated scan-bed image still has
    /// margin to trim). Defaults to 0.1° — avoids needless resampling
    /// jitter for already-straight scans.</param>
    /// <param name="interpolate">Resampling kernel for the corrective
    /// rotation. <see cref="VipsKernel.Linear"/> is plenty for documents;
    /// step up to Cubic/Lanczos for photo-heavy content.</param>
    /// <param name="autoCrop">If true (default), crop the result to the
    /// page bounding box. Set false to keep the rotated/un-rotated full
    /// frame (e.g. for callers that do their own framing).</param>
    /// <param name="autoCropThreshold">Brightness threshold for the crop
    /// pass — see <see cref="DefaultAutoCropThreshold"/>.</param>
    public static VipsImage Compute(
        VipsImage input,
        double maxAngleDegrees = DefaultMaxAngleDegrees,
        double minAngleToCorrect = 0.1,
        VipsKernel interpolate = VipsKernel.Linear,
        bool autoCrop = true,
        int autoCropThreshold = DefaultAutoCropThreshold)
    {
        ArgumentNullException.ThrowIfNull(input);
        var skew = DetectSkewDegrees(input, maxAngleDegrees);

        VipsImage rotated;
        if (Math.Abs(skew) < minAngleToCorrect)
        {
            rotated = input;
        }
        else
        {
            rotated = VipsImageOps.Rotate(input, -skew, interpolate);
            // Trim the rotation pad geometrically. VipsAffine fills out-of-source
            // samples with black (value 0), and downstream analysis (notably
            // VipsDetectPageBounds, which counts dark pixels per row) reads
            // those pad pixels as "content" — so we strip them before they
            // can confuse the detector.
            rotated = CropInscribedRectangle(rotated, input.Width, input.Height, Math.Abs(skew));
        }

        return autoCrop ? AutoCropDocument(rotated, autoCropThreshold) : rotated;
    }

    /// <summary>
    /// Crop a rotated image to the largest axis-aligned rectangle that fits
    /// inside the original rotated rectangle — i.e. the rectangle with no
    /// visible rotation pad on any side. Closed-form formula for the
    /// rotated W×H rectangle (with |α| &lt; 45°):
    ///   <c>inscLong  = (W·cos α − H·sin α) / cos 2α</c>
    ///   <c>inscShort = (H·cos α − W·sin α) / cos 2α</c>
    /// where W is the longer original side.
    /// </summary>
    private static VipsImage CropInscribedRectangle(
        VipsImage rotated, int origW, int origH, double absSkewDegrees)
    {
        double alpha = absSkewDegrees * Math.PI / 180.0;
        double cosA  = Math.Cos(alpha);
        double sinA  = Math.Sin(alpha);
        double cos2A = Math.Cos(2 * alpha);
        if (cos2A <= 0.001) return rotated;  // near 45° — formula degenerate

        bool wide = origW >= origH;
        double w = wide ? origW : origH;
        double h = wide ? origH : origW;

        double inscLong  = (w * cosA - h * sinA) / cos2A;
        double inscShort = (h * cosA - w * sinA) / cos2A;
        if (inscLong <= 0 || inscShort <= 0) return rotated;

        double inscW = wide ? inscLong  : inscShort;
        double inscH = wide ? inscShort : inscLong;

        int iw = (int)Math.Floor(inscW);
        int ih = (int)Math.Floor(inscH);
        if (iw <= 0 || ih <= 0)            return rotated;
        if (iw >= rotated.Width && ih >= rotated.Height) return rotated;

        int left = Math.Max(0, (rotated.Width  - iw) / 2);
        int top  = Math.Max(0, (rotated.Height - ih) / 2);
        iw = Math.Min(iw, rotated.Width  - left);
        ih = Math.Min(ih, rotated.Height - top);
        return VipsImageOps.ExtractArea(rotated, left, top, iw, ih);
    }

    /// <summary>
    /// Crop the image to the page bounding box, isolating the document from
    /// both the black rotation pad and any same-coloured scan-bed margin.
    /// Defers to <see cref="VipsDetectPageBounds"/> which finds the four
    /// axis-aligned page edges via Canny + Hough; if detection fails (no
    /// clear edges in the scan), the input is returned unchanged.
    /// </summary>
    public static VipsImage AutoCropDocument(VipsImage input, int threshold = DefaultAutoCropThreshold)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.BandFormat != VipsBandFormat.UChar) return input;

        var rect = VipsDetectPageBounds.Compute(input);
        if (rect.Width <= 0 || rect.Height <= 0) return input;
        if (rect.Width == input.Width && rect.Height == input.Height) return input;
        return VipsImageOps.ExtractArea(input, rect.Left, rect.Top, rect.Width, rect.Height);
    }

    /// <summary>
    /// Detect the skew angle in degrees without rotating. Positive values
    /// mean the image is currently rotated clockwise relative to upright
    /// (i.e. text baselines slope down to the right); pass the negation to
    /// <see cref="VipsImageOps.Rotate(VipsImage, double, VipsKernel)"/> to
    /// straighten.
    /// </summary>
    public static double DetectSkewDegrees(
        VipsImage input,
        double maxAngleDegrees = DefaultMaxAngleDegrees)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (maxAngleDegrees <= 0 || maxAngleDegrees >= 90)
            throw new ArgumentOutOfRangeException(nameof(maxAngleDegrees),
                "maxAngleDegrees must be in (0, 90).");

        // ---- 1. Reduce to UChar single-band so HoughLine accepts it ---------
        var gray = input;
        if (gray.Bands != 1 || gray.Interpretation != VipsInterpretation.BW)
            gray = VipsImageOps.Colourspace(gray, VipsInterpretation.BW);
        if (gray.BandFormat != VipsBandFormat.UChar)
            gray = VipsImageOps.Cast(gray, VipsBandFormat.UChar);

        // ---- 2. Downscale (analysis only — full-res image still gets rotated)
        int maxDim = Math.Max(gray.Width, gray.Height);
        if (maxDim > DefaultAnalysisMaxDimension)
        {
            double scale = (double)DefaultAnalysisMaxDimension / maxDim;
            gray = VipsImageOps.Resize(gray, scale);
        }

        // ---- 3. Invert + threshold so dark ink becomes bright voters --------
        var inverted = VipsImageOps.Invert(gray);
        var binary = VipsImageOps.Threshold(inverted, 128);

        // ---- 4. Hough transform --------------------------------------------
        var hough = VipsImageOps.HoughLine(
            binary,
            width: DefaultThetaBins,
            height: DefaultRhoBins,
            threshold: 128);

        // Materialise the UInt accumulator so we can index directly.
        byte[] pixels;
        if (hough.Pixels is { } existing)
        {
            pixels = existing;
        }
        else
        {
            var sink = new MemorySink(hough);
            sink.RunAsync().GetAwaiter().GetResult();
            pixels = sink.Pixels;
        }

        // ---- 5. Score each θ slice by Σ(votes²) over ρ, peak-find ----------
        // Why sum-of-squares and not plain sum: every voter contributes one
        // vote per θ no matter the orientation, so a marginal sum is roughly
        // constant across θ and useless for peak picking. Σρ(votes²) is the
        // standard projection-profile variance proxy — high when many voters
        // share the same ρ (i.e. actually lie on a line at that θ), low when
        // votes are spread evenly across ρ.
        int W = hough.Width, H = hough.Height;
        double degPerBin = 180.0 / W;     // θ ∈ [0°, 180°) over W bins
        int bin90 = W / 2;
        int searchHalf = (int)Math.Ceiling(maxAngleDegrees / degPerBin);
        int tMin = Math.Max(0, bin90 - searchHalf);
        int tMax = Math.Min(W, bin90 + searchHalf + 1);

        var thetaScore = new double[W];
        for (int y = 0; y < H; y++)
        {
            int rowOff = y * W * 4;  // UInt = 4 bytes
            for (int t = tMin; t < tMax; t++)
            {
                double v = BinaryPrimitives.ReadUInt32LittleEndian(
                    pixels.AsSpan(rowOff + t * 4, 4));
                thetaScore[t] += v * v;
            }
        }

        double best = 0;
        int peakT = bin90;
        for (int t = tMin; t < tMax; t++)
        {
            if (thetaScore[t] > best) { best = thetaScore[t]; peakT = t; }
        }
        if (best == 0) return 0.0;  // no voters → image had no edges to align

        // ---- 6. Parabolic sub-bin refinement around (peakT−1, peakT, peakT+1)
        double frac = 0.0;
        if (peakT > tMin && peakT < tMax - 1)
        {
            double yL = thetaScore[peakT - 1];
            double y0 = thetaScore[peakT];
            double yR = thetaScore[peakT + 1];
            double denom = 2.0 * (yL - 2.0 * y0 + yR);
            if (Math.Abs(denom) > 1e-9)
            {
                frac = (yL - yR) / denom;
                if (frac > 1.0) frac = 1.0;
                else if (frac < -1.0) frac = -1.0;
            }
        }

        double peakThetaDeg = degPerBin * (peakT + frac);
        // For a horizontal line, peak θ = 90°. A clockwise rotation by α
        // shifts the normal-direction θ to 90° + α (see VipsHoughLine
        // parameterisation: ρ = x·cosθ + y·sinθ).
        double skewDeg = peakThetaDeg - 90.0;
        return skewDeg;
    }
}
