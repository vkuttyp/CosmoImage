using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Misc;

/// <summary>
/// Concatenate bands across N input images. All inputs must agree on
/// width / height / band-format. Output band count is the sum of input
/// band counts, ordered as the inputs were supplied.
///
/// <para>The canonical use is rejoining channels split apart for
/// per-channel processing — e.g. take an RGB image, run the green
/// band through a sharpen, bandjoin the original R and B back to
/// produce R, sharpened-G, B. Also useful for adding alpha to an RGB
/// image: <c>Bandjoin(rgb, alphaImage)</c>.</para>
///
/// Mirrors libvips <c>vips_bandjoin</c>.
/// </summary>
public class VipsBandjoin : VipsOperation
{
    public VipsImage[]? Inputs { get; set; }
    public VipsImage? Out { get; set; }

    public override int Build()
    {
        if (Inputs == null || Inputs.Length == 0) return -1;
        if (Inputs.Length == 1) { Out = Inputs[0]; return 0; }

        var first = Inputs[0];
        int totalBands = 0;
        foreach (var img in Inputs)
        {
            if (img == null) return -1;
            if (img.Width != first.Width || img.Height != first.Height) return -1;
            if (img.BandFormat != first.BandFormat) return -1;
            totalBands += img.Bands;
        }

        Out = new VipsImage
        {
            Width = first.Width, Height = first.Height, Bands = totalBands,
            BandFormat = first.BandFormat,
            Interpretation = totalBands switch
            {
                1 or 2 => VipsInterpretation.BW,
                _ => VipsInterpretation.RGB,
            },
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
        h.Add("Bandjoin");
        if (Inputs != null) foreach (var i in Inputs) h.Add(RuntimeHelpers.GetHashCode(i));
        return h.ToHashCode();
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var regions = (VipsRegion[])seq!;
        VipsRect r = outRegion.Valid;

        for (int i = 0; i < regions.Length; i++)
            if (regions[i].Prepare(r) != 0) return -1;

        int outBands = outRegion.Image.Bands;
        bool isFloat = outRegion.Image.BandFormat == VipsBandFormat.Float;
        int sampleSize = isFloat ? 4 : 1;
        int outPel = outBands * sampleSize;

        // Per-input metadata: starting band offset within the output pel.
        var bandOffsets = new int[regions.Length];
        var inputBands = new int[regions.Length];
        int cumOff = 0;
        for (int i = 0; i < regions.Length; i++)
        {
            bandOffsets[i] = cumOff;
            inputBands[i] = regions[i].Image.Bands;
            cumOff += inputBands[i];
        }

        for (int y = 0; y < r.Height; y++)
        {
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);

            for (int x = 0; x < r.Width; x++)
            {
                int dstPelOff = x * outPel;
                for (int i = 0; i < regions.Length; i++)
                {
                    var inAddr = regions[i].GetAddress(r.Left + x, r.Top + y);
                    int copyBytes = inputBands[i] * sampleSize;
                    inAddr.Slice(0, copyBytes)
                          .CopyTo(outAddr.Slice(dstPelOff + bandOffsets[i] * sampleSize, copyBytes));
                }
            }
        }
        return 0;
    }
}
