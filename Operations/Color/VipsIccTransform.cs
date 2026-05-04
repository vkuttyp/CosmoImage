using System;
using System.IO;
using System.Runtime.CompilerServices;
using CosmoImage.Operations.Metadata;
using ImageMagick;

namespace CosmoImage.Operations.Color;

public class VipsIccTransform : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public byte[]? InputProfile { get; set; }
    public byte[]? OutputProfile { get; set; }
    public VipsInterpretation Intent { get; set; } = VipsInterpretation.SRGB;

    public override int Build()
    {
        if (In == null || OutputProfile == null) return -1;

        // Pre-build a pure CMM if possible. Try Matrix/TRC first (the 90%
        // case — sRGB / AdobeRGB / Display-P3 / etc., precomputed LUTs);
        // fall through to the LUT-based CMM (printer / scanner profiles
        // using mft2); finally fall back to Magick for anything else
        // (mAB / mBA, CMYK destination, Lab in unusual encodings).
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
                    lutCmm = VipsIccLutCmm.TryBuild(srcParsed, dstParsed);
            }
        }

        // Determine destination band count. Matrix/TRC always lands in
        // 3-channel RGB; LUT path may produce CMYK (4 channels). Both
        // pass through alpha when the source carries it.
        int dstBands = 3;
        if (lutCmm != null) dstBands = lutCmm.DstChannels;
        if (In.Bands == dstBands + 1 || In.Bands == 4 && dstBands == 3) dstBands = In.Bands;

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
                InputProfile = InputProfile,
                OutputProfile = OutputProfile,
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
        public byte[]? InputProfile;
        public byte[]? OutputProfile;
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
        if (ctx.LutCmm != null)
            return GeneratePureLutCmm(inRegion, outRegion, r, srcBands, dstBands, ctx.LutCmm);

        return GenerateMagick(inRegion, outRegion, r, srcBands, ctx.InputProfile, ctx.OutputProfile!);
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

    private static int GenerateMagick(VipsRegion inRegion, VipsRegion outRegion,
        VipsRect r, int bands, byte[]? inputProfile, byte[] outputProfile)
    {
        // Magick fallback for LUT-based / Lab / CMYK / grayscale profiles.
        // Re-encodes the region through MagickImage to apply the transform —
        // slow but correct for profile types the pure CMM doesn't model.
        byte[] pix = new byte[r.Width * r.Height * bands];
        for (int y = 0; y < r.Height; y++)
        {
            var line = inRegion.GetAddress(r.Left, r.Top + y);
            line.Slice(0, r.Width * bands).CopyTo(pix.AsSpan(y * r.Width * bands));
        }

        using var magickImage = new MagickImage();
        var settings = new MagickReadSettings
        {
            Width = (uint)r.Width,
            Height = (uint)r.Height,
            Format = bands == 3 ? MagickFormat.Rgb : (bands == 1 ? MagickFormat.Gray : MagickFormat.Rgba),
        };
        magickImage.Read(pix, settings);

        if (inputProfile != null)
            magickImage.SetProfile(new ColorProfile(inputProfile));
        magickImage.SetProfile(new ColorProfile(outputProfile));

        using var outPixels = magickImage.GetPixels();
        var outData = outPixels.ToByteArray(0, 0, (uint)r.Width, (uint)r.Height, "RGB");
        if (outData == null) return -1;

        int outBands = 3;
        for (int y = 0; y < r.Height; y++)
        {
            var destLine = outRegion.GetAddress(r.Left, r.Top + y);
            outData.AsSpan(y * r.Width * outBands, r.Width * outBands).CopyTo(destLine);
        }
        return 0;
    }
}
