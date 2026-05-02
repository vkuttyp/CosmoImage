using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Color;

/// <summary>
/// CIE Lab → XYZ under the D65 illuminant. Mirrors libvips
/// <c>vips_Lab2XYZ</c>. Float 3-band only — XYZ is a linear-light
/// space, encoding it in UChar would force a clamp every operation.
///
/// <para>
/// X = Xn · f⁻¹((L + 16) / 116 + a / 500)<br/>
/// Y = Yn · f⁻¹((L + 16) / 116)<br/>
/// Z = Zn · f⁻¹((L + 16) / 116 − b / 200)<br/>
/// </para>
/// <para>D65 white: Xn = 0.95047, Yn = 1.00000, Zn = 1.08883.</para>
/// </summary>
public class VipsLab2XYZ : VipsOperation
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
        => HashCode.Combine("Lab2XYZ", RuntimeHelpers.GetHashCode(In));

    private const double Xn = 0.95047, Yn = 1.0, Zn = 1.08883;
    private const double Delta = 6.0 / 29;

    /// <summary>Inverse of the Lab f-function.</summary>
    public static double FInv(double t) => t > Delta ? t * t * t : 3 * Delta * Delta * (t - 4.0 / 29);

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
                double t = (L + 16) / 116.0;
                double X = Xn * FInv(t + aa / 500);
                double Y = Yn * FInv(t);
                double Z = Zn * FInv(t - bb / 200);
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(o + 0, 4), (float)X);
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(o + 4, 4), (float)Y);
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(o + 8, 4), (float)Z);
            }
        }
        return 0;
    }
}

/// <summary>
/// CIE XYZ (D65) → Lab. Mirrors libvips <c>vips_XYZ2Lab</c>. Float
/// 3-band only.
/// </summary>
public class VipsXYZ2Lab : VipsOperation
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
        => HashCode.Combine("XYZ2Lab", RuntimeHelpers.GetHashCode(In));

    private const double Xn = 0.95047, Yn = 1.0, Zn = 1.08883;
    private const double Delta = 6.0 / 29;

    /// <summary>Lab f-function: cube-root with linear toe segment.</summary>
    public static double F(double t)
        => t > Delta * Delta * Delta ? Math.Cbrt(t) : t / (3 * Delta * Delta) + 4.0 / 29;

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
                double fx = F(X / Xn), fy = F(Y / Yn), fz = F(Z / Zn);
                double L = 116 * fy - 16;
                double aa = 500 * (fx - fy);
                double bb = 200 * (fy - fz);
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(o + 0, 4), (float)L);
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(o + 4, 4), (float)aa);
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(o + 8, 4), (float)bb);
            }
        }
        return 0;
    }
}
