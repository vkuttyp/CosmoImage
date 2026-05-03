using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Misc;

public enum VipsMath2Operation
{
    /// <summary>left^right.</summary>
    Pow = 0,
    /// <summary>right^left (reverse-pow).</summary>
    Wop = 1,
    /// <summary>atan2(left, right). Output in radians; pass UChar in if you have
    /// already cast to a fraction-of-π convention.</summary>
    Atan2 = 2,
}

/// <summary>
/// Per-pixel binary math op on two images. Both inputs must agree on
/// dimensions, band count, and band format. Mirrors libvips
/// <c>vips_pow</c> / <c>vips_wop</c> / <c>vips_atan2</c>.
///
/// <para>UChar inputs preserve UChar with clamping; for non-clamping
/// behaviour cast to Float first.</para>
/// </summary>
public class VipsMath2 : VipsOperation
{
    public VipsImage? Left { get; set; }
    public VipsImage? Right { get; set; }
    public VipsImage? Out { get; set; }
    public VipsMath2Operation Op { get; set; }

    public override int Build()
    {
        if (Left == null || Right == null) return -1;
        if (Left.BandFormat != Right.BandFormat) return -1;
        if (Left.Width != Right.Width || Left.Height != Right.Height) return -1;
        if (Left.Bands != Right.Bands) return -1;

        Out = new VipsImage
        {
            Width = Left.Width, Height = Left.Height,
            Bands = Left.Bands, BandFormat = Left.BandFormat,
            Interpretation = Left.Interpretation,
            Coding = Left.Coding, XRes = Left.XRes, YRes = Left.YRes,
            StartFn = VipsSeq.StartMany, GenerateFn = Generate, StopFn = VipsSeq.StopMany,
            ClientA = new[] { Left, Right }, ClientB = Op,
        };
        Out.CopyMetadataFrom(Left);
        Out.SetPipeline(VipsDemandStyle.Any, Left, Right);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Math2", RuntimeHelpers.GetHashCode(Left),
            RuntimeHelpers.GetHashCode(Right), Op);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var regions = (VipsRegion[])seq!;
        var op = (VipsMath2Operation)b!;
        VipsRect r = outRegion.Valid;
        if (regions[0].Prepare(r) != 0) return -1;
        if (regions[1].Prepare(r) != 0) return -1;

        VipsImage outImg = outRegion.Image;
        bool isFloat = outImg.BandFormat == VipsBandFormat.Float;
        int totalSamples = r.Width * outImg.Bands;

        for (int y = 0; y < r.Height; y++)
        {
            var la = regions[0].GetAddress(r.Left, r.Top + y);
            var ra = regions[1].GetAddress(r.Left, r.Top + y);
            var oa = outRegion.GetAddress(r.Left, r.Top + y);
            for (int s = 0; s < totalSamples; s++)
            {
                double lv, rv;
                if (isFloat)
                {
                    lv = BinaryPrimitives.ReadSingleLittleEndian(la.Slice(s * 4, 4));
                    rv = BinaryPrimitives.ReadSingleLittleEndian(ra.Slice(s * 4, 4));
                }
                else
                {
                    lv = la[s];
                    rv = ra[s];
                }
                double v = op switch
                {
                    VipsMath2Operation.Pow => Math.Pow(lv, rv),
                    VipsMath2Operation.Wop => Math.Pow(rv, lv),
                    VipsMath2Operation.Atan2 => Math.Atan2(lv, rv),
                    _ => 0,
                };
                if (isFloat)
                    BinaryPrimitives.WriteSingleLittleEndian(oa.Slice(s * 4, 4), (float)v);
                else
                    oa[s] = (byte)Math.Clamp(v, 0, 255);
            }
        }
        return 0;
    }
}
