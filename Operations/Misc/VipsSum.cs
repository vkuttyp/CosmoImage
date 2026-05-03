using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Misc;

/// <summary>
/// Pixel-wise sum across N input images. All inputs must agree on
/// dimensions, band count, and band format. Mirrors libvips
/// <c>vips_sum</c>.
///
/// <para>Output is the same band format as the inputs, with UChar
/// clamping at 255 — same convention as <see cref="VipsArithmetic2"/>.
/// To sum many UChar images without clamp, cast to Float first
/// (<c>image.Cast(Float)</c>) and let the Float branch run.</para>
/// </summary>
public class VipsSum : VipsOperation
{
    public VipsImage[]? Inputs { get; set; }
    public VipsImage? Out { get; set; }

    public override int Build()
    {
        if (Inputs == null || Inputs.Length == 0) return -1;
        if (Inputs.Length == 1) { Out = Inputs[0]; return 0; }

        var first = Inputs[0];
        foreach (var img in Inputs)
        {
            if (img == null) return -1;
            if (img.Width != first.Width || img.Height != first.Height) return -1;
            if (img.Bands != first.Bands || img.BandFormat != first.BandFormat) return -1;
        }

        Out = new VipsImage
        {
            Width = first.Width, Height = first.Height,
            Bands = first.Bands, BandFormat = first.BandFormat,
            Interpretation = first.Interpretation,
            Coding = first.Coding, XRes = first.XRes, YRes = first.YRes,
            StartFn = VipsSeq.StartMany, GenerateFn = Generate, StopFn = VipsSeq.StopMany,
            ClientA = Inputs,
        };
        Out.CopyMetadataFrom(first);
        Out.SetPipeline(VipsDemandStyle.Any, Inputs);
        return 0;
    }

    public override int GetCacheKey()
    {
        var h = new HashCode();
        h.Add("Sum");
        if (Inputs != null) foreach (var i in Inputs) h.Add(RuntimeHelpers.GetHashCode(i));
        return h.ToHashCode();
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var regions = (VipsRegion[])seq!;
        VipsRect r = outRegion.Valid;
        for (int i = 0; i < regions.Length; i++)
            if (regions[i].Prepare(r) != 0) return -1;

        VipsImage outImg = outRegion.Image;
        int bands = outImg.Bands;
        bool isFloat = outImg.BandFormat == VipsBandFormat.Float;
        int totalSamples = r.Width * bands;

        for (int y = 0; y < r.Height; y++)
        {
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            if (isFloat)
            {
                for (int s = 0; s < totalSamples; s++)
                {
                    double sum = 0;
                    for (int i = 0; i < regions.Length; i++)
                    {
                        var addr = regions[i].GetAddress(r.Left, r.Top + y);
                        sum += BinaryPrimitives.ReadSingleLittleEndian(addr.Slice(s * 4, 4));
                    }
                    BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(s * 4, 4), (float)sum);
                }
            }
            else
            {
                for (int s = 0; s < totalSamples; s++)
                {
                    int sum = 0;
                    for (int i = 0; i < regions.Length; i++)
                        sum += regions[i].GetAddress(r.Left, r.Top + y)[s];
                    outAddr[s] = (byte)Math.Clamp(sum, 0, 255);
                }
            }
        }
        return 0;
    }
}
