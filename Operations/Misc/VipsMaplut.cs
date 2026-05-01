using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Misc;

public class VipsMaplut : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Lut { get; set; }
    public VipsImage? Out { get; set; }

    public override int Build()
    {
        if (In == null || Lut == null) return -1;

        Out = new VipsImage
        {
            Width = In.Width,
            Height = In.Height,
            Bands = Lut.Bands, // Output bands determined by LUT
            BandFormat = In.BandFormat,
            Interpretation = In.Interpretation,
            Coding = In.Coding,
            XRes = In.XRes,
            YRes = In.YRes,
            StartFn = VipsSeq.StartMany,
            GenerateFn = Generate,
            StopFn = VipsSeq.StopMany,
            ClientA = new[] { In, Lut }
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In, Lut);

        return 0;
    }

    public override int GetCacheKey()
    {
        return HashCode.Combine("Maplut", RuntimeHelpers.GetHashCode(In), RuntimeHelpers.GetHashCode(Lut));
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var regions = (VipsRegion[])seq!;
        var inRegion = regions[0];
        var lutRegion = regions[1];
        VipsImage @in = inRegion.Image;
        VipsImage lut = lutRegion.Image;
        VipsRect r = outRegion.Valid;

        if (inRegion.Prepare(r) != 0) return -1;
        // LUT is usually 1x256, so we prepare the whole thing once
        if (lutRegion.Prepare(new VipsRect(0, 0, lut.Width, lut.Height)) != 0) return -1;

        int inBands = @in.Bands;
        int outBands = outRegion.Image.Bands;
        int inPelSize = @in.SizeOfPel;
        int outPelSize = outRegion.Image.SizeOfPel;
        int lutBands = lut.Bands;

        for (int y = 0; y < r.Height; y++)
        {
            var inAddr = inRegion.GetAddress(r.Left, r.Top + y);
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);

            for (int x = 0; x < r.Width; x++)
            {
                for (int i = 0; i < outBands; i++)
                {
                    // Map input pixel value to LUT index
                    // If inBands == 1, all outBands use same input
                    // If inBands == outBands, each band uses its own LUT entry
                    int inIdx = inBands == 1 ? 0 : i;
                    byte val = inAddr[x * inPelSize + inIdx];
                    
                    var lutAddr = lutRegion.GetAddress(val, 0);
                    // LUT values are also UChar for now
                    outAddr[x * outPelSize + i] = lutAddr[i % lutBands];
                }
            }
        }

        return 0;
    }
}
