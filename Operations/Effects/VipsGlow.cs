using System;
using System.Runtime.CompilerServices;
using System.Numerics;

namespace CosmoImage.Operations.Effects;

/// <summary>
/// Bloom-style glow: blur the image and add a fraction of the blurred result
/// back to the original. Bright areas extend a soft halo; dark areas barely
/// change because they have little to add. Same dual-input seq pattern as
/// <see cref="VipsUnsharpMask"/> — input + GaussBlur(input).
/// </summary>
public class VipsGlow : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }

    /// <summary>Standard deviation of the Gaussian halo. Larger = wider glow.</summary>
    public double Sigma { get; set; } = 5.0;

    /// <summary>How strongly the blurred image adds to the original. 0 = none, 1 = full add.</summary>
    public double Strength { get; set; } = 0.3;

    public override int Build()
    {
        if (In == null) return -1;

        var blurred = VipsImageOps.GaussBlur(In, Sigma);

        Out = new VipsImage
        {
            Width = In.Width,
            Height = In.Height,
            Bands = In.Bands,
            BandFormat = In.BandFormat,
            Interpretation = In.Interpretation,
            Coding = In.Coding,
            XRes = In.XRes,
            YRes = In.YRes,
            StartFn = VipsSeq.StartMany,
            GenerateFn = Generate,
            StopFn = VipsSeq.StopMany,
            ClientA = new[] { In, blurred },
            ClientB = Strength
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.FatStrip, In, blurred);

        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Glow", RuntimeHelpers.GetHashCode(In), Sigma, Strength);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var regions = (VipsRegion[])seq!;
        var inRegion = regions[0];
        var blurRegion = regions[1];
        VipsImage @in = inRegion.Image;
        double strength = (double)b!;
        VipsRect r = outRegion.Valid;

        if (inRegion.Prepare(r) != 0) return -1;
        if (blurRegion.Prepare(r) != 0) return -1;

        int totalBytes = r.Width * @in.Bands;
        int vSize = Vector<float>.Count;
        var vStrength = new Vector<float>((float)strength);
        // Hoisted out of the per-row loop — same allocation reused for every tile pixel batch.
        Span<float> inF = stackalloc float[vSize];
        Span<float> blurF = stackalloc float[vSize];

        for (int y = 0; y < r.Height; y++)
        {
            var inAddr = inRegion.GetAddress(r.Left, r.Top + y);
            var blurAddr = blurRegion.GetAddress(r.Left, r.Top + y);
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);

            int i = 0;
            for (; i <= totalBytes - vSize; i += vSize)
            {
                for (int j = 0; j < vSize; j++)
                {
                    inF[j] = inAddr[i + j];
                    blurF[j] = blurAddr[i + j];
                }
                var vIn = new Vector<float>(inF);
                var vBlur = new Vector<float>(blurF);
                var vRes = vIn + vStrength * vBlur;
                for (int j = 0; j < vSize; j++)
                    outAddr[i + j] = (byte)Math.Clamp(vRes[j], 0, 255);
            }

            for (; i < totalBytes; i++)
            {
                double res = inAddr[i] + strength * blurAddr[i];
                outAddr[i] = (byte)Math.Clamp(res, 0, 255);
            }
        }
        return 0;
    }
}
