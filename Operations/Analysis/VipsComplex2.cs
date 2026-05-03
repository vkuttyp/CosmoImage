using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Analysis;

public enum VipsComplex2Op
{
    /// <summary>arg(left · conj(right)) — phase difference between the two.</summary>
    CrossPhase = 0,
}

/// <summary>
/// Per-pixel binary op on two DPComplex images. Mirrors libvips
/// <c>vips_complex2</c>.
///
/// <para>The headline use is <see cref="VipsComplex2Op.CrossPhase"/>:
/// the argument of <c>z₁ · conj(z₂)</c>. This is the standard
/// "phase-only" registration ingredient — combine with the FFT
/// of two images and take the argument to find the translation
/// between them.</para>
/// </summary>
public class VipsComplex2 : VipsOperation
{
    public VipsImage? Left { get; set; }
    public VipsImage? Right { get; set; }
    public VipsImage? Out { get; set; }
    public VipsComplex2Op Op { get; set; }

    public override int Build()
    {
        if (Left == null || Right == null) return -1;
        if (Left.BandFormat != VipsBandFormat.DPComplex) return -1;
        if (Right.BandFormat != VipsBandFormat.DPComplex) return -1;
        if (Left.Bands != 1 || Right.Bands != 1) return -1;
        if (Left.Width != Right.Width || Left.Height != Right.Height) return -1;

        Out = new VipsImage
        {
            Width = Left.Width, Height = Left.Height, Bands = 1,
            BandFormat = VipsBandFormat.DPComplex,
            Interpretation = Left.Interpretation,
            XRes = Left.XRes, YRes = Left.YRes,
            StartFn = VipsSeq.StartMany, GenerateFn = Generate, StopFn = VipsSeq.StopMany,
            ClientA = new[] { Left, Right }, ClientB = Op,
        };
        Out.CopyMetadataFrom(Left);
        Out.SetPipeline(VipsDemandStyle.Any, Left, Right);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Complex2", RuntimeHelpers.GetHashCode(Left),
            RuntimeHelpers.GetHashCode(Right), Op);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var regions = (VipsRegion[])seq!;
        var op = (VipsComplex2Op)b!;
        VipsRect r = outRegion.Valid;
        if (regions[0].Prepare(r) != 0) return -1;
        if (regions[1].Prepare(r) != 0) return -1;

        for (int y = 0; y < r.Height; y++)
        {
            var la = regions[0].GetAddress(r.Left, r.Top + y);
            var ra = regions[1].GetAddress(r.Left, r.Top + y);
            var oa = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                double l1 = BinaryPrimitives.ReadDoubleLittleEndian(la.Slice(x * 16 + 0, 8));
                double l2 = BinaryPrimitives.ReadDoubleLittleEndian(la.Slice(x * 16 + 8, 8));
                double r1 = BinaryPrimitives.ReadDoubleLittleEndian(ra.Slice(x * 16 + 0, 8));
                double r2 = BinaryPrimitives.ReadDoubleLittleEndian(ra.Slice(x * 16 + 8, 8));
                double o1, o2;
                switch (op)
                {
                    case VipsComplex2Op.CrossPhase:
                        // (l · conj(r)).
                        o1 = l1 * r1 + l2 * r2;
                        o2 = l2 * r1 - l1 * r2;
                        break;
                    default: o1 = 0; o2 = 0; break;
                }
                BinaryPrimitives.WriteDoubleLittleEndian(oa.Slice(x * 16 + 0, 8), o1);
                BinaryPrimitives.WriteDoubleLittleEndian(oa.Slice(x * 16 + 8, 8), o2);
            }
        }
        return 0;
    }
}
