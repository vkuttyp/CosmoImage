using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Geometric;

public class VipsExtractArea : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    public override int Build()
    {
        if (In == null) return -1;

        // Clip to input bounds
        int left = Math.Clamp(Left, 0, In.Width);
        int top = Math.Clamp(Top, 0, In.Height);
        int width = Math.Min(Width, In.Width - left);
        int height = Math.Min(Height, In.Height - top);

        Out = new VipsImage
        {
            Width = width,
            Height = height,
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
            ClientB = new VipsRect(left, top, width, height)
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);

        return 0;
    }

    public override int GetCacheKey()
    {
        return HashCode.Combine("ExtractArea", RuntimeHelpers.GetHashCode(In), Left, Top, Width, Height);
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        VipsRect extractRect = (VipsRect)b!;
        VipsRect r = outRegion.Valid;

        // We need to request a shifted area from the input
        VipsRect inRect = new VipsRect(r.Left + extractRect.Left, r.Top + extractRect.Top, r.Width, r.Height);

        if (inRegion.Prepare(inRect) != 0) return -1;

        int pelSize = @in.SizeOfPel;
        int rowLength = r.Width * pelSize;

        for (int y = 0; y < r.Height; y++)
        {
            var inAddr = inRegion.GetAddress(inRect.Left, inRect.Top + y);
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            inAddr.Slice(0, rowLength).CopyTo(outAddr);
        }

        return 0;
    }
}
