using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Convolution;

public class VipsConv1D : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public double[]? Kernel { get; set; }
    public bool Vertical { get; set; }

    public override int Build()
    {
        if (In == null || Kernel == null) return -1;

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
            ClientB = new { Kernel, Vertical }
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.FatStrip, In);

        return 0;
    }

    public override int GetCacheKey()
    {
        var hash = new HashCode();
        hash.Add("Conv1D");
        hash.Add(RuntimeHelpers.GetHashCode(In));
        if (Kernel != null) foreach (var k in Kernel) hash.Add(k);
        hash.Add(Vertical);
        return hash.ToHashCode();
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        dynamic config = b!;
        double[] kernel = config.Kernel;
        bool vertical = config.Vertical;
        VipsRect r = outRegion.Valid;

        int kSize = kernel.Length;
        int kOffset = kSize / 2;

        VipsRect inRect = vertical
            ? new VipsRect(r.Left, r.Top - kOffset, r.Width, r.Height + kSize - 1)
            : new VipsRect(r.Left - kOffset, r.Top, r.Width + kSize - 1, r.Height);

        VipsRect clippedRect = VipsRect.Intersect(inRect, new VipsRect(0, 0, @in.Width, @in.Height));
        if (inRegion.Prepare(clippedRect) != 0) return -1;

        int bands = @in.Bands;
        int pelSize = @in.SizeOfPel;

        for (int y = 0; y < r.Height; y++)
        {
            var outLine = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                for (int bnd = 0; bnd < bands; bnd++)
                {
                    double sum = 0;
                    for (int i = 0; i < kSize; i++)
                    {
                        int ix = vertical ? (r.Left + x) : (r.Left + x + i - kOffset);
                        int iy = vertical ? (r.Top + y + i - kOffset) : (r.Top + y);

                        if (ix >= 0 && ix < @in.Width && iy >= 0 && iy < @in.Height)
                        {
                            var inPel = inRegion.GetAddress(ix, iy);
                            sum += inPel[bnd] * kernel[i];
                        }
                    }
                    outLine[x * pelSize + bnd] = (byte)Math.Clamp(sum, 0, 255);
                }
            }
        }

        return 0;
    }
}
