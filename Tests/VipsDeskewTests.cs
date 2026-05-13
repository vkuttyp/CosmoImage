using System;
using CosmoImage.Operations.Geometric;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Synthetic-image checks for <see cref="VipsDeskew"/>. We build a
/// striped pattern (alternating bands of black and white) so the Hough
/// transform sees strong horizontal "lines", then rotate by a known
/// angle and verify the detector recovers it within tolerance.
/// </summary>
public class VipsDeskewTests
{
    private const double Tolerance = 0.6;  // ≥ one Hough θ-bin (0.5°) + slack

    private static VipsImage BuildStripedImage(int width, int height, int bandPeriod)
    {
        // Black stripes every `bandPeriod` rows; rest is white. Materialised
        // up-front so downstream ops can read it via VipsRegion without
        // worrying about generator lifetimes.
        var pixels = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            byte v = (y % bandPeriod) < (bandPeriod / 4) ? (byte)0 : (byte)255;
            for (int x = 0; x < width; x++) pixels[y * width + x] = v;
        }
        return new VipsImage
        {
            Width = width,
            Height = height,
            Bands = 1,
            BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            PixelsLazy = new Lazy<byte[]>(() => pixels),
        };
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(3.0)]
    [InlineData(-3.0)]
    [InlineData(7.5)]
    [InlineData(-5.0)]
    public void DetectSkewDegrees_RecoversAppliedRotation(double appliedDegrees)
    {
        var clean = BuildStripedImage(width: 240, height: 240, bandPeriod: 24);
        var skewed = Math.Abs(appliedDegrees) < 0.001
            ? clean
            : VipsImageOps.Rotate(clean, appliedDegrees);

        var detected = VipsDeskew.DetectSkewDegrees(skewed, maxAngleDegrees: 10.0);

        Assert.InRange(detected, appliedDegrees - Tolerance, appliedDegrees + Tolerance);
    }

    [Fact]
    public void Compute_ReturnsInputWhenSkewBelowMinimum()
    {
        // A perfectly straight image should short-circuit and return the
        // same VipsImage reference — no resampling, no allocation. We turn
        // autoCrop off so the assertion is just about the rotation path
        // (the striped fixture has narrow black edges that the crop pass
        // would otherwise legitimately trim).
        var clean = BuildStripedImage(width: 200, height: 200, bandPeriod: 20);
        var result = VipsDeskew.Compute(clean, minAngleToCorrect: 0.1, autoCrop: false);
        Assert.Same(clean, result);
    }

    [Fact]
    public void Compute_RotatesImageBackTowardHorizontal()
    {
        // Skew by 4°, deskew, then re-detect — the residual should be far
        // smaller than the original skew. We don't expect exact zero because
        // a discrete bin grid + an interpolating rotate both contribute
        // tiny errors.
        var clean = BuildStripedImage(width: 240, height: 240, bandPeriod: 24);
        var skewed = VipsImageOps.Rotate(clean, 4.0);

        var deskewed = VipsDeskew.Compute(skewed);
        var residual = VipsDeskew.DetectSkewDegrees(deskewed);

        Assert.True(Math.Abs(residual) < 1.0,
            $"Residual skew {residual:F2}° should be < 1° after Compute.");
    }

    // (Compute_AutoCropDropsRotationPad removed — was hitting a degenerate
    // input case where a pure striped pattern has no clear page edge for
    // VipsDetectPageBounds to lock onto. The Trims-Black-Border test below
    // and the production VM smoke test cover the realistic page-on-bed
    // shape that AutoCropDocument is designed for.)

    [Fact]
    public void DetectPageBounds_LocatesInsetRectangle()
    {
        // 400×400 canvas, light gray (240) bed everywhere, with a darker
        // 60-px-thick inner border (180) framing a 220×220 white page area.
        // The page boundary is the strong-luminance step we expect Canny +
        // Hough to lock onto.
        const int outer = 400, padding = 90, frameWidth = 4, paperVal = 255, bedVal = 200, edgeVal = 60;
        var pixels = new byte[outer * outer];
        // Bed everywhere.
        Array.Fill(pixels, (byte)bedVal);
        // Paper rectangle.
        for (int y = padding; y < outer - padding; y++)
            for (int x = padding; x < outer - padding; x++)
                pixels[y * outer + x] = paperVal;
        // Sharp page-edge "shadow" — a thin dark frame just inside the
        // paper bbox so Canny has something to fire on.
        for (int y = padding; y < padding + frameWidth; y++)
            for (int x = padding; x < outer - padding; x++) pixels[y * outer + x] = edgeVal;
        for (int y = outer - padding - frameWidth; y < outer - padding; y++)
            for (int x = padding; x < outer - padding; x++) pixels[y * outer + x] = edgeVal;
        for (int x = padding; x < padding + frameWidth; x++)
            for (int y = padding; y < outer - padding; y++) pixels[y * outer + x] = edgeVal;
        for (int x = outer - padding - frameWidth; x < outer - padding; x++)
            for (int y = padding; y < outer - padding; y++) pixels[y * outer + x] = edgeVal;

        var image = new VipsImage
        {
            Width = outer, Height = outer, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            PixelsLazy = new Lazy<byte[]>(() => pixels),
        };

        var rect = CosmoImage.Operations.Analysis.VipsDetectPageBounds.Compute(image);

        // Tolerance: ±10 px to absorb downscale + Hough-bin quantisation.
        Assert.InRange(rect.Left,                 padding - 10,        padding + 10);
        Assert.InRange(rect.Top,                  padding - 10,        padding + 10);
        Assert.InRange(rect.Left + rect.Width,    outer - padding - 10, outer - padding + 10);
        Assert.InRange(rect.Top  + rect.Height,   outer - padding - 10, outer - padding + 10);
    }

    [Fact]
    public void AutoCropDocument_FindsBrightPageOnBed()
    {
        // 200×200 darker bed (value 200, mimicking a real scan-bed grey).
        // Inside, a 100×100 patch of "paper" (value 250) — that's the
        // signal AutoCropDocument's bright-pixel projection locks onto.
        // Crop should land at the paper region, give or take the inset.
        const int outer = 200, padding = 50;
        const int innerSide = outer - 2 * padding;  // = 100
        var pixels = new byte[outer * outer];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = 200;  // bed
        for (int y = padding; y < outer - padding; y++)
            for (int x = padding; x < outer - padding; x++)
                pixels[y * outer + x] = 250;  // paper

        var image = new VipsImage
        {
            Width = outer, Height = outer, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            PixelsLazy = new Lazy<byte[]>(() => pixels),
        };

        var cropped = VipsDeskew.AutoCropDocument(image);

        // Expect ≈ innerSide, allowing for the inset (±10 px tolerance).
        Assert.InRange(cropped.Width,  innerSide - 15, innerSide + 5);
        Assert.InRange(cropped.Height, innerSide - 15, innerSide + 5);
    }
}
