using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Color;

/// <summary>
/// Bulk band-count / band-order conversions at the pixel-format
/// level. Mirrors the conversion methods on ImageSharp's
/// <c>PixelOperations&lt;TPixel&gt;</c>.
///
/// <para>Where this differs from <see cref="VipsCast"/>:
/// <c>VipsCast</c> changes the band format (UChar↔Float) but not
/// the band count. The methods here change band count and order —
/// e.g. RGB → grayscale (3 → 1), RGB → RGBA (3 → 4 with opaque
/// alpha), RGBA → BGRA (channel reorder).</para>
///
/// <para>UChar 1 / 2 / 3 / 4-band inputs supported. Float input is
/// rejected for the colour-mixing conversions; cast first if
/// needed.</para>
/// </summary>
public class VipsPixelOperations : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public int TargetBands { get; set; }
    /// <summary>Optional channel-permutation: index = output band, value = input band.</summary>
    public int[]? Permutation { get; set; }

    public override int Build()
    {
        if (In == null) return -1;
        if (In.BandFormat != VipsBandFormat.UChar) return -1;
        if (TargetBands < 1 || TargetBands > 4) return -1;
        if (Permutation != null && Permutation.Length != TargetBands) return -1;

        Out = new VipsImage
        {
            Width = In.Width, Height = In.Height,
            Bands = TargetBands, BandFormat = VipsBandFormat.UChar,
            Interpretation = TargetBands switch
            {
                1 => VipsInterpretation.BW,
                2 => VipsInterpretation.BW,
                _ => VipsInterpretation.RGB,
            },
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In, ClientB = (TargetBands, Permutation),
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
    {
        var h = new HashCode();
        h.Add("PixelOperations"); h.Add(RuntimeHelpers.GetHashCode(In)); h.Add(TargetBands);
        if (Permutation != null) foreach (var p in Permutation) h.Add(p);
        return h.ToHashCode();
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inReg = (VipsRegion)seq!;
        var (targetBands, perm) = ((int, int[]?))b!;
        VipsImage @in = inReg.Image;
        VipsRect r = outRegion.Valid;
        if (inReg.Prepare(r) != 0) return -1;

        int srcBands = @in.Bands;

        // If permutation is supplied, take that path (no luminance
        // conversion — channels are picked by index).
        if (perm != null)
        {
            for (int y = 0; y < r.Height; y++)
            {
                var ia = inReg.GetAddress(r.Left, r.Top + y);
                var oa = outRegion.GetAddress(r.Left, r.Top + y);
                for (int x = 0; x < r.Width; x++)
                {
                    int srcOff = x * srcBands;
                    int dstOff = x * targetBands;
                    for (int o = 0; o < targetBands; o++)
                    {
                        int s = perm[o];
                        oa[dstOff + o] = s >= 0 && s < srcBands ? ia[srcOff + s] : (byte)255;
                    }
                }
            }
            return 0;
        }

        // No permutation — band-count change with luminance / opaque-alpha
        // synthesis as appropriate.
        for (int y = 0; y < r.Height; y++)
        {
            var ia = inReg.GetAddress(r.Left, r.Top + y);
            var oa = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                int srcOff = x * srcBands;
                int dstOff = x * targetBands;
                ConvertPel(ia, srcOff, srcBands, oa, dstOff, targetBands);
            }
        }
        return 0;
    }

    /// <summary>
    /// Per-pixel band-count conversion. Synthesises luminance via
    /// BT.601 (Y = 0.299·R + 0.587·G + 0.114·B); synthesises an
    /// opaque alpha when growing RGB→RGBA or L→LA.
    /// </summary>
    private static void ConvertPel(
        ReadOnlySpan<byte> src, int srcOff, int srcBands,
        Span<byte> dst, int dstOff, int dstBands)
    {
        // Decode the source into normalised (R, G, B, A).
        byte sR, sG, sB, sA;
        switch (srcBands)
        {
            case 1: sR = sG = sB = src[srcOff]; sA = 255; break;
            case 2: sR = sG = sB = src[srcOff + 0]; sA = src[srcOff + 1]; break;
            case 3: sR = src[srcOff + 0]; sG = src[srcOff + 1]; sB = src[srcOff + 2]; sA = 255; break;
            default:
                sR = src[srcOff + 0]; sG = src[srcOff + 1]; sB = src[srcOff + 2]; sA = src[srcOff + 3]; break;
        }

        // BT.601 luminance for any L-flavoured target.
        byte luminance = (byte)((sR * 299 + sG * 587 + sB * 114 + 500) / 1000);

        switch (dstBands)
        {
            case 1: dst[dstOff] = luminance; break;
            case 2: dst[dstOff] = luminance; dst[dstOff + 1] = sA; break;
            case 3: dst[dstOff] = sR; dst[dstOff + 1] = sG; dst[dstOff + 2] = sB; break;
            case 4: dst[dstOff] = sR; dst[dstOff + 1] = sG; dst[dstOff + 2] = sB; dst[dstOff + 3] = sA; break;
        }
    }
}
