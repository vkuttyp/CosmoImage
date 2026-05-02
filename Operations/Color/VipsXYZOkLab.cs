using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Color;

/// <summary>
/// XYZ (D65) → OkLab. Björn Ottosson's perceptual colour space
/// (2020) — designed to be close to CIELAB in lightness while being
/// markedly more uniform along hue and chroma. The increasingly
/// standard pick for hue-preserving gradients and tone-mapping
/// pipelines (used by CSS Color 4, web design tooling, and several
/// modern HDR workflows). Mirrors libvips <c>vips_XYZ2Oklab</c>.
///
/// <para>The transform is XYZ → linear LMS via <c>M1</c>, cube-root,
/// then linear LMS' → OkLab via <c>M2</c>. Reference white (D65) maps
/// to <c>(L, a, b) = (1, 0, 0)</c> — note OkLab L is in <c>[0, 1]</c>,
/// unlike CIE Lab which is in <c>[0, 100]</c>.</para>
/// </summary>
public class VipsXYZ2OkLab : VipsOperation
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
            Interpretation = VipsInterpretation.OkLab,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("XYZ2OkLab", RuntimeHelpers.GetHashCode(In));

    /// <summary>Forward XYZ → OkLab (Ottosson 2020).</summary>
    public static (double L, double a, double b) XYZ2OkLab(double X, double Y, double Z)
    {
        double l = 0.8189330101 * X + 0.3618667424 * Y - 0.1288597137 * Z;
        double m = 0.0329845436 * X + 0.9293118715 * Y + 0.0361456387 * Z;
        double s = 0.0482003018 * X + 0.2643662691 * Y + 0.6338517070 * Z;
        double lp = Math.Cbrt(l), mp = Math.Cbrt(m), sp = Math.Cbrt(s);
        double L = 0.2104542553 * lp + 0.7936177850 * mp - 0.0040720468 * sp;
        double aa = 1.9779984951 * lp - 2.4285922050 * mp + 0.4505937099 * sp;
        double bb = 0.0259040371 * lp + 0.7827717662 * mp - 0.8086757660 * sp;
        return (L, aa, bb);
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
                double X = BinaryPrimitives.ReadSingleLittleEndian(inAddr.Slice(o + 0, 4));
                double Y = BinaryPrimitives.ReadSingleLittleEndian(inAddr.Slice(o + 4, 4));
                double Z = BinaryPrimitives.ReadSingleLittleEndian(inAddr.Slice(o + 8, 4));
                var (L, aa, bb) = XYZ2OkLab(X, Y, Z);
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(o + 0, 4), (float)L);
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(o + 4, 4), (float)aa);
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(o + 8, 4), (float)bb);
            }
        }
        return 0;
    }
}

/// <summary>
/// OkLab → XYZ (D65). Inverse of <see cref="VipsXYZ2OkLab"/>; mirrors
/// libvips <c>vips_Oklab2XYZ</c>.
/// </summary>
public class VipsOkLab2XYZ : VipsOperation
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
            Interpretation = VipsInterpretation.XYZ,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("OkLab2XYZ", RuntimeHelpers.GetHashCode(In));

    /// <summary>Inverse OkLab → XYZ (Ottosson 2020).</summary>
    public static (double X, double Y, double Z) OkLab2XYZ(double L, double a, double b)
    {
        double lp = L + 0.3963377774 * a + 0.2158037573 * b;
        double mp = L - 0.1055613458 * a - 0.0638541728 * b;
        double sp = L - 0.0894841775 * a - 1.2914855480 * b;
        double l = lp * lp * lp, m = mp * mp * mp, s = sp * sp * sp;
        double X = 1.2270138511 * l - 0.5577999807 * m + 0.2812561489 * s;
        double Y = -0.0405801784 * l + 1.1122568696 * m - 0.0716766787 * s;
        double Z = -0.0763812845 * l - 0.4214819784 * m + 1.5861632204 * s;
        return (X, Y, Z);
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
                var (X, Y, Z) = OkLab2XYZ(L, aa, bb);
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(o + 0, 4), (float)X);
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(o + 4, 4), (float)Y);
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(o + 8, 4), (float)Z);
            }
        }
        return 0;
    }
}
