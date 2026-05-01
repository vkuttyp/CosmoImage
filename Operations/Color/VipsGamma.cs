using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Color;

public class VipsGamma : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public double Exponent { get; set; }

    public override int Build()
    {
        if (In == null) return -1;

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
            StartFn = VipsSeq.StartOne,
            GenerateFn = Generate,
            StopFn = VipsSeq.StopOne,
            ClientA = In,
            ClientB = Exponent
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);

        return 0;
    }

    public override int GetCacheKey()
    {
        return HashCode.Combine("Gamma", RuntimeHelpers.GetHashCode(In), Exponent);
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        double exponent = (double)b!;
        VipsRect r = outRegion.Valid;

        if (inRegion.Prepare(r) != 0) return -1;

        int pelSize = @in.SizeOfPel;
        int bands = @in.Bands;

        // Precompute LUT for performance
        byte[] lut = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            lut[i] = (byte)Math.Clamp(Math.Pow(i / 255.0, 1.0 / exponent) * 255.0, 0, 255);
        }

        for (int y = 0; y < r.Height; y++)
        {
            var inAddr = inRegion.GetAddress(r.Left, r.Top + y);
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);

            for (int x = 0; x < r.Width * bands; x++)
            {
                outAddr[x] = lut[inAddr[x]];
            }
        }

        return 0;
    }
}
