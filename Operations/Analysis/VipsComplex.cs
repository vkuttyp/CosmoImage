using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Analysis;

public enum VipsComplexOp
{
    /// <summary>(re, im) → (mag, angle).</summary>
    Polar = 0,
    /// <summary>(mag, angle) → (re, im).</summary>
    Rect = 1,
    /// <summary>Complex conjugate — negate imaginary component.</summary>
    Conj = 2,
}

/// <summary>
/// Per-pixel unary op on a DPComplex image. Mirrors libvips
/// <c>vips_complex</c>.
///
/// <para>Polar reinterprets each complex sample as
/// <c>(magnitude, angle)</c> — useful for visualising spectra. Rect
/// is the inverse, taking polar pairs back to rectangular form.
/// Conj negates the imaginary component (the standard complex
/// conjugate).</para>
/// </summary>
public class VipsComplex : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public VipsComplexOp Op { get; set; }

    public override int Build()
    {
        if (In == null) return -1;
        if (In.BandFormat != VipsBandFormat.DPComplex) return -1;
        if (In.Bands != 1) return -1;

        Out = new VipsImage
        {
            Width = In.Width, Height = In.Height, Bands = 1,
            BandFormat = VipsBandFormat.DPComplex,
            Interpretation = In.Interpretation,
            XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In, ClientB = Op,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Complex", RuntimeHelpers.GetHashCode(In), Op);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inReg = (VipsRegion)seq!;
        var op = (VipsComplexOp)b!;
        VipsRect r = outRegion.Valid;
        if (inReg.Prepare(r) != 0) return -1;

        for (int y = 0; y < r.Height; y++)
        {
            var ia = inReg.GetAddress(r.Left, r.Top + y);
            var oa = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                double a1 = BinaryPrimitives.ReadDoubleLittleEndian(ia.Slice(x * 16 + 0, 8));
                double a2 = BinaryPrimitives.ReadDoubleLittleEndian(ia.Slice(x * 16 + 8, 8));
                double o1, o2;
                switch (op)
                {
                    case VipsComplexOp.Polar:
                        o1 = Math.Sqrt(a1 * a1 + a2 * a2);
                        o2 = Math.Atan2(a2, a1);
                        break;
                    case VipsComplexOp.Rect:
                        // a1 = magnitude, a2 = angle (radians).
                        o1 = a1 * Math.Cos(a2);
                        o2 = a1 * Math.Sin(a2);
                        break;
                    default: // Conj
                        o1 = a1; o2 = -a2;
                        break;
                }
                BinaryPrimitives.WriteDoubleLittleEndian(oa.Slice(x * 16 + 0, 8), o1);
                BinaryPrimitives.WriteDoubleLittleEndian(oa.Slice(x * 16 + 8, 8), o2);
            }
        }
        return 0;
    }
}
