using System;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 188 — LBB (Locally Bounded Bicubic) edge-preserving
/// interpolator. Same 4×4 Catmull-Rom kernel as
/// <see cref="VipsKernel.Cubic"/>, but the per-pixel weighted sum
/// gets clamped to the bandwise min/max of the inner 2×2 pixels
/// nearest the sample point.
///
/// <para>Tests pin the key property: on a sharp 0/255 step edge,
/// Cubic overshoots beyond the inner-2×2 range (in floating-point;
/// the byte cast then clamps the visible value to 0..255), while
/// LBB produces samples strictly within the inner-2×2 range.
/// On smooth content, LBB and Cubic should agree (no overshoot to
/// clamp).</para>
/// </summary>
public class Round188Tests
{
    /// <summary>Step-edge image: half black, half white, sharp transition.</summary>
    private static VipsImage StepEdge(int w, int h, int edgeX)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) =>
            {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        int gx = reg.Valid.Left + x;
                        addr[x] = (byte)(gx < edgeX ? 0 : 255);
                    }
                }
                return 0;
            }
        };

    private static byte ReadPel(VipsImage img, int x, int y)
    {
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, img.Width, img.Height));
        return reg.GetAddress(x, y)[0];
    }

    [Fact]
    public void Lbb_OnStepEdge_BoundsByInnerTwoByTwoRange()
    {
        // Step from 0 to 255 at x=8. After 2× upscale, every output
        // sample's inner-2×2 in source space is one of:
        //   • {0,0,0,0} (left of edge)              → output = 0
        //   • {255,255,255,255} (right of edge)     → output = 255
        //   • spans the step → inner range = [0,255], so any
        //     intermediate value is permitted
        // LBB-OFF-the-edge must therefore be exactly 0 or 255.
        var src = StepEdge(16, 1, edgeX: 8);
        var resized = src.Resize(2.0, kernel: VipsKernel.Lbb);

        // Off-edge zones: x < 14 (well left of edge) and x ≥ 18 (well
        // right). Output 2× = 32 cols; the inner-2×2 for source x in
        // [0..6] is uniformly 0; for source x in [9..15] uniformly 255.
        // After 2× resize, output pixels with srcX in [0..6] = output
        // x in [0..13]. With srcX in [9..15] = output x in [18..30].
        for (int x = 0; x <= 13; x++)
            Assert.Equal(0, ReadPel(resized, x, 0));
        for (int x = 18; x <= 30; x++)
            Assert.Equal(255, ReadPel(resized, x, 0));
    }

    [Fact]
    public void Cubic_OnStepEdge_HasIntermediateRingValues()
    {
        // Sanity check on the comparison: plain Catmull-Rom Cubic will
        // produce overshoots that, after the byte-clamp at output, give
        // intermediate values around the edge. This isn't necessarily
        // *visible* (byte clipping hides it past 0/255) but along the
        // approach to the edge there are clearly-not-{0,255} samples.
        var src = StepEdge(16, 1, edgeX: 8);
        var resized = src.Resize(2.0, kernel: VipsKernel.Cubic);

        bool sawIntermediate = false;
        for (int x = 0; x < resized.Width; x++)
        {
            byte v = ReadPel(resized, x, 0);
            if (v > 50 && v < 205) { sawIntermediate = true; break; }
        }
        Assert.True(sawIntermediate, "Cubic should ring around the step edge");
    }

    [Fact]
    public void Lbb_OnSmoothGradient_MatchesCubic()
    {
        // No overshoot to clamp on a smooth ramp → LBB and Cubic
        // should be pixel-identical.
        var src = new VipsImage
        {
            Width = 16, Height = 1, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) =>
            {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                        addr[x] = (byte)((reg.Valid.Left + x) * 16);
                }
                return 0;
            }
        };
        var lbbResized = src.Resize(2.0, kernel: VipsKernel.Lbb);
        var cubicResized = src.Resize(2.0, kernel: VipsKernel.Cubic);

        for (int x = 0; x < lbbResized.Width; x++)
        {
            // Allow ±1 byte difference from rounding noise.
            int diff = ReadPel(lbbResized, x, 0) - ReadPel(cubicResized, x, 0);
            Assert.InRange(diff, -1, 1);
        }
    }

    [Fact]
    public void Lbb_SupportMatchesCubic()
    {
        // Cosmetic but worth pinning: LBB's window is 4×4, same as
        // Cubic. If someone widens it, the inner-2×2 logic must stay
        // anchored to the correct nearest-pixel pair.
        Assert.Equal(2, CosmoImage.Core.VipsKernels.Support(VipsKernel.Lbb));
        Assert.Equal(2, CosmoImage.Core.VipsKernels.Support(VipsKernel.Cubic));
    }
}
