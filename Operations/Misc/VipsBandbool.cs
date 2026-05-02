using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Misc;

/// <summary>
/// Reduce across the band axis with a bitwise op: AND/OR/XOR fold the
/// input's bands down into a single-band UChar output. Each output
/// pixel is <c>in[0] op in[1] op … op in[K-1]</c>.
///
/// <para>UChar only — bitwise ops on Float make no physical sense.
/// Single-band input is a pass-through. Mirrors libvips <c>vips_bandbool</c>.</para>
/// </summary>
public class VipsBandbool : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public VipsBooleanOperation Op { get; set; }

    public override int Build()
    {
        if (In == null) return -1;
        if (In.BandFormat != VipsBandFormat.UChar) return -1;
        if (In.Bands == 1) { Out = In; return 0; }

        Out = new VipsImage
        {
            Width = In.Width, Height = In.Height, Bands = 1,
            BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In, ClientB = Op,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Bandbool", RuntimeHelpers.GetHashCode(In), Op);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        VipsBooleanOperation op = (VipsBooleanOperation)b!;
        VipsRect r = outRegion.Valid;

        if (inRegion.Prepare(r) != 0) return -1;
        int bands = @in.Bands;

        for (int y = 0; y < r.Height; y++)
        {
            var inAddr = inRegion.GetAddress(r.Left, r.Top + y);
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                int pel = x * bands;
                byte acc = inAddr[pel];
                for (int bnd = 1; bnd < bands; bnd++)
                {
                    byte rhs = inAddr[pel + bnd];
                    acc = op switch
                    {
                        VipsBooleanOperation.And => (byte)(acc & rhs),
                        VipsBooleanOperation.Or => (byte)(acc | rhs),
                        VipsBooleanOperation.Xor => (byte)(acc ^ rhs),
                        _ => acc,
                    };
                }
                outAddr[x] = acc;
            }
        }
        return 0;
    }
}

/// <summary>
/// Average all bands into a single-band output of the same band format.
/// Mirrors libvips <c>vips_bandmean</c>. Common use: collapse RGB to
/// gray for further analysis without going through a colour transform.
/// </summary>
public class VipsBandmean : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }

    public override int Build()
    {
        if (In == null) return -1;
        if (In.Bands == 1) { Out = In; return 0; }

        Out = new VipsImage
        {
            Width = In.Width, Height = In.Height, Bands = 1,
            BandFormat = In.BandFormat,
            Interpretation = VipsInterpretation.BW,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Bandmean", RuntimeHelpers.GetHashCode(In));

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        VipsRect r = outRegion.Valid;

        if (inRegion.Prepare(r) != 0) return -1;
        int bands = @in.Bands;

        if (@in.BandFormat == VipsBandFormat.Float)
        {
            for (int y = 0; y < r.Height; y++)
            {
                var inAddr = inRegion.GetAddress(r.Left, r.Top + y);
                var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
                for (int x = 0; x < r.Width; x++)
                {
                    float acc = 0;
                    for (int bnd = 0; bnd < bands; bnd++)
                        acc += System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(
                            inAddr.Slice((x * bands + bnd) * 4, 4));
                    System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(
                        outAddr.Slice(x * 4, 4), acc / bands);
                }
            }
            return 0;
        }

        // UChar fast path: integer mean with rounding.
        int half = bands / 2;
        for (int y = 0; y < r.Height; y++)
        {
            var inAddr = inRegion.GetAddress(r.Left, r.Top + y);
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                int pel = x * bands;
                int sum = 0;
                for (int bnd = 0; bnd < bands; bnd++) sum += inAddr[pel + bnd];
                outAddr[x] = (byte)((sum + half) / bands);
            }
        }
        return 0;
    }
}
