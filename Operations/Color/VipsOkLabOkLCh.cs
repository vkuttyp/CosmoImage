using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Color;

/// <summary>
/// OkLab → OkLCh — polar form of OkLab. <c>L</c> stays unchanged;
/// <c>C = sqrt(a² + b²)</c>, <c>h = atan2(b, a)</c> in degrees
/// <c>[0, 360)</c>. Mirrors libvips <c>vips_Oklab2Oklch</c>.
///
/// <para>OkLCh is the practical workspace for hue-preserving colour
/// adjustments (rotate, desaturate) — its hue circle is far more
/// uniform than CIE LCh, so a 30°-rotation looks like a 30° rotation
/// across the whole gamut rather than warping toward yellow at high
/// chroma.</para>
/// </summary>
public class VipsOkLab2OkLCh : VipsOperation
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
            Interpretation = VipsInterpretation.OkLCh,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("OkLab2OkLCh", RuntimeHelpers.GetHashCode(In));

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
                double C = Math.Sqrt(aa * aa + bb * bb);
                double h = Math.Atan2(bb, aa) * 180 / Math.PI;
                if (h < 0) h += 360;
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(o + 0, 4), (float)L);
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(o + 4, 4), (float)C);
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(o + 8, 4), (float)h);
            }
        }
        return 0;
    }
}

/// <summary>
/// OkLCh → OkLab. Inverse of <see cref="VipsOkLab2OkLCh"/>; mirrors
/// libvips <c>vips_Oklch2Oklab</c>.
/// </summary>
public class VipsOkLCh2OkLab : VipsOperation
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
        => HashCode.Combine("OkLCh2OkLab", RuntimeHelpers.GetHashCode(In));

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
                double C = BinaryPrimitives.ReadSingleLittleEndian(inAddr.Slice(o + 4, 4));
                double h = BinaryPrimitives.ReadSingleLittleEndian(inAddr.Slice(o + 8, 4));
                double rad = h * Math.PI / 180;
                double aa = C * Math.Cos(rad);
                double bb = C * Math.Sin(rad);
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(o + 0, 4), (float)L);
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(o + 4, 4), (float)aa);
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(o + 8, 4), (float)bb);
            }
        }
        return 0;
    }
}
