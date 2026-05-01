using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Analysis;

public class VipsHistCum : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }

    public override int Build()
    {
        if (In == null) return -1;

        Out = new VipsImage
        {
            Width = In.Width,
            Height = In.Height,
            Bands = In.Bands,
            BandFormat = VipsBandFormat.Double, // Cumulative sums can be large
            Interpretation = VipsInterpretation.Histogram,
            XRes = 1.0,
            YRes = 1.0,
            StartFn = VipsSeq.StartOne,
            GenerateFn = Generate,
            StopFn = VipsSeq.StopOne,
            ClientA = In
        };
        Out.SetPipeline(VipsDemandStyle.Any, In);

        return 0;
    }

    public override int GetCacheKey()
    {
        return HashCode.Combine("HistCum", RuntimeHelpers.GetHashCode(In));
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        VipsRect r = outRegion.Valid;

        inRegion.Prepare(new VipsRect(0, 0, @in.Width, @in.Height));

        int bands = @in.Bands;
        double[] sum = new double[bands];

        for (int x = 0; x < @in.Width; x++)
        {
            var inAddr = inRegion.GetAddress(x, 0);
            var outAddr = outRegion.GetAddress(x, 0);

            for (int i = 0; i < bands; i++)
            {
                // Input is UInt (4 bytes)
                uint val = BitConverter.ToUInt32(inAddr.Slice(i * 4, 4));
                sum[i] += val;
                BitConverter.TryWriteBytes(outAddr.Slice(i * 8, 8), sum[i]);
            }
        }

        return 0;
    }
}

public class VipsHistNorm : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }

    public override int Build()
    {
        if (In == null) return -1;

        Out = new VipsImage
        {
            Width = In.Width,
            Height = In.Height,
            Bands = In.Bands,
            BandFormat = VipsBandFormat.UChar, // Result of norm for LUT is usually UChar
            Interpretation = VipsInterpretation.Histogram,
            XRes = 1.0,
            YRes = 1.0,
            StartFn = VipsSeq.StartOne,
            GenerateFn = Generate,
            StopFn = VipsSeq.StopOne,
            ClientA = In
        };
        Out.SetPipeline(VipsDemandStyle.Any, In);

        return 0;
    }

    public override int GetCacheKey()
    {
        return HashCode.Combine("HistNorm", RuntimeHelpers.GetHashCode(In));
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        VipsRect r = outRegion.Valid;

        inRegion.Prepare(new VipsRect(0, 0, @in.Width, @in.Height));

        int bands = @in.Bands;
        double[] max = new double[bands];

        // Find max
        for (int x = 0; x < @in.Width; x++)
        {
            var inAddr = inRegion.GetAddress(x, 0);
            for (int i = 0; i < bands; i++)
            {
                double val = BitConverter.ToDouble(inAddr.Slice(i * 8, 8));
                max[i] = Math.Max(max[i], val);
            }
        }

        // Normalize
        for (int x = 0; x < @in.Width; x++)
        {
            var inAddr = inRegion.GetAddress(x, 0);
            var outAddr = outRegion.GetAddress(x, 0);

            for (int i = 0; i < bands; i++)
            {
                double val = BitConverter.ToDouble(inAddr.Slice(i * 8, 8));
                outAddr[i] = (byte)(max[i] > 0 ? (val / max[i] * 255.0) : 0);
            }
        }

        return 0;
    }
}
