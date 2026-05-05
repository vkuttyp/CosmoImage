using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Color;

/// <summary>
/// CIE L*a*b* → DIN99 (DIN 6176:2001). Perceptually-uniform Lab
/// variant where Euclidean distance in DIN99 space approximates
/// CIEDE2000 colour difference at a fraction of the cost.
///
/// <para>Useful for clustering / nearest-neighbour searches in
/// colour space — operations that benefit from perceptual uniformity
/// but don't justify a per-pair ΔE2000 evaluation. Round-trip
/// through <see cref="VipsDIN992Lab"/> is loss-less to floating-
/// point precision.</para>
///
/// <para>Closed-form math, no lookup tables — simpler to ship than
/// libvips' specific UCS (which uses Munsell renotation tables).
/// Both spaces solve the same "perceptual uniformity for clustering"
/// use case.</para>
/// </summary>
public class VipsLab2DIN99 : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }

    public override int Build()
    {
        if (In == null) return -1;
        if (In.BandFormat != VipsBandFormat.Float || In.Bands != 3) return -1;

        Out = new VipsImage
        {
            Width = In.Width, Height = In.Height, Bands = 3, BandFormat = VipsBandFormat.Float,
            Interpretation = In.Interpretation, // no dedicated DIN99 enum yet — pass through
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Lab2DIN99", RuntimeHelpers.GetHashCode(In));

    /// <summary>
    /// Convert one CIE L*a*b* triple to DIN99 (L99, a99, b99).
    /// Constants per DIN 6176:2001 — kE = kCH = 1.
    /// </summary>
    public static (double L99, double a99, double b99) Lab2DIN99(double L, double a, double b)
    {
        // Lightness compression: log-shaped, range 0..100 stays ~0..100.
        double L99 = 105.51 * Math.Log(1.0 + 0.0158 * L);
        // 16° rotation in the a*b* plane, then squash the b axis by 0.7
        // to compensate for the eye's reduced sensitivity in the
        // yellow-blue direction.
        double e = a * Cos16 + b * Sin16;
        double f = (-a * Sin16 + b * Cos16) * 0.7;
        // Chroma compression: log-shaped to flatten high-chroma
        // perceptual differences.
        double G = Math.Sqrt(e * e + f * f);
        double C99 = Math.Log(1.0 + 0.045 * G) / 0.045;
        // Hue rotated back, applied as polar coordinates.
        double H99rad = Math.Atan2(f, e) + Sixteen;
        double a99 = C99 * Math.Cos(H99rad);
        double b99_ = C99 * Math.Sin(H99rad);
        return (L99, a99, b99_);
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsRect r = outRegion.Valid;
        if (inRegion.Prepare(r) != 0) return -1;

        for (int y = 0; y < r.Height; y++)
        {
            var inAddr = inRegion.GetAddress(r.Left, r.Top + y);
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                int o = x * 12;
                double L = BinaryPrimitives.ReadSingleLittleEndian(inAddr.Slice(o + 0, 4));
                double aa = BinaryPrimitives.ReadSingleLittleEndian(inAddr.Slice(o + 4, 4));
                double bb = BinaryPrimitives.ReadSingleLittleEndian(inAddr.Slice(o + 8, 4));
                var (L99, a99, b99) = Lab2DIN99(L, aa, bb);
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(o + 0, 4), (float)L99);
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(o + 4, 4), (float)a99);
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(o + 8, 4), (float)b99);
            }
        }
        return 0;
    }

    private const double Sixteen = 16.0 * Math.PI / 180.0;
    private static readonly double Cos16 = Math.Cos(Sixteen);
    private static readonly double Sin16 = Math.Sin(Sixteen);
}

/// <summary>
/// DIN99 → CIE L*a*b*. Inverse of <see cref="VipsLab2DIN99"/>;
/// reverses the chroma compression, the 16° rotation, and the
/// log-shaped lightness compression.
/// </summary>
public class VipsDIN992Lab : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }

    public override int Build()
    {
        if (In == null) return -1;
        if (In.BandFormat != VipsBandFormat.Float || In.Bands != 3) return -1;

        Out = new VipsImage
        {
            Width = In.Width, Height = In.Height, Bands = 3, BandFormat = VipsBandFormat.Float,
            Interpretation = VipsInterpretation.Lab,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("DIN992Lab", RuntimeHelpers.GetHashCode(In));

    /// <summary>
    /// Convert one DIN99 triple back to CIE L*a*b*.
    /// </summary>
    public static (double L, double a, double b) DIN992Lab(double L99, double a99, double b99)
    {
        // Reverse chroma compression: ln-based decompression.
        double C99 = Math.Sqrt(a99 * a99 + b99 * b99);
        double H99rad = Math.Atan2(b99, a99) - Sixteen;
        double G = (Math.Exp(0.045 * C99) - 1.0) / 0.045;
        double e = G * Math.Cos(H99rad);
        double f = G * Math.Sin(H99rad);
        // Undo the 0.7 squash on f, then reverse the 16° rotation.
        double a = e * Cos16 - (f / 0.7) * Sin16;
        double b = e * Sin16 + (f / 0.7) * Cos16;
        // Reverse lightness compression.
        double L = (Math.Exp(L99 / 105.51) - 1.0) / 0.0158;
        return (L, a, b);
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsRect r = outRegion.Valid;
        if (inRegion.Prepare(r) != 0) return -1;

        for (int y = 0; y < r.Height; y++)
        {
            var inAddr = inRegion.GetAddress(r.Left, r.Top + y);
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                int o = x * 12;
                double L99 = BinaryPrimitives.ReadSingleLittleEndian(inAddr.Slice(o + 0, 4));
                double a99 = BinaryPrimitives.ReadSingleLittleEndian(inAddr.Slice(o + 4, 4));
                double b99 = BinaryPrimitives.ReadSingleLittleEndian(inAddr.Slice(o + 8, 4));
                var (L, aa, bb) = DIN992Lab(L99, a99, b99);
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(o + 0, 4), (float)L);
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(o + 4, 4), (float)aa);
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(o + 8, 4), (float)bb);
            }
        }
        return 0;
    }

    private const double Sixteen = 16.0 * Math.PI / 180.0;
    private static readonly double Cos16 = Math.Cos(Sixteen);
    private static readonly double Sin16 = Math.Sin(Sixteen);
}
