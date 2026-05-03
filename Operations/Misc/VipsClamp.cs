using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Misc;

/// <summary>
/// Per-band clamp to <c>[Min, Max]</c> range. Mirrors libvips
/// <c>vips_clamp</c>. UChar and Float branches; per-band scalar
/// limits applied uniformly across pixels.
///
/// <para>The natural pairing with arithmetic ops that may overshoot
/// (Linear with negative slopes, math ops, sums of many UChar
/// images cast to Float). Output preserves the input format and
/// dimensions; out-of-range values clamp toward the limit, never
/// wrap.</para>
/// </summary>
public class VipsClamp : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public double Min { get; set; } = 0;
    public double Max { get; set; } = 255;

    public override int Build()
    {
        if (In == null) return -1;
        if (Min > Max) return -1;

        Out = new VipsImage
        {
            Width = In.Width, Height = In.Height,
            Bands = In.Bands, BandFormat = In.BandFormat,
            Interpretation = In.Interpretation,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In, ClientB = (Min, Max),
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Clamp", RuntimeHelpers.GetHashCode(In), Min, Max);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inReg = (VipsRegion)seq!;
        var (min, max) = ((double, double))b!;
        VipsRect r = outRegion.Valid;
        if (inReg.Prepare(r) != 0) return -1;

        VipsImage @in = inReg.Image;
        bool isFloat = @in.BandFormat == VipsBandFormat.Float;
        int totalSamples = r.Width * @in.Bands;

        for (int y = 0; y < r.Height; y++)
        {
            var ia = inReg.GetAddress(r.Left, r.Top + y);
            var oa = outRegion.GetAddress(r.Left, r.Top + y);
            if (isFloat)
            {
                for (int s = 0; s < totalSamples; s++)
                {
                    float v = BinaryPrimitives.ReadSingleLittleEndian(ia.Slice(s * 4, 4));
                    BinaryPrimitives.WriteSingleLittleEndian(oa.Slice(s * 4, 4),
                        (float)Math.Clamp(v, min, max));
                }
            }
            else
            {
                byte loByte = (byte)Math.Clamp(min, 0, 255);
                byte hiByte = (byte)Math.Clamp(max, 0, 255);
                for (int s = 0; s < totalSamples; s++)
                {
                    byte v = ia[s];
                    if (v < loByte) v = loByte;
                    else if (v > hiByte) v = hiByte;
                    oa[s] = v;
                }
            }
        }
        return 0;
    }
}
