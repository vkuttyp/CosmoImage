using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Iofuncs;

/// <summary>
/// Force sequential top-to-bottom evaluation of the upstream pipeline.
/// Mirrors libvips <c>vips_sequential</c>. Sets the demand style to
/// <see cref="VipsDemandStyle.FatStrip"/> and pulls input rows in
/// strict y-order so streaming savers don't need to seek backward.
///
/// <para>The standard wrapping for a save sink: the source might
/// emit small random tiles, but the saver wants whole scanlines from
/// y=0 downward.</para>
/// </summary>
public class VipsSequential : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }

    public override int Build()
    {
        if (In == null) return -1;

        Out = new VipsImage
        {
            Width = In.Width, Height = In.Height,
            Bands = In.Bands, BandFormat = In.BandFormat,
            Interpretation = In.Interpretation,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In,
        };
        Out.CopyMetadataFrom(In);
        // FatStrip = "give me wide strips, top-to-bottom" — what savers want.
        Out.SetPipeline(VipsDemandStyle.FatStrip, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Sequential", RuntimeHelpers.GetHashCode(In));

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inReg = (VipsRegion)seq!;
        VipsRect r = outRegion.Valid;
        if (inReg.Prepare(r) != 0) return -1;

        VipsImage @in = inReg.Image;
        int pelSize = @in.SizeOfPel;
        int rowBytes = r.Width * pelSize;
        for (int y = 0; y < r.Height; y++)
        {
            inReg.GetAddress(r.Left, r.Top + y).Slice(0, rowBytes)
                .CopyTo(outRegion.GetAddress(r.Left, r.Top + y));
        }
        return 0;
    }
}
