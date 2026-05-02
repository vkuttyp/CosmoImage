using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Color;

/// <summary>
/// scRGB (linear-light, sRGB primaries, D65 white) → XYZ. The
/// standard sRGB-primary 3×3 matrix. Mirrors libvips
/// <c>vips_scRGB2XYZ</c>. Float 3-band only.
///
/// <para>scRGB is the spine of HDR / wide-gamut pipelines: same
/// primaries as sRGB, but unbounded — values &lt; 0 sit outside the
/// gamut, &gt; 1 sit above the SDR diffuse white. Round-tripping
/// scRGB → XYZ → Lab → … → scRGB is loss-less to floating-point
/// precision, unlike the gamma-encoded sRGB path.</para>
/// </summary>
public class VipsScRGB2XYZ : VipsOperation
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
        => HashCode.Combine("scRGB2XYZ", RuntimeHelpers.GetHashCode(In));

    public static (double X, double Y, double Z) ScRGB2XYZ(double R, double G, double B)
    {
        double X = 0.4124564 * R + 0.3575761 * G + 0.1804375 * B;
        double Y = 0.2126729 * R + 0.7151522 * G + 0.0721750 * B;
        double Z = 0.0193339 * R + 0.1191920 * G + 0.9503041 * B;
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
                double R = BinaryPrimitives.ReadSingleLittleEndian(inAddr.Slice(o + 0, 4));
                double G = BinaryPrimitives.ReadSingleLittleEndian(inAddr.Slice(o + 4, 4));
                double B = BinaryPrimitives.ReadSingleLittleEndian(inAddr.Slice(o + 8, 4));
                var (X, Y, Z) = ScRGB2XYZ(R, G, B);
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(o + 0, 4), (float)X);
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(o + 4, 4), (float)Y);
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(o + 8, 4), (float)Z);
            }
        }
        return 0;
    }
}

/// <summary>
/// XYZ → scRGB (linear-light, sRGB primaries, D65). Inverse of the
/// sRGB-primary matrix. Mirrors libvips <c>vips_XYZ2scRGB</c>. Output
/// can sit outside [0, 1] — that's the point of scRGB.
/// </summary>
public class VipsXYZ2scRGB : VipsOperation
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
            Interpretation = VipsInterpretation.scRGB,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("XYZ2scRGB", RuntimeHelpers.GetHashCode(In));

    public static (double R, double G, double B) XYZ2ScRGB(double X, double Y, double Z)
    {
        double R = +3.2404542 * X - 1.5371385 * Y - 0.4985314 * Z;
        double G = -0.9692660 * X + 1.8760108 * Y + 0.0415560 * Z;
        double B = +0.0556434 * X - 0.2040259 * Y + 1.0572252 * Z;
        return (R, G, B);
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
                var (R, G, B) = XYZ2ScRGB(X, Y, Z);
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(o + 0, 4), (float)R);
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(o + 4, 4), (float)G);
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(o + 8, 4), (float)B);
            }
        }
        return 0;
    }
}
