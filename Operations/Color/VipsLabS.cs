using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Color;

/// <summary>
/// Float Lab → 16-bit signed-short Lab. Mirrors libvips
/// <c>vips_Lab2LabS</c>. The mapping is:
/// <list type="bullet">
///   <item>L: <c>0..100</c> → <c>0..32767</c> (factor 327.67)</item>
///   <item>a: <c>-128..128</c> → <c>-32768..32767</c> (factor 256)</item>
///   <item>b: same as a</item>
/// </list>
///
/// <para>LabS is the high-precision intermediate format libvips uses
/// for chained colour ops — float-precision Lab arithmetic with 16-bit
/// storage avoids the rounding artefacts of LabQ while staying tighter
/// than full Float. Output is a 3-band Short image.</para>
/// </summary>
public class VipsLab2LabS : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }

    public override int Build()
    {
        if (In == null) return -1;
        if (In.BandFormat != VipsBandFormat.Float || In.Bands != 3) return -1;

        Out = new VipsImage
        {
            Width = In.Width, Height = In.Height, Bands = 3, BandFormat = VipsBandFormat.Short,
            Interpretation = VipsInterpretation.LabS,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Lab2LabS", RuntimeHelpers.GetHashCode(In));

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
                double L = BinaryPrimitives.ReadSingleLittleEndian(inAddr.Slice(x * 12 + 0, 4));
                double aa = BinaryPrimitives.ReadSingleLittleEndian(inAddr.Slice(x * 12 + 4, 4));
                double bb = BinaryPrimitives.ReadSingleLittleEndian(inAddr.Slice(x * 12 + 8, 4));

                short Ls = (short)Math.Clamp(Math.Round(L * 327.67), short.MinValue, short.MaxValue);
                short As = (short)Math.Clamp(Math.Round(aa * 256.0), short.MinValue, short.MaxValue);
                short Bs = (short)Math.Clamp(Math.Round(bb * 256.0), short.MinValue, short.MaxValue);

                BinaryPrimitives.WriteInt16LittleEndian(outAddr.Slice(x * 6 + 0, 2), Ls);
                BinaryPrimitives.WriteInt16LittleEndian(outAddr.Slice(x * 6 + 2, 2), As);
                BinaryPrimitives.WriteInt16LittleEndian(outAddr.Slice(x * 6 + 4, 2), Bs);
            }
        }
        return 0;
    }
}

/// <summary>
/// 16-bit signed-short Lab → Float Lab. Inverse of
/// <see cref="VipsLab2LabS"/>; mirrors libvips <c>vips_LabS2Lab</c>.
/// </summary>
public class VipsLabS2Lab : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }

    public override int Build()
    {
        if (In == null) return -1;
        if (In.BandFormat != VipsBandFormat.Short || In.Bands != 3) return -1;

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
        => HashCode.Combine("LabS2Lab", RuntimeHelpers.GetHashCode(In));

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
                short Ls = BinaryPrimitives.ReadInt16LittleEndian(inAddr.Slice(x * 6 + 0, 2));
                short As = BinaryPrimitives.ReadInt16LittleEndian(inAddr.Slice(x * 6 + 2, 2));
                short Bs = BinaryPrimitives.ReadInt16LittleEndian(inAddr.Slice(x * 6 + 4, 2));

                float L = (float)(Ls / 327.67);
                float aa = As / 256.0f;
                float bb = Bs / 256.0f;

                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(x * 12 + 0, 4), L);
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(x * 12 + 4, 4), aa);
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(x * 12 + 8, 4), bb);
            }
        }
        return 0;
    }
}
