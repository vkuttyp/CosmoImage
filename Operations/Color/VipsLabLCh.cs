using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Color;

/// <summary>
/// Cartesian Lab → polar LCh. Mirrors libvips <c>vips_Lab2LCh</c>.
/// L stays unchanged; chroma is the radial distance in the a/b plane,
/// hue the angle in degrees [0, 360).
///
/// <para>LCh is the natural space for working with hue and saturation
/// independently — common operations include "rotate hue by 30°"
/// (<c>h += 30</c>) or "desaturate by 20%" (<c>C *= 0.8</c>) without
/// disturbing lightness.</para>
/// </summary>
public class VipsLab2LCh : VipsOperation
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
            Interpretation = VipsInterpretation.LCh,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Lab2LCh", RuntimeHelpers.GetHashCode(In));

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
/// Polar LCh → Cartesian Lab. Mirrors libvips <c>vips_LCh2Lab</c>.
/// </summary>
public class VipsLCh2Lab : VipsOperation
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
        => HashCode.Combine("LCh2Lab", RuntimeHelpers.GetHashCode(In));

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
