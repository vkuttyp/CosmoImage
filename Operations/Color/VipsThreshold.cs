using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Color;

/// <summary>
/// Binary threshold: per-band, output is 255 where input ≥
/// <see cref="Value"/>, 0 otherwise. Mirrors ImageSharp's
/// <c>Threshold(amount)</c> processor and libvips'
/// <c>vips_relational_const(MoreEq)</c>.
///
/// <para>The basic step toward binarisation, edge masks, and
/// alpha-from-luminance pipelines. UChar in / UChar out. For Float
/// inputs, value is interpreted in the same numeric range as the
/// input.</para>
/// </summary>
public class VipsThreshold : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public double Value { get; set; } = 128;

    public override int Build()
    {
        if (In == null) return -1;

        Out = new VipsImage
        {
            Width = In.Width, Height = In.Height,
            Bands = In.Bands, BandFormat = VipsBandFormat.UChar,
            Interpretation = In.Interpretation,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In, ClientB = Value,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Threshold", RuntimeHelpers.GetHashCode(In), Value);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inReg = (VipsRegion)seq!;
        var value = (double)b!;
        VipsRect r = outRegion.Valid;
        if (inReg.Prepare(r) != 0) return -1;

        VipsImage @in = inReg.Image;
        bool isFloat = @in.BandFormat == VipsBandFormat.Float;
        int bands = @in.Bands;
        int totalSamples = r.Width * bands;

        for (int y = 0; y < r.Height; y++)
        {
            var ia = inReg.GetAddress(r.Left, r.Top + y);
            var oa = outRegion.GetAddress(r.Left, r.Top + y);
            if (isFloat)
            {
                for (int s = 0; s < totalSamples; s++)
                {
                    float v = BinaryPrimitives.ReadSingleLittleEndian(ia.Slice(s * 4, 4));
                    oa[s] = (byte)(v >= value ? 255 : 0);
                }
            }
            else
            {
                byte vByte = (byte)Math.Clamp(value, 0, 255);
                for (int s = 0; s < totalSamples; s++)
                    oa[s] = ia[s] >= vByte ? (byte)255 : (byte)0;
            }
        }
        return 0;
    }
}
