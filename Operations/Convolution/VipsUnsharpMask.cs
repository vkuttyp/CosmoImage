using System;
using System.Runtime.CompilerServices;
using System.Numerics;

namespace CosmoImage.Operations.Convolution;

public class VipsUnsharpMask : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public double Sigma { get; set; } = 1.0;
    public double Amount { get; set; } = 1.0;

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
            ClientB = Amount
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.FatStrip, In, blurred);

        return 0;
    }

    public override int GetCacheKey()
    {
        return HashCode.Combine("UnsharpMask", RuntimeHelpers.GetHashCode(In), Sigma, Amount);
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var regions = (VipsRegion[])seq!;
        var inRegion = regions[0];
        var blurRegion = regions[1];
        VipsImage @in = inRegion.Image;
        double amount = (double)b!;
        VipsRect r = outRegion.Valid;

        if (inRegion.Prepare(r) != 0) return -1;
        if (blurRegion.Prepare(r) != 0) return -1;

        int totalBytes = r.Width * @in.Bands;
        int vSize = Vector<float>.Count;
        float amt = (float)amount;
        var vAmt = new Vector<float>(amt);

        for (int y = 0; y < r.Height; y++)
        {
            var inAddr = inRegion.GetAddress(r.Left, r.Top + y);
            var blurAddr = blurRegion.GetAddress(r.Left, r.Top + y);
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);

            int i = 0;
            for (; i <= totalBytes - vSize; i += vSize)
            {
                float[] inF = new float[vSize];
                float[] blurF = new float[vSize];
                for (int j = 0; j < vSize; j++)
                {
                    inF[j] = inAddr[i + j];
                    blurF[j] = blurAddr[i + j];
                }

                var vIn = new Vector<float>(inF);
                var vBlur = new Vector<float>(blurF);

                var vDiff = vIn - vBlur;
                var vRes = vIn + vAmt * vDiff;

                for (int j = 0; j < vSize; j++)
                {
                    outAddr[i + j] = (byte)Math.Clamp(vRes[j], 0, 255);
                }
            }

            for (; i < totalBytes; i++)
            {
                double original = inAddr[i];
                double blurredVal = blurAddr[i];
                double diff = original - blurredVal;
                double res = original + amount * diff;
                outAddr[i] = (byte)Math.Clamp(res, 0, 255);
            }
        }

        return 0;
    }
}
