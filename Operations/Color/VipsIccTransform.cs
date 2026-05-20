using System;
using System.IO;
using System.Runtime.CompilerServices;
using CosmoImage.Operations.Metadata;

namespace CosmoImage.Operations.Color;

public class VipsIccTransform : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public byte[]? InputProfile { get; set; }
    public byte[]? OutputProfile { get; set; }
    public VipsInterpretation Intent { get; set; } = VipsInterpretation.SRGB;
    /// <summary>
    /// ICC rendering intent. Selects A2B0/A2B1/A2B2 + B2A0/B2A1/B2A2
    /// tag slots when both source and destination are LUT profiles
    /// that carry intent-specific LUTs. Defaults to Perceptual.
    /// </summary>
    public VipsIccRenderingIntent RenderingIntent { get; set; } = VipsIccRenderingIntent.Perceptual;

    /// <summary>
    /// When <c>true</c>, scale shadow values so the source profile's
    /// black point maps to the destination profile's black point.
    /// Preserves shadow detail when the destination has darker
    /// representable black than the source. Only meaningful for
    /// LUT-based profiles that carry a non-zero <c>bkpt</c> tag —
    /// matrix profiles like sRGB have BP ≈ 0 and BPC has no effect.
    /// </summary>
    public bool BlackPointCompensation { get; set; }

    public override int Build()
    {
        if (In == null || OutputProfile == null) return -1;

        // Pre-build a pure CMM if possible. Try Matrix/TRC first (the 90%
        // case — sRGB / AdobeRGB / Display-P3 / etc., precomputed LUTs);
        // fall through to the LUT-based CMM (mft1/mft2 lut tables, mAB/mBA
        // multi-process elements, CMYK 4D CLUTs, Lab PCS — covered by
        // Round121–123 tests). Throw at Generate time only for the narrow
        // cases that escape both paths: channel-count mismatch with a
        // matrix profile, missing A2B/B2A tags, or 5+ channel device
        // profiles — see Generate() for the diagnostic message.
        VipsIccCmm? matrixCmm = null;
        VipsIccLutCmm? lutCmm = null;
        if (InputProfile != null)
        {
            var srcParsed = VipsIccProfile.TryParse(InputProfile);
            var dstParsed = VipsIccProfile.TryParse(OutputProfile);
            if (srcParsed != null && dstParsed != null)
            {
                matrixCmm = VipsIccCmm.TryBuild(srcParsed, dstParsed);
                if (matrixCmm == null)
                    lutCmm = VipsIccLutCmm.TryBuild(srcParsed, dstParsed, RenderingIntent,
                        BlackPointCompensation);
            }
        }

        // Determine destination band count. Matrix/TRC always lands in
        // 3-channel RGB; LUT path may produce CMYK (4 channels). Both
        // pass through alpha when the source carries it.
        int dstBands = 3;
        if (lutCmm != null) dstBands = lutCmm.DstChannels;
        if (In.Bands == dstBands + 1 || In.Bands == 4 && dstBands == 3) dstBands = In.Bands;
        // Gray+alpha (2-band) through a 3-channel matrix CMM: preserve alpha
        // so the output is RGBA (4 bands), not just RGB. Pure-gray (1-band)
        // through matrix CMM stays at 3 (no alpha to preserve).
        else if (matrixCmm != null && In.Bands == 2 && dstBands == 3) dstBands = 4;

        Out = new VipsImage
        {
            Width = In.Width,
            Height = In.Height,
            Bands = dstBands,
            BandFormat = VipsBandFormat.UChar,
            Interpretation = Intent,
            Coding = In.Coding,
            XRes = In.XRes,
            YRes = In.YRes,
            StartFn = VipsSeq.StartOne,
            GenerateFn = Generate,
            StopFn = VipsSeq.StopOne,
            ClientA = In,
            ClientB = new TransformContext
            {
                Cmm = matrixCmm,
                LutCmm = lutCmm,
                DstBands = dstBands,
            },
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);

        return 0;
    }

    private sealed class TransformContext
    {
        public VipsIccCmm? Cmm;
        public VipsIccLutCmm? LutCmm;
        public int DstBands;
    }

    public override int GetCacheKey()
    {
        var hash = new HashCode();
        hash.Add("IccTransform");
        hash.Add(RuntimeHelpers.GetHashCode(In));
        if (InputProfile != null) hash.Add(InputProfile.Length);
        if (OutputProfile != null) hash.Add(OutputProfile.Length);
        hash.Add(Intent);
        hash.Add(BlackPointCompensation);
        return hash.ToHashCode();
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        var ctx = (TransformContext)b!;
        VipsRect r = outRegion.Valid;

        if (inRegion.Prepare(r) != 0) return -1;

        int srcBands = @in.Bands;
        int dstBands = ctx.DstBands;
        if (ctx.Cmm != null && srcBands == dstBands && (srcBands == 3 || srcBands == 4))
            return GeneratePureCmm(inRegion, outRegion, r, srcBands, ctx.Cmm);
        // Gray (or gray + alpha) input through an RGB matrix profile: replicate
        // the gray byte to all three channels before the matrix multiply. The
        // output is 3-band RGB (or 4-band RGBA with the source alpha preserved).
        if (ctx.Cmm != null && (srcBands == 1 || srcBands == 2)
            && (dstBands == 3 || dstBands == 4))
            return GeneratePureCmmFromGray(inRegion, outRegion, r, srcBands, dstBands, ctx.Cmm);
        if (ctx.LutCmm != null)
            return GeneratePureLutCmm(inRegion, outRegion, r, srcBands, dstBands, ctx.LutCmm);

        // We reach here only when both CMMs failed to build/match. The pure
        // CMM stack covers Matrix/TRC (sRGB-family), mft1/mft2 lut tables,
        // mAB/mBA multi-process elements, CMYK 4D CLUTs, Lab PCS, BPC, and
        // rendering-intent selection — covered by Round121–123 + Round137
        // tests. The realistic triggers for this throw are narrow:
        //
        //   • Matrix profile matched but srcBands isn't 3 or 4 (e.g.
        //     1-band gray input through an RGB matrix profile).
        //   • srcBands ≠ dstBands and no LUT path applies (matrix profiles
        //     can't change channel count).
        //   • Profile pair has no A2B/B2A tag in any rendering intent and
        //     also isn't a matrix profile (uncommon — most v2 / v4
        //     profiles carry at least the perceptual tag).
        //   • Device profiles with 5+ channels (Hexachrome, n-ink) —
        //     VipsIccLutCmm caps SrcChannels/DstChannels at 4.
        throw new NotSupportedException(
            $"ICC transform: cannot build a pure-managed CMM for this profile pair " +
            $"with srcBands={srcBands}, dstBands={dstBands}. " +
            $"Matrix={(ctx.Cmm != null ? "built" : "not buildable")}, " +
            $"LUT={(ctx.LutCmm != null ? "built" : "not buildable")}. " +
            "Likely cause: channel-count mismatch (e.g. 1-band gray through an RGB matrix " +
            "profile), profile pair missing A2B/B2A tags, or a 5+ channel device profile. " +
            "Convert to a 3- or 4-band image first, or extend VipsIccLutCmm's channel cap.");
    }

    /// <summary>
    /// Pure-CMM path for LUT-based profiles. Same row-by-row pattern as
    /// the Matrix/TRC fast path but goes through the n-linear-CLUT
    /// pipeline. Source and destination band counts may differ
    /// (RGB → CMYK or vice versa).
    /// </summary>
    private static int GeneratePureLutCmm(VipsRegion inRegion, VipsRegion outRegion,
        VipsRect r, int srcBands, int dstBands, VipsIccLutCmm cmm)
    {
        int srcRowBytes = r.Width * srcBands;
        int dstRowBytes = r.Width * dstBands;
        var rowBuf = new byte[srcRowBytes];
        var dstBuf = new byte[dstRowBytes];
        for (int y = 0; y < r.Height; y++)
        {
            var inLine = inRegion.GetAddress(r.Left, r.Top + y);
            inLine.Slice(0, srcRowBytes).CopyTo(rowBuf);
            cmm.Apply(rowBuf, 0, srcBands, dstBuf, 0, dstBands, r.Width);
            var outLine = outRegion.GetAddress(r.Left, r.Top + y);
            dstBuf.AsSpan(0, dstRowBytes).CopyTo(outLine);
        }
        return 0;
    }

    /// <summary>
    /// Auto-promote 1-band gray (or 2-band gray+alpha) input to 3 (or 4)
    /// channels before running the matrix CMM. Common workflow: applying
    /// an sRGB ICC transform to a grayscale image — without this branch the
    /// shape mismatch would have thrown.
    /// </summary>
    private static int GeneratePureCmmFromGray(VipsRegion inRegion, VipsRegion outRegion,
        VipsRect r, int srcBands, int dstBands, VipsIccCmm cmm)
    {
        bool hasAlpha = srcBands == 2;
        int matrixBands = hasAlpha ? 4 : 3;
        int srcRowBytes = r.Width * srcBands;
        int matrixRowBytes = r.Width * matrixBands;
        int dstRowBytes = r.Width * dstBands;
        var expanded = new byte[matrixRowBytes];
        var dstBuf = new byte[matrixRowBytes];
        for (int y = 0; y < r.Height; y++)
        {
            var inLine = inRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                int sp = x * srcBands;
                int dp = x * matrixBands;
                byte g = inLine[sp];
                expanded[dp + 0] = g;
                expanded[dp + 1] = g;
                expanded[dp + 2] = g;
                if (hasAlpha) expanded[dp + 3] = inLine[sp + 1];
            }
            cmm.Apply(expanded, 0, dstBuf, 0, r.Width, matrixBands);
            var outLine = outRegion.GetAddress(r.Left, r.Top + y);
            dstBuf.AsSpan(0, dstRowBytes).CopyTo(outLine);
        }
        return 0;
    }

    /// <summary>
    /// Pure-CMM path: copy the source region row-by-row into a tight
    /// buffer, run <see cref="VipsIccCmm.Apply"/>, then write it back.
    /// Avoids the costly Magick decode/encode round-trip used by the
    /// fallback path.
    /// </summary>
    private static int GeneratePureCmm(VipsRegion inRegion, VipsRegion outRegion,
        VipsRect r, int bands, VipsIccCmm cmm)
    {
        int rowBytes = r.Width * bands;
        var rowBuf = new byte[rowBytes];
        var dstBuf = new byte[rowBytes];
        for (int y = 0; y < r.Height; y++)
        {
            var inLine = inRegion.GetAddress(r.Left, r.Top + y);
            inLine.Slice(0, rowBytes).CopyTo(rowBuf);
            cmm.Apply(rowBuf, 0, dstBuf, 0, r.Width, bands);
            var outLine = outRegion.GetAddress(r.Left, r.Top + y);
            dstBuf.AsSpan(0, rowBytes).CopyTo(outLine);
        }
        return 0;
    }

}
