using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Analysis;

/// <summary>
/// Build a DPComplex image from two Float real-valued images: the first
/// supplies the real component, the second the imaginary. Mirrors
/// libvips <c>vips_complexform</c>.
///
/// <para>The natural inverse is <see cref="VipsComplexGet"/>. Both
/// inputs must be Float 1-band and agree on dimensions; output is
/// DPComplex 1-band of the same dimensions.</para>
/// </summary>
public class VipsComplexForm : VipsOperation
{
    public VipsImage? Real { get; set; }
    public VipsImage? Imag { get; set; }
    public VipsImage? Out { get; set; }

    public override int Build()
    {
        if (Real == null || Imag == null) return -1;
        if (Real.BandFormat != VipsBandFormat.Float || Imag.BandFormat != VipsBandFormat.Float) return -1;
        if (Real.Bands != 1 || Imag.Bands != 1) return -1;
        if (Real.Width != Imag.Width || Real.Height != Imag.Height) return -1;

        Out = new VipsImage
        {
            Width = Real.Width, Height = Real.Height, Bands = 1,
            BandFormat = VipsBandFormat.DPComplex,
            Interpretation = VipsInterpretation.Fourier,
            XRes = Real.XRes, YRes = Real.YRes,
            StartFn = VipsSeq.StartMany, GenerateFn = Generate, StopFn = VipsSeq.StopMany,
            ClientA = new[] { Real, Imag },
        };
        Out.CopyMetadataFrom(Real);
        Out.SetPipeline(VipsDemandStyle.Any, Real, Imag);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("ComplexForm", RuntimeHelpers.GetHashCode(Real),
            RuntimeHelpers.GetHashCode(Imag));

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var regions = (VipsRegion[])seq!;
        VipsRect r = outRegion.Valid;
        if (regions[0].Prepare(r) != 0) return -1;
        if (regions[1].Prepare(r) != 0) return -1;

        for (int y = 0; y < r.Height; y++)
        {
            var ra = regions[0].GetAddress(r.Left, r.Top + y);
            var ia = regions[1].GetAddress(r.Left, r.Top + y);
            var oa = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                double re = BinaryPrimitives.ReadSingleLittleEndian(ra.Slice(x * 4, 4));
                double im = BinaryPrimitives.ReadSingleLittleEndian(ia.Slice(x * 4, 4));
                BinaryPrimitives.WriteDoubleLittleEndian(oa.Slice(x * 16 + 0, 8), re);
                BinaryPrimitives.WriteDoubleLittleEndian(oa.Slice(x * 16 + 8, 8), im);
            }
        }
        return 0;
    }
}
