using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Color;

/// <summary>
/// XYZ → Yxy (CIE chromaticity coordinates).
/// <para>
/// Y stays unchanged; the chromaticity coordinates are
/// <c>x = X / (X + Y + Z)</c> and <c>y = Y / (X + Y + Z)</c>. Mirrors
/// libvips <c>vips_XYZ2Yxy</c>. Float 3-band only.
/// </para>
///
/// <para>Yxy is the natural space for working with chromaticity
/// independent of luminance — D65, for instance, is at
/// (x, y) ≈ (0.3127, 0.3290) regardless of brightness. For
/// pure-black input (X = Y = Z = 0) the chromaticity is undefined; we
/// follow libvips and emit (0, 0, 0).</para>
/// </summary>
public class VipsXYZ2Yxy : VipsOperation
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
            Interpretation = VipsInterpretation.YXY,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("XYZ2Yxy", RuntimeHelpers.GetHashCode(In));

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
                double sum = X + Y + Z;
                float yo = (float)Y;
                float xx = sum > 0 ? (float)(X / sum) : 0f;
                float yy = sum > 0 ? (float)(Y / sum) : 0f;
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(o + 0, 4), yo);
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(o + 4, 4), xx);
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(o + 8, 4), yy);
            }
        }
        return 0;
    }
}

/// <summary>
/// Yxy → XYZ. Mirrors libvips <c>vips_Yxy2XYZ</c>.
/// <code>X = x · Y / y; Z = (1 − x − y) · Y / y</code>
/// For y = 0 we emit black (X = Y = Z = 0).
/// </summary>
public class VipsYxy2XYZ : VipsOperation
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
        => HashCode.Combine("Yxy2XYZ", RuntimeHelpers.GetHashCode(In));

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
                double Y = BinaryPrimitives.ReadSingleLittleEndian(inAddr.Slice(o + 0, 4));
                double xx = BinaryPrimitives.ReadSingleLittleEndian(inAddr.Slice(o + 4, 4));
                double yy = BinaryPrimitives.ReadSingleLittleEndian(inAddr.Slice(o + 8, 4));
                float Xo, Yo, Zo;
                if (yy == 0) { Xo = 0; Yo = 0; Zo = 0; }
                else
                {
                    Xo = (float)(xx * Y / yy);
                    Yo = (float)Y;
                    Zo = (float)((1 - xx - yy) * Y / yy);
                }
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(o + 0, 4), Xo);
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(o + 4, 4), Yo);
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(o + 8, 4), Zo);
            }
        }
        return 0;
    }
}
