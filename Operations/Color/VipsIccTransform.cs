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

        // Pre-build the pure CMM if both profiles are Matrix/TRC RGB
        // (the 90% case: sRGB / AdobeRGB / Display-P3 / etc.). This is
        // a one-time cost paid in Build, so the per-pixel inner loop
        // in Generate stays tight. Returns null for LUT-based or
        // CMYK/Lab profiles — those fall back to Magick below.
        VipsIccCmm? cmm = null;
        if (InputProfile != null)
        {
            var srcParsed = VipsIccProfile.TryParse(InputProfile);
            var dstParsed = VipsIccProfile.TryParse(OutputProfile);
            if (srcParsed != null && dstParsed != null)
                cmm = VipsIccCmm.TryBuild(srcParsed, dstParsed);
        }

        Out = new VipsImage
        {
            Width = In.Width,
            Height = In.Height,
            Bands = 3, // Target profile is usually RGB
            BandFormat = VipsBandFormat.UChar,
            Interpretation = Intent,
            Coding = In.Coding,
            XRes = In.XRes,
            YRes = In.YRes,
            StartFn = VipsSeq.StartOne,
            GenerateFn = Generate,
            StopFn = VipsSeq.StopOne,
            ClientA = In,
            ClientB = new TransformContext { InputProfile = InputProfile, OutputProfile = OutputProfile, Cmm = cmm },
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

        int bands = @in.Bands;
        if (ctx.Cmm != null && (bands == 3 || bands == 4))
            return GeneratePureCmm(inRegion, outRegion, r, bands, ctx.Cmm);

        return GenerateMagick(inRegion, outRegion, r, bands, ctx.InputProfile, ctx.OutputProfile!);
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
