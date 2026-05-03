using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Analysis;

public enum VipsComplexGetMode
{
    Real = 0,
    Imag = 1,
    Magnitude = 2,
    Phase = 3,
}

/// <summary>
/// Extract a Float real component from a DPComplex image. Mirrors
/// libvips <c>vips_complexget</c>. Selects via
/// <see cref="VipsComplexGetMode"/>: real part, imaginary part,
/// magnitude <c>|z|</c>, or phase <c>arg(z)</c> in radians.
///
/// <para>Inverse of <see cref="VipsComplexForm"/> when used with
/// <c>Real</c> / <c>Imag</c>; the polar pair is
/// <c>Magnitude</c> / <c>Phase</c>.</para>
/// </summary>
public class VipsComplexGet : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public VipsComplexGetMode Mode { get; set; }

    public override int Build()
    {
        if (In == null) return -1;
        if (In.BandFormat != VipsBandFormat.DPComplex) return -1;
        if (In.Bands != 1) return -1;

        Out = new VipsImage
        {
            Width = In.Width, Height = In.Height, Bands = 1,
            BandFormat = VipsBandFormat.Float,
            Interpretation = VipsInterpretation.BW,
            XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In, ClientB = Mode,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("ComplexGet", RuntimeHelpers.GetHashCode(In), Mode);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inReg = (VipsRegion)seq!;
        var mode = (VipsComplexGetMode)b!;
        VipsRect r = outRegion.Valid;
        if (inReg.Prepare(r) != 0) return -1;

        for (int y = 0; y < r.Height; y++)
        {
            var ia = inReg.GetAddress(r.Left, r.Top + y);
            var oa = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                double re = BinaryPrimitives.ReadDoubleLittleEndian(ia.Slice(x * 16 + 0, 8));
                double im = BinaryPrimitives.ReadDoubleLittleEndian(ia.Slice(x * 16 + 8, 8));
                float v = mode switch
                {
                    VipsComplexGetMode.Real => (float)re,
                    VipsComplexGetMode.Imag => (float)im,
                    VipsComplexGetMode.Magnitude => (float)Math.Sqrt(re * re + im * im),
                    VipsComplexGetMode.Phase => (float)Math.Atan2(im, re),
                    _ => 0,
                };
                BinaryPrimitives.WriteSingleLittleEndian(oa.Slice(x * 4, 4), v);
            }
        }
        return 0;
    }
}
