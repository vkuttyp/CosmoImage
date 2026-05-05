using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Color;

/// <summary>
/// Colour-primary identifier per CICP (ITU-T H.273 §8.1).
/// Only the values relevant to a still-image HDR pipeline are listed.
/// </summary>
public enum VipsCicpPrimaries
{
    /// <summary>Rec. ITU-R BT.709 — standard SDR / sRGB.</summary>
    BT709 = 1,
    /// <summary>Rec. ITU-R BT.2020 — UHD wide-gamut.</summary>
    BT2020 = 9,
}

/// <summary>
/// Transfer-characteristic identifier per CICP (ITU-T H.273 §8.2).
/// The linearisation curve applied to the encoded sample.
/// </summary>
public enum VipsCicpTransfer
{
    /// <summary>BT.709 OETF (1.099·L^0.45 − 0.099 above 0.018, linear below).</summary>
    BT709 = 1,
    /// <summary>Identity — input is already linear.</summary>
    Linear = 8,
    /// <summary>BT.2020 10-bit OETF (same as BT.709, different breakpoint).</summary>
    BT2020 = 14,
    /// <summary>SMPTE ST 2084 (BT.2100) PQ — absolute 0..10000 cd/m².</summary>
    PQ = 16,
    /// <summary>Hybrid Log-Gamma (BT.2100) — scene-referred OETF inverse.</summary>
    HLG = 18,
}

/// <summary>
/// Decode a CICP-tagged HDR / SDR image to <c>scRGB</c> (linear-light,
/// sRGB primaries, Float 3-band, unbounded). Mirrors libvips
/// <c>vips_CMYK2XYZ</c> structure.
///
/// <para>Two-step decode:</para>
/// <list type="number">
///   <item>Apply the inverse transfer function (PQ / HLG / BT.709 /
///         Linear) to each colour sample, normalised to <c>[0, 1]</c>.</item>
///   <item>Multiply the resulting linear-RGB triple by the
///         primaries-conversion matrix to land in sRGB primaries.</item>
/// </list>
///
/// <para>Output values are scene-referred linear in scRGB. For PQ the
/// EOTF nominally returns absolute display luminance in cd/m²; we
/// divide by 100 so SDR diffuse-white aligns with <c>scRGB ≈ 1.0</c>
/// and peak HDR (10000 cd/m²) lands at <c>scRGB ≈ 100.0</c>. HLG
/// inverse-OETF returns scene-referred values in <c>[0, 12]</c>
/// directly — no extra scaling.</para>
///
/// <para>Input must be UChar 3-band (8-bit). 16-bit (UShort) is a
/// straightforward follow-up; we currently handle the most common case.</para>
/// </summary>
public class VipsCicp2scRGB : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public VipsCicpPrimaries Primaries { get; set; } = VipsCicpPrimaries.BT2020;
    public VipsCicpTransfer Transfer { get; set; } = VipsCicpTransfer.PQ;

    public override int Build()
    {
        if (In == null) return -1;
        if (In.Bands != 3) return -1;
        if (In.BandFormat != VipsBandFormat.UChar && In.BandFormat != VipsBandFormat.UShort) return -1;

        Out = new VipsImage
        {
            Width = In.Width, Height = In.Height,
            Bands = 3, BandFormat = VipsBandFormat.Float,
            Interpretation = VipsInterpretation.scRGB,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In, ClientB = (Primaries, Transfer),
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Cicp2scRGB", RuntimeHelpers.GetHashCode(In), Primaries, Transfer);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        var (primaries, transfer) = ((VipsCicpPrimaries, VipsCicpTransfer))b!;
        VipsImage @in = inRegion.Image;
        VipsRect r = outRegion.Valid;
        if (inRegion.Prepare(r) != 0) return -1;

        bool isShort = @in.BandFormat == VipsBandFormat.UShort;
        double normDiv = isShort ? 65535.0 : 255.0;

        for (int y = 0; y < r.Height; y++)
        {
            var inAddr = inRegion.GetAddress(r.Left, r.Top + y);
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                double r0, g0, b0;
                if (isShort)
                {
                    int o = x * 6;
                    r0 = BinaryPrimitives.ReadUInt16LittleEndian(inAddr.Slice(o + 0, 2)) / normDiv;
                    g0 = BinaryPrimitives.ReadUInt16LittleEndian(inAddr.Slice(o + 2, 2)) / normDiv;
                    b0 = BinaryPrimitives.ReadUInt16LittleEndian(inAddr.Slice(o + 4, 2)) / normDiv;
                }
                else
                {
                    int o = x * 3;
                    r0 = inAddr[o + 0] / normDiv;
                    g0 = inAddr[o + 1] / normDiv;
                    b0 = inAddr[o + 2] / normDiv;
                }

                double rl = InverseTransfer(r0, transfer);
                double gl = InverseTransfer(g0, transfer);
                double bl = InverseTransfer(b0, transfer);

                ApplyPrimariesMatrix(primaries, rl, gl, bl, out double rs, out double gs, out double bs);

                int oo = x * 12;
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(oo + 0, 4), (float)rs);
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(oo + 4, 4), (float)gs);
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(oo + 8, 4), (float)bs);
            }
        }
        return 0;
    }

    // BT.2100 HLG inverse-OETF constants. C is computed from A; we hold
    // it as a static readonly because Math.Log isn't a compile-time const.
    private const double HlgA = 0.17883277;
    private const double HlgB = 1.0 - 4.0 * HlgA;
    private static readonly double HlgC = 0.5 - HlgA * Math.Log(4.0 * HlgA);

    /// <summary>Inverse OETF / EOTF lookup. Input is the encoded sample in [0, 1].</summary>
    public static double InverseTransfer(double e, VipsCicpTransfer transfer)
    {
        switch (transfer)
        {
            case VipsCicpTransfer.Linear:
                return e;

            case VipsCicpTransfer.BT709:
            case VipsCicpTransfer.BT2020:
                // Inverse of BT.709 OETF. The forward curve has a small
                // linear segment near zero; inverse mirrors it.
                return e < 0.081 ? e / 4.5 : Math.Pow((e + 0.099) / 1.099, 1.0 / 0.45);

            case VipsCicpTransfer.PQ:
                {
                    // SMPTE ST 2084. Maps [0, 1] code → [0, 10000] cd/m².
                    // Divide by 100 so SDR diffuse white (100 cd/m²) ≈ 1.0
                    // in scRGB — the conventional alignment for HDR
                    // pipelines that need to interoperate with SDR content.
                    const double m1 = 2610.0 / 16384.0;
                    const double m2 = (2523.0 / 4096.0) * 128.0;
                    const double c1 = 3424.0 / 4096.0;
                    const double c2 = (2413.0 / 4096.0) * 32.0;
                    const double c3 = (2392.0 / 4096.0) * 32.0;
                    double ePow = Math.Pow(Math.Max(e, 0), 1.0 / m2);
                    double num = Math.Max(ePow - c1, 0);
                    double den = c2 - c3 * ePow;
                    return Math.Pow(num / den, 1.0 / m1) * 10000.0 / 100.0;
                }

            case VipsCicpTransfer.HLG:
                {
                    // Inverse OETF (scene-referred) per BT.2100. Maps
                    // [0, 1] code → [0, 12] scene linear.
                    if (e <= 0.5) return (e * e) / 3.0;
                    return (Math.Exp((e - HlgC) / HlgA) + HlgB) / 12.0;
                }

            default:
                return e;
        }
    }

    /// <summary>
    /// Convert linear RGB in <paramref name="primaries"/> to scRGB
    /// (sRGB primaries) via the published BT.2087 / BT.2020 matrices.
    /// Both are D65 so the matrix is a pure primary rotation, no
    /// chromatic adaptation needed.
    /// </summary>
    public static void ApplyPrimariesMatrix(VipsCicpPrimaries primaries,
        double r, double g, double b, out double rs, out double gs, out double bs)
    {
        switch (primaries)
        {
            case VipsCicpPrimaries.BT709:
                // BT.709 = sRGB primaries — identity.
                rs = r; gs = g; bs = b;
                return;

            case VipsCicpPrimaries.BT2020:
                // BT.2020 → BT.709 (sRGB primaries), BT.2087 published values.
                rs =  1.6605 * r - 0.5876 * g - 0.0728 * b;
                gs = -0.1246 * r + 1.1329 * g - 0.0083 * b;
                bs = -0.0182 * r - 0.1006 * g + 1.1187 * b;
                return;

            default:
                rs = r; gs = g; bs = b;
                return;
        }
    }
}
