using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Misc;

/// <summary>
/// Reverse the byte order of every multi-byte sample. UChar passes
/// through unchanged (single-byte samples have no endianness). Float
/// reverses every 4-byte group. Mirrors libvips <c>vips_byteswap</c>.
///
/// <para>Useful when consuming raw binary blobs (FITS, NIfTI, custom
/// recorders) that were written on a different-endian host than the
/// reader.</para>
/// </summary>
public class VipsByteswap : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }

    public override int Build()
    {
        if (In == null) return -1;
        // UChar samples have no endian — short-circuit to a pass-through.
        if (In.BandFormat == VipsBandFormat.UChar) { Out = In; return 0; }

        Out = new VipsImage
        {
            Width = In.Width, Height = In.Height,
            Bands = In.Bands, BandFormat = In.BandFormat,
            Interpretation = In.Interpretation,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Byteswap", RuntimeHelpers.GetHashCode(In));

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        VipsRect r = outRegion.Valid;
        if (inRegion.Prepare(r) != 0) return -1;

        // Float: reverse every 4-byte group.
        int sampleSize = @in.SizeOfPel / @in.Bands;
        int rowSamples = r.Width * @in.Bands;

        for (int y = 0; y < r.Height; y++)
        {
            var inAddr = inRegion.GetAddress(r.Left, r.Top + y);
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int s = 0; s < rowSamples; s++)
            {
                int off = s * sampleSize;
                for (int k = 0; k < sampleSize; k++)
                    outAddr[off + k] = inAddr[off + sampleSize - 1 - k];
            }
        }
        return 0;
    }
}
