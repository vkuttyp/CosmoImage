using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Color;

/// <summary>
/// Naïve CMYK → XYZ. Mirrors libvips' no-profile CMYK fallback.
///
/// <para>The transform is CMYK (UChar, 0..255 per band) → linear sRGB
/// via the standard <c>R = (1−C)(1−K)</c> formula, then linear sRGB →
/// XYZ via the sRGB-primary matrix. This is not a proper printing
/// CMYK conversion — for that, use an ICC profile through
/// <c>IccTransform</c>. Use this op when the input has been treated
/// as nominal CMYK without colorant data.</para>
/// </summary>
public class VipsCMYK2XYZ : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }

    public override int Build()
    {
        if (In == null) return -1;
        if (In.BandFormat != VipsBandFormat.UChar || In.Bands != 4) return -1;

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
        => HashCode.Combine("CMYK2XYZ", RuntimeHelpers.GetHashCode(In));

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
                int i = x * 4;
                double C = inAddr[i + 0] / 255.0;
                double M = inAddr[i + 1] / 255.0;
                double Yc = inAddr[i + 2] / 255.0;
                double K = inAddr[i + 3] / 255.0;
                double R = (1 - C) * (1 - K);
                double G = (1 - M) * (1 - K);
                double B = (1 - Yc) * (1 - K);
                var (X, Y, Z) = VipsScRGB2XYZ.ScRGB2XYZ(R, G, B);
                int o = x * 12;
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(o + 0, 4), (float)X);
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(o + 4, 4), (float)Y);
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(o + 8, 4), (float)Z);
            }
        }
        return 0;
    }
}

/// <summary>
/// Naïve XYZ → CMYK. Mirrors libvips' no-profile fallback. XYZ → linear
/// sRGB (clamped to [0, 1]) → CMYK via <c>K = 1 − max(R, G, B)</c>
/// followed by <c>C = (1−R−K)/(1−K)</c> etc.
/// </summary>
public class VipsXYZ2CMYK : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }

    public override int Build()
    {
        if (In == null) return -1;
        if (In.BandFormat != VipsBandFormat.Float || In.Bands != 3) return -1;

        Out = new VipsImage
        {
            Width = In.Width, Height = In.Height, Bands = 4, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.CMYK,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("XYZ2CMYK", RuntimeHelpers.GetHashCode(In));

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
                var (R, G, B) = VipsXYZ2scRGB.XYZ2ScRGB(X, Y, Z);
                R = Math.Clamp(R, 0, 1);
                G = Math.Clamp(G, 0, 1);
                B = Math.Clamp(B, 0, 1);
                double K = 1 - Math.Max(R, Math.Max(G, B));
                double C, M, Yc;
                if (K >= 1.0)
                {
                    C = 0; M = 0; Yc = 0;
                }
                else
                {
                    double inv = 1 - K;
                    C = (1 - R - K) / inv;
                    M = (1 - G - K) / inv;
                    Yc = (1 - B - K) / inv;
                }
                int oi = x * 4;
                outAddr[oi + 0] = (byte)Math.Clamp(Math.Round(C * 255), 0, 255);
                outAddr[oi + 1] = (byte)Math.Clamp(Math.Round(M * 255), 0, 255);
                outAddr[oi + 2] = (byte)Math.Clamp(Math.Round(Yc * 255), 0, 255);
                outAddr[oi + 3] = (byte)Math.Clamp(Math.Round(K * 255), 0, 255);
            }
        }
        return 0;
    }
}
