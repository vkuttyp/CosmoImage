using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Color;

/// <summary>
/// sRGB UChar → HSV UChar. Cylindrical reparametrisation of RGB.
/// Mirrors libvips <c>vips_sRGB2HSV</c>.
///
/// <para>libvips packs HSV into UChar per band, with H ∈ [0, 255]
/// representing 0–360° (each step ≈ 1.41°), S ∈ [0, 255] for
/// saturation, V ∈ [0, 255] for value. We follow that convention so
/// pipelines compose cleanly.</para>
///
/// <para>HSV is convenient for hue rotation (<c>H += 30</c>), masking
/// by colour family (<c>H ∈ [40, 80]</c> ≈ greens), and quick
/// prototyping. It is *not* perceptually uniform — for that, use
/// <see cref="VipsXYZ2OkLab"/> + <see cref="VipsOkLab2OkLCh"/>.</para>
/// </summary>
public class VipsSRGB2HSV : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }

    public override int Build()
    {
        if (In == null) return -1;
        if (In.BandFormat != VipsBandFormat.UChar || In.Bands != 3) return -1;

        Out = new VipsImage
        {
            Width = In.Width, Height = In.Height, Bands = 3, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.HSV,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("sRGB2HSV", RuntimeHelpers.GetHashCode(In));

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
                int o = x * 3;
                int R = inAddr[o + 0], G = inAddr[o + 1], B = inAddr[o + 2];
                int max = Math.Max(R, Math.Max(G, B));
                int min = Math.Min(R, Math.Min(G, B));
                int delta = max - min;

                int V = max;
                int S = max == 0 ? 0 : (255 * delta) / max;

                double Hf;
                if (delta == 0) Hf = 0;
                else if (max == R) Hf = ((G - B) * 1.0 / delta) % 6;
                else if (max == G) Hf = ((B - R) * 1.0 / delta) + 2;
                else Hf = ((R - G) * 1.0 / delta) + 4;
                if (Hf < 0) Hf += 6;
                // 0..6 → 0..255 (so 256 maps to 0 wraps).
                int H = (int)Math.Round(Hf * 256.0 / 6.0) & 0xFF;

                outAddr[o + 0] = (byte)H;
                outAddr[o + 1] = (byte)S;
                outAddr[o + 2] = (byte)V;
            }
        }
        return 0;
    }
}

/// <summary>
/// HSV UChar → sRGB UChar. Mirrors libvips <c>vips_HSV2sRGB</c>.
/// Inverse of <see cref="VipsSRGB2HSV"/>.
/// </summary>
public class VipsHSV2sRGB : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }

    public override int Build()
    {
        if (In == null) return -1;
        if (In.BandFormat != VipsBandFormat.UChar || In.Bands != 3) return -1;

        Out = new VipsImage
        {
            Width = In.Width, Height = In.Height, Bands = 3, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.SRGB,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("HSV2sRGB", RuntimeHelpers.GetHashCode(In));

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
                int o = x * 3;
                int H = inAddr[o + 0], S = inAddr[o + 1], V = inAddr[o + 2];
                if (S == 0)
                {
                    outAddr[o + 0] = (byte)V;
                    outAddr[o + 1] = (byte)V;
                    outAddr[o + 2] = (byte)V;
                    continue;
                }
                double Hf = H * 6.0 / 256.0;
                int sector = (int)Math.Floor(Hf);
                double frac = Hf - sector;
                double s = S / 255.0;
                double v = V / 255.0;
                double p = v * (1 - s);
                double q = v * (1 - s * frac);
                double t = v * (1 - s * (1 - frac));
                double R, G, B;
                switch (sector % 6)
                {
                    case 0: R = v; G = t; B = p; break;
                    case 1: R = q; G = v; B = p; break;
                    case 2: R = p; G = v; B = t; break;
                    case 3: R = p; G = q; B = v; break;
                    case 4: R = t; G = p; B = v; break;
                    default: R = v; G = p; B = q; break;
                }
                outAddr[o + 0] = (byte)Math.Clamp(Math.Round(R * 255), 0, 255);
                outAddr[o + 1] = (byte)Math.Clamp(Math.Round(G * 255), 0, 255);
                outAddr[o + 2] = (byte)Math.Clamp(Math.Round(B * 255), 0, 255);
            }
        }
        return 0;
    }
}
