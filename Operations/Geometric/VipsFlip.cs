using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Geometric;

public class VipsFlip : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public VipsDirection Direction { get; set; }

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
            ClientB = Direction
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.SmallTile, In);

        return 0;
    }

    public override int GetCacheKey()
    {
        return HashCode.Combine("Flip", RuntimeHelpers.GetHashCode(In), Direction);
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = (VipsImage)a!;
        VipsDirection direction = (VipsDirection)b!;
        VipsRect r = outRegion.Valid;

        if (direction == VipsDirection.Vertical)
        {
            // Vertical flip: map y to Height - 1 - y
            VipsRect inRect = new VipsRect(r.Left, @in.Height - 1 - (r.Top + r.Height - 1), r.Width, r.Height);
            if (inRegion.Prepare(inRect) != 0) return -1;

            int pelSize = @in.SizeOfPel;
            int lineSize = r.Width * pelSize;

            for (int y = 0; y < r.Height; y++)
            {
                var inAddr = inRegion.GetAddress(r.Left, inRect.Top + (r.Height - 1 - y));
                var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
                inAddr.Slice(0, lineSize).CopyTo(outAddr);
            }
        }
        else // Horizontal flip
        {
            // Horizontal flip: map x to Width - 1 - x
            VipsRect inRect = new VipsRect(@in.Width - 1 - (r.Left + r.Width - 1), r.Top, r.Width, r.Height);
            if (inRegion.Prepare(inRect) != 0) return -1;

            int pelSize = @in.SizeOfPel;

            for (int y = 0; y < r.Height; y++)
            {
                var inAddr = inRegion.GetAddress(inRect.Left, r.Top + y);
                var outAddr = outRegion.GetAddress(r.Left, r.Top + y);

                for (int x = 0; x < r.Width; x++)
                {
                    var sourcePel = inAddr.Slice((r.Width - 1 - x) * pelSize, pelSize);
                    var destPel = outAddr.Slice(x * pelSize, pelSize);
                    sourcePel.CopyTo(destPel);
                }
            }
        }

        return 0;
    }
}
