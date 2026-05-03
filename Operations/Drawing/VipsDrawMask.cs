using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Drawing;

/// <summary>
/// Apply <see cref="Ink"/> to the base image where <see cref="Mask"/>
/// is non-zero, with mask value used as alpha for the blend. Mirrors
/// libvips <c>vips_draw_mask</c>.
///
/// <para>The mask must be UChar 1-band with the same dimensions as
/// the placement footprint. <see cref="X"/> / <see cref="Y"/> set the
/// mask's top-left in base coordinates. Mask byte 0 means "leave
/// base unchanged"; 255 means "use ink fully"; in-between blends
/// linearly.</para>
///
/// <para>The standard use is anti-aliased shape rasterisation:
/// generate an alpha mask via <c>SdfCircle</c> + smoothstep, then
/// draw a colour through it.</para>
/// </summary>
public class VipsDrawMask : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Mask { get; set; }
    public VipsImage? Out { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public byte[]? Ink { get; set; }

    public override int Build()
    {
        if (In == null || Mask == null || Ink == null) return -1;
        if (In.BandFormat != VipsBandFormat.UChar) return -1;
        if (Mask.BandFormat != VipsBandFormat.UChar || Mask.Bands != 1) return -1;
        if (Ink.Length != In.SizeOfPel) return -1;

        Out = new VipsImage
        {
            Width = In.Width, Height = In.Height,
            Bands = In.Bands, BandFormat = In.BandFormat,
            Interpretation = In.Interpretation,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartMany, GenerateFn = Generate, StopFn = VipsSeq.StopMany,
            ClientA = new[] { In, Mask }, ClientB = (X, Y, Ink),
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In, Mask);
        return 0;
    }

    public override int GetCacheKey()
    {
        var h = new HashCode();
        h.Add("DrawMask"); h.Add(RuntimeHelpers.GetHashCode(In));
        h.Add(RuntimeHelpers.GetHashCode(Mask));
        h.Add(X); h.Add(Y);
        if (Ink != null) foreach (var bb in Ink) h.Add(bb);
        return h.ToHashCode();
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var regions = (VipsRegion[])seq!;
        var inReg = regions[0];
        var maskReg = regions[1];
        var (px, py, ink) = ((int, int, byte[]))b!;
        VipsImage @in = inReg.Image;
        VipsImage mask = maskReg.Image;
        VipsRect r = outRegion.Valid;
        int pelSize = @in.SizeOfPel;
        int rowBytes = r.Width * pelSize;

        if (inReg.Prepare(r) != 0) return -1;
        for (int y = 0; y < r.Height; y++)
            inReg.GetAddress(r.Left, r.Top + y).Slice(0, rowBytes)
                .CopyTo(outRegion.GetAddress(r.Left, r.Top + y));

        // Mask area in base coords: (px, py, mask.W, mask.H). Intersect.
        int x0 = Math.Max(r.Left, px);
        int y0 = Math.Max(r.Top, py);
        int x1 = Math.Min(r.Left + r.Width, px + mask.Width);
        int y1 = Math.Min(r.Top + r.Height, py + mask.Height);
        if (x0 >= x1 || y0 >= y1) return 0;
        var maskRect = new VipsRect(x0 - px, y0 - py, x1 - x0, y1 - y0);
        if (maskReg.Prepare(maskRect) != 0) return -1;
        for (int sy = 0; sy < maskRect.Height; sy++)
        {
            var maskAddr = maskReg.GetAddress(maskRect.Left, maskRect.Top + sy);
            var outAddr = outRegion.GetAddress(x0, y0 + sy);
            for (int sx = 0; sx < maskRect.Width; sx++)
            {
                int alpha = maskAddr[sx];
                if (alpha == 0) continue;
                int pelOff = sx * pelSize;
                if (alpha == 255)
                {
                    ink.AsSpan().CopyTo(outAddr.Slice(pelOff, pelSize));
                    continue;
                }
                // Per-byte blend: out = base + alpha/255 · (ink − base)
                for (int bnd = 0; bnd < pelSize; bnd++)
                {
                    int baseV = outAddr[pelOff + bnd];
                    int v = baseV + ((alpha * (ink[bnd] - baseV)) >> 8);
                    outAddr[pelOff + bnd] = (byte)Math.Clamp(v, 0, 255);
                }
            }
        }
        return 0;
    }
}
