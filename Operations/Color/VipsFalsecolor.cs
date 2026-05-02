using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Color;

/// <summary>
/// Map a single-band UChar image through a 256-entry RGB lookup table
/// to produce an RGB false-colour visualisation. libvips' <c>falsecolour</c>
/// uses a fixed PET-scan-style ramp (cool blues for low values through
/// hot reds for high). We use the classic "jet" / "rainbow" colour map
/// — visually similar, widely recognised, and cheap to compute.
///
/// <para>The intended use is *visualisation only* — false-colour ramps
/// are notoriously misleading for quantitative comparison (see Borland
/// &amp; Taylor 2007). But for "where in this depth map are the high
/// values?", a rainbow LUT remains useful.</para>
/// </summary>
public class VipsFalsecolor : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }

    public override int Build()
    {
        if (In == null) return -1;
        if (In.BandFormat != VipsBandFormat.UChar) return -1;
        if (In.Bands != 1) return -1;

        Out = new VipsImage
        {
            Width = In.Width, Height = In.Height, Bands = 3,
            BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In, ClientB = JetLut,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Falsecolor", RuntimeHelpers.GetHashCode(In));

    // Classic "jet" colour map. Lazy-built once on first access.
    private static readonly byte[] JetLut = BuildJetLut();

    private static byte[] BuildJetLut()
    {
        var lut = new byte[256 * 3];
        for (int i = 0; i < 256; i++)
        {
            // Standard jet — five anchor colours on [0,1]:
            //   0.0 → (0,0,128)   0.125 → (0,0,255)   0.375 → (0,255,255)
            //   0.625 → (255,255,0)   0.875 → (255,0,0)   1.0 → (128,0,0)
            // These are derived from clipped, shifted sinusoidal lobes.
            double v = i / 255.0;
            double r = Math.Clamp(1.5 - Math.Abs(4 * v - 3), 0, 1);
            double g = Math.Clamp(1.5 - Math.Abs(4 * v - 2), 0, 1);
            double bl = Math.Clamp(1.5 - Math.Abs(4 * v - 1), 0, 1);
            lut[i * 3 + 0] = (byte)(r * 255);
            lut[i * 3 + 1] = (byte)(g * 255);
            lut[i * 3 + 2] = (byte)(bl * 255);
        }
        return lut;
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        var lut = (byte[])b!;
        VipsRect r = outRegion.Valid;

        if (inRegion.Prepare(r) != 0) return -1;

        for (int y = 0; y < r.Height; y++)
        {
            var inAddr = inRegion.GetAddress(r.Left, r.Top + y);
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                int li = inAddr[x] * 3;
                outAddr[x * 3 + 0] = lut[li + 0];
                outAddr[x * 3 + 1] = lut[li + 1];
                outAddr[x * 3 + 2] = lut[li + 2];
            }
        }
        return 0;
    }
}
