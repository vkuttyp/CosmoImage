using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Drawing;

/// <summary>
/// Paste a sub-image into the base at <c>(X, Y)</c>. Mirrors libvips
/// <c>vips_draw_image</c>. Differs from
/// <see cref="Operations.Mosaicing.VipsInsert"/> in that the output
/// stays the same size as <see cref="In"/>; sub pixels falling
/// outside the base are clipped.
///
/// <para>Both inputs must agree on band count and band format.
/// Out-of-bounds offsets simply produce a verbatim copy of the
/// base.</para>
/// </summary>
public class VipsDrawImage : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Sub { get; set; }
    public VipsImage? Out { get; set; }
    public int X { get; set; }
    public int Y { get; set; }

    public override int Build()
    {
        if (In == null || Sub == null) return -1;
        if (In.Bands != Sub.Bands || In.BandFormat != Sub.BandFormat) return -1;

        Out = new VipsImage
        {
            Width = In.Width, Height = In.Height,
            Bands = In.Bands, BandFormat = In.BandFormat,
            Interpretation = In.Interpretation,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartMany, GenerateFn = Generate, StopFn = VipsSeq.StopMany,
            ClientA = new[] { In, Sub }, ClientB = (X, Y),
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In, Sub);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("DrawImage", RuntimeHelpers.GetHashCode(In),
            RuntimeHelpers.GetHashCode(Sub), X, Y);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var regions = (VipsRegion[])seq!;
        var inReg = regions[0];
        var subReg = regions[1];
        var (px, py) = ((int, int))b!;
        VipsImage @in = inReg.Image;
        VipsImage @sub = subReg.Image;
        VipsRect r = outRegion.Valid;
        int pelSize = @in.SizeOfPel;
        int rowBytes = r.Width * pelSize;

        if (inReg.Prepare(r) != 0) return -1;
        for (int y = 0; y < r.Height; y++)
            inReg.GetAddress(r.Left, r.Top + y).Slice(0, rowBytes)
                .CopyTo(outRegion.GetAddress(r.Left, r.Top + y));

        // Sub region in input coords: (px, py, sub.W, sub.H). Intersect with r.
        int x0 = Math.Max(r.Left, px);
        int y0 = Math.Max(r.Top, py);
        int x1 = Math.Min(r.Left + r.Width, px + @sub.Width);
        int y1 = Math.Min(r.Top + r.Height, py + @sub.Height);
        if (x0 >= x1 || y0 >= y1) return 0;
        var subRect = new VipsRect(x0 - px, y0 - py, x1 - x0, y1 - y0);
        if (subReg.Prepare(subRect) != 0) return -1;
        int subRowBytes = subRect.Width * pelSize;
        for (int sy = 0; sy < subRect.Height; sy++)
        {
            var inAddr = subReg.GetAddress(subRect.Left, subRect.Top + sy);
            var outAddr = outRegion.GetAddress(x0, y0 + sy);
            inAddr.Slice(0, subRowBytes).CopyTo(outAddr);
        }
        return 0;
    }
}
