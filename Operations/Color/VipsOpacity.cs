using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Color;

/// <summary>
/// Multiply the alpha channel of an alpha-bearing image by
/// <see cref="Amount"/> (0..1). Mirrors ImageSharp's
/// <c>Opacity(amount)</c> processor — the standard "fade this layer
/// to N%" knob in any image-editing pipeline.
///
/// <para>Operates on RGBA (4-band) or LA (2-band) inputs; non-alpha
/// images are passed through unchanged. UChar and Float branches.</para>
/// </summary>
public class VipsOpacity : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    /// <summary>Alpha multiplier, 0..1. 0 = fully transparent, 1 = unchanged.</summary>
    public double Amount { get; set; } = 1.0;

    public override int Build()
    {
        if (In == null) return -1;
        if (Amount < 0 || Amount > 1) return -1;
        // Pass through if no alpha channel.
        if (In.Bands != 2 && In.Bands != 4) { Out = In; return 0; }

        Out = new VipsImage
        {
            Width = In.Width, Height = In.Height,
            Bands = In.Bands, BandFormat = In.BandFormat,
            Interpretation = In.Interpretation,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In, ClientB = Amount,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Opacity", RuntimeHelpers.GetHashCode(In), Amount);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inReg = (VipsRegion)seq!;
        var amount = (double)b!;
        VipsRect r = outRegion.Valid;
        if (inReg.Prepare(r) != 0) return -1;

        VipsImage @in = inReg.Image;
        int bands = @in.Bands;
        int alphaIdx = bands - 1; // last band is alpha for RGBA / LA
        bool isFloat = @in.BandFormat == VipsBandFormat.Float;
        int sampleSize = isFloat ? 4 : 1;
        int pelBytes = bands * sampleSize;

        for (int y = 0; y < r.Height; y++)
        {
            var ia = inReg.GetAddress(r.Left, r.Top + y);
            var oa = outRegion.GetAddress(r.Left, r.Top + y);
            // Copy non-alpha bytes verbatim.
            ia.Slice(0, r.Width * pelBytes).CopyTo(oa);
            // Scale just the alpha band per pixel.
            for (int x = 0; x < r.Width; x++)
            {
                int aOff = x * pelBytes + alphaIdx * sampleSize;
                if (isFloat)
                {
                    float aVal = BinaryPrimitives.ReadSingleLittleEndian(oa.Slice(aOff, 4));
                    BinaryPrimitives.WriteSingleLittleEndian(oa.Slice(aOff, 4), (float)(aVal * amount));
                }
                else
                {
                    oa[aOff] = (byte)Math.Clamp(Math.Round(oa[aOff] * amount), 0, 255);
                }
            }
        }
        return 0;
    }
}
