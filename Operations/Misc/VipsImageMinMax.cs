using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Misc;

/// <summary>
/// Pixel-wise minimum across N input images. All inputs must agree on
/// dimensions / bands / format. Mirrors libvips
/// <c>vips_min2</c> when applied pairwise — the N-input variant
/// composes that across all inputs.
///
/// <para>Useful for "darkest of N captures" denoising, lower-envelope
/// rendering, and clipping pipelines where the per-pixel limit comes
/// from another image.</para>
/// </summary>
public class VipsImageMin : VipsOperation
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
        h.Add("ImageMin");
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
                    float best = float.MaxValue;
                    for (int i = 0; i < regions.Length; i++)
                    {
                        float v = BinaryPrimitives.ReadSingleLittleEndian(
                            regions[i].GetAddress(r.Left, r.Top + y).Slice(s * 4, 4));
                        if (v < best) best = v;
                    }
                    BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(s * 4, 4), best);
                }
            }
            else
            {
                for (int s = 0; s < totalSamples; s++)
                {
                    byte best = 255;
                    for (int i = 0; i < regions.Length; i++)
                    {
                        byte v = regions[i].GetAddress(r.Left, r.Top + y)[s];
                        if (v < best) best = v;
                    }
                    outAddr[s] = best;
                }
            }
        }
        return 0;
    }
}

/// <summary>
/// Pixel-wise maximum across N input images. Counterpart of
/// <see cref="VipsImageMin"/>; "brightest of N captures" use case.
/// </summary>
public class VipsImageMax : VipsOperation
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
        h.Add("ImageMax");
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
                    float best = float.MinValue;
                    for (int i = 0; i < regions.Length; i++)
                    {
                        float v = BinaryPrimitives.ReadSingleLittleEndian(
                            regions[i].GetAddress(r.Left, r.Top + y).Slice(s * 4, 4));
                        if (v > best) best = v;
                    }
                    BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(s * 4, 4), best);
                }
            }
            else
            {
                for (int s = 0; s < totalSamples; s++)
                {
                    byte best = 0;
                    for (int i = 0; i < regions.Length; i++)
                    {
                        byte v = regions[i].GetAddress(r.Left, r.Top + y)[s];
                        if (v > best) best = v;
                    }
                    outAddr[s] = best;
                }
            }
        }
        return 0;
    }
}
