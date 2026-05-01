using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Geometric;

public class VipsShrink : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public int HShrink { get; set; }
    public int VShrink { get; set; }

    public override int Build()
    {
        if (In == null) return -1;
        if (HShrink <= 0 || VShrink <= 0) return -1;

        Out = new VipsImage
        {
            Width = In.Width / HShrink,
            Height = In.Height / VShrink,
            Bands = In.Bands,
            BandFormat = In.BandFormat,
            Interpretation = In.Interpretation,
            Coding = In.Coding,
            XRes = In.XRes / HShrink,
            YRes = In.YRes / VShrink,
            StartFn = VipsSeq.StartOne,
            GenerateFn = Generate,
            StopFn = VipsSeq.StopOne,
            ClientA = In,
            ClientB = new int[] { HShrink, VShrink }
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.SmallTile, In);

        return 0;
    }

    public override int GetCacheKey()
    {
        return HashCode.Combine("Shrink", RuntimeHelpers.GetHashCode(In), HShrink, VShrink);
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        int[] factors = (int[])b!;
        int hShrink = factors[0];
        int vShrink = factors[1];
        VipsRect r = outRegion.Valid;

        // Area to fetch from input
        VipsRect inRect = new VipsRect(r.Left * hShrink, r.Top * vShrink, r.Width * hShrink, r.Height * vShrink);

        if (inRegion.Prepare(inRect) != 0) return -1;

        int bands = @in.Bands;
        int pelSize = @in.SizeOfPel;
        int shrinkArea = hShrink * vShrink;

        for (int y = 0; y < r.Height; y++)
        {
            var outLine = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                for (int bnd = 0; bnd < bands; bnd++)
                {
                    int sum = 0;
                    for (int sy = 0; sy < vShrink; sy++)
                    {
                        int inY = inRect.Top + y * vShrink + sy;
                        int inXBase = inRect.Left + x * hShrink;
                        var inLine = inRegion.GetAddress(inXBase, inY);
                        
                        for (int sx = 0; sx < hShrink; sx++)
                        {
                            sum += inLine[sx * pelSize + bnd];
                        }
                    }
                    outLine[x * pelSize + bnd] = (byte)(sum / shrinkArea);
                }
            }
        }

        return 0;
    }
}
