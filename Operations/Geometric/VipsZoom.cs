using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Geometric;

/// <summary>
/// Integer scale-up by replication — each input pixel covers an
/// <c>XFac × YFac</c> block in the output. Nearest-neighbour
/// upsampling, no interpolation. Mirrors libvips <c>vips_zoom</c>.
///
/// <para>This is *not* the same as <see cref="VipsReplicate"/>: zoom
/// expands each pixel into a block; replicate tiles the entire image.
/// For zoom factor 2, an input pel <c>(0, 0)</c> contributes to output
/// pels <c>(0..1, 0..1)</c>; for replicate factor 2, it contributes to
/// output pels <c>(0, 0)</c>, <c>(W, 0)</c>, <c>(0, H)</c>, <c>(W, H)</c>.</para>
/// </summary>
public class VipsZoom : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public int XFac { get; set; } = 1;
    public int YFac { get; set; } = 1;

    public override int Build()
    {
        if (In == null) return -1;
        if (XFac < 1 || YFac < 1) return -1;

        Out = new VipsImage
        {
            Width = In.Width * XFac, Height = In.Height * YFac,
            Bands = In.Bands, BandFormat = In.BandFormat,
            Interpretation = In.Interpretation,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In, ClientB = (XFac, YFac),
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Zoom", RuntimeHelpers.GetHashCode(In), XFac, YFac);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        var (xfac, yfac) = ((int, int))b!;
        VipsRect r = outRegion.Valid;
        int pelSize = @in.SizeOfPel;

        // Map output rect → input rect (clip to image bounds).
        int srcX0 = r.Left / xfac;
        int srcY0 = r.Top / yfac;
        int srcX1 = (r.Left + r.Width + xfac - 1) / xfac;
        int srcY1 = (r.Top + r.Height + yfac - 1) / yfac;
        var inRect = new VipsRect(srcX0, srcY0, srcX1 - srcX0, srcY1 - srcY0);
        if (inRegion.Prepare(inRect) != 0) return -1;

        for (int gy = r.Top; gy < r.Top + r.Height; gy++)
        {
            int sy = gy / yfac;
            var inAddr = inRegion.GetAddress(inRect.Left, sy);
            var outAddr = outRegion.GetAddress(r.Left, gy);
            // Walk one output scanline. Each block of `xfac` consecutive
            // output pels reads the same source pel. Source x-offset
            // within the prepared input row is (gx/xfac - srcX0).
            for (int x = 0; x < r.Width; x++)
            {
                int gx = r.Left + x;
                int srcOffset = (gx / xfac - srcX0) * pelSize;
                inAddr.Slice(srcOffset, pelSize).CopyTo(outAddr.Slice(x * pelSize, pelSize));
            }
        }
        return 0;
    }
}
