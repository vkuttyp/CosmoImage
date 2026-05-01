using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Convolution;

public class VipsConv : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public double[,]? Mask { get; set; }

    public override int Build()
    {
        if (In == null || Mask == null) return -1;

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
            ClientB = Mask
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.FatStrip, In);

        return 0;
    }

    public override int GetCacheKey()
    {
        var hash = new HashCode();
        hash.Add("Conv");
        hash.Add(RuntimeHelpers.GetHashCode(In));
        if (Mask != null)
        {
            foreach (var m in Mask) hash.Add(m);
        }
        return hash.ToHashCode();
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = (VipsImage)a!;
        double[,] mask = (double[,])b!;
        VipsRect r = outRegion.Valid;

        int mh = mask.GetLength(0);
        int mw = mask.GetLength(1);
        int ox = mw / 2;
        int oy = mh / 2;

        VipsRect inRect = new VipsRect(r.Left - ox, r.Top - oy, r.Width + mw - 1, r.Height + mh - 1);
        VipsRect clipped = VipsRect.Intersect(inRect, new VipsRect(0, 0, @in.Width, @in.Height));
        if (inRegion.Prepare(clipped) != 0) return -1;

        int bands = @in.Bands;
        int pelSize = @in.SizeOfPel;

        for (int y = 0; y < r.Height; y++)
        {
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                for (int bnd = 0; bnd < bands; bnd++)
                {
                    double sum = 0;
                    for (int my = 0; mh > my; my++)
                    {
                        for (int mx = 0; mw > mx; mx++)
                        {
                            int ix = r.Left + x + mx - ox;
                            int iy = r.Top + y + my - oy;

                            if (ix >= 0 && ix < @in.Width && iy >= 0 && iy < @in.Height)
                            {
                                sum += inRegion.GetAddress(ix, iy)[bnd] * mask[my, mx];
                            }
                        }
                    }
                    outAddr[x * pelSize + bnd] = (byte)Math.Clamp(sum, 0, 255);
                }
            }
        }

        return 0;
    }
}
