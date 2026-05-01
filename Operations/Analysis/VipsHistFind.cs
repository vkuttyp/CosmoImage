using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Analysis;

public class VipsHistFind : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }

    public override int Build()
    {
        if (In == null) return -1;

        Out = new VipsImage
        {
            Width = 256,
            Height = 1,
            Bands = In.Bands,
            BandFormat = VipsBandFormat.UInt, // Histograms usually uint counts
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
        return HashCode.Combine("HistFind", RuntimeHelpers.GetHashCode(In));
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        VipsRect r = outRegion.Valid;

        // Histograms MUST be generated from the entire image
        uint[,] hist = new uint[@in.Bands, 256];

        // Process in large chunks to minimize Prepare overhead
        int chunkSize = 256;
        for (int y = 0; y < @in.Height; y += chunkSize)
        {
            int h = Math.Min(chunkSize, @in.Height - y);
            VipsRect fullLine = new VipsRect(0, y, @in.Width, h);
            inRegion.Prepare(fullLine);

            int bands = @in.Bands;
            int pelSize = @in.SizeOfPel;

            for (int ly = 0; ly < h; ly++)
            {
                var addr = inRegion.GetAddress(0, y + ly);
                for (int x = 0; x < @in.Width; x++)
                {
                    for (int i = 0; i < bands; i++)
                    {
                        hist[i, addr[x * pelSize + i]]++;
                    }
                }
            }
        }

        // Write to output region (which should be 1x256)
        int outPelSize = outRegion.Image.SizeOfPel;
        for (int x = 0; x < 256; x++)
        {
            var outAddr = outRegion.GetAddress(x, 0);
            for (int i = 0; i < @in.Bands; i++)
            {
                // Write as UInt (4 bytes)
                BitConverter.TryWriteBytes(outAddr.Slice(i * 4, 4), hist[i, x]);
            }
        }

        return 0;
    }
}
