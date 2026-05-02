using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Color;

/// <summary>
/// Per-pixel CMC(l:c) colour-difference between two Lab images.
/// Devised by the UK Society of Dyers and Colourists (1984) and
/// designed for the textile industry's "is this dye-lot acceptable?"
/// pass/fail checks. Mirrors libvips <c>vips_dECMC</c>. Float Lab
/// 3-band on both sides; output is single-band Float.
///
/// <para>The metric is asymmetric — the weighting functions are
/// computed from the *reference* (Left) image, so swapping inputs
/// changes the result. Default weights are <c>l = 2, c = 1</c>
/// (acceptability mode); use <c>l = 1, c = 1</c> for perceptibility.</para>
/// </summary>
public class VipsdECMC : VipsOperation
{
    public VipsImage? Left { get; set; }
    public VipsImage? Right { get; set; }
    public VipsImage? Out { get; set; }
    /// <summary>Lightness weight. Default 2 (acceptability).</summary>
    public double L { get; set; } = 2;
    /// <summary>Chroma weight. Default 1.</summary>
    public double C { get; set; } = 1;

    public override int Build()
    {
        if (Left == null || Right == null) return -1;
        if (Left.BandFormat != VipsBandFormat.Float || Right.BandFormat != VipsBandFormat.Float) return -1;
        if (Left.Bands != 3 || Right.Bands != 3) return -1;
        if (Left.Width != Right.Width || Left.Height != Right.Height) return -1;

        Out = new VipsImage
        {
            Width = Left.Width, Height = Left.Height, Bands = 1, BandFormat = VipsBandFormat.Float,
            Interpretation = VipsInterpretation.BW,
            Coding = Left.Coding, XRes = Left.XRes, YRes = Left.YRes,
            StartFn = VipsSeq.StartMany, GenerateFn = Generate, StopFn = VipsSeq.StopMany,
            ClientA = new[] { Left, Right }, ClientB = (L, C),
        };
        Out.CopyMetadataFrom(Left);
        Out.SetPipeline(VipsDemandStyle.Any, Left, Right);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("dECMC", RuntimeHelpers.GetHashCode(Left), RuntimeHelpers.GetHashCode(Right), L, C);

    /// <summary>
    /// CMC(l:c) ΔE between two Lab triplets. Reference is
    /// <c>(L1, a1, b1)</c>; sample is <c>(L2, a2, b2)</c>. Default
    /// weights match libvips: <c>l = 2, c = 1</c>.
    /// </summary>
    public static double ComputeDECMC(double L1, double a1, double b1,
        double L2, double a2, double b2,
        double l = 2, double c = 1)
    {
        double C1 = Math.Sqrt(a1 * a1 + b1 * b1);
        double C2 = Math.Sqrt(a2 * a2 + b2 * b2);
        double dL = L1 - L2;
        double dC = C1 - C2;
        double da = a1 - a2;
        double db = b1 - b2;
        double dE_ab2 = dL * dL + da * da + db * db;
        double dH2 = dE_ab2 - dL * dL - dC * dC;
        if (dH2 < 0) dH2 = 0;

        double SL = L1 < 16 ? 0.511 : (0.040975 * L1) / (1 + 0.01765 * L1);
        double SC = (0.0638 * C1) / (1 + 0.0131 * C1) + 0.638;
        double C1_4 = C1 * C1 * C1 * C1;
        double F = Math.Sqrt(C1_4 / (C1_4 + 1900));
        double h1 = Math.Atan2(b1, a1) * 180 / Math.PI;
        if (h1 < 0) h1 += 360;
        double T = (h1 >= 164 && h1 <= 345)
            ? 0.56 + Math.Abs(0.2 * Math.Cos((h1 + 168) * Math.PI / 180))
            : 0.36 + Math.Abs(0.4 * Math.Cos((h1 + 35) * Math.PI / 180));
        double SH = SC * (F * T + 1 - F);

        double termL = dL / (l * SL);
        double termC = dC / (c * SC);
        double termH2 = dH2 / (SH * SH);
        return Math.Sqrt(termL * termL + termC * termC + termH2);
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var regions = (VipsRegion[])seq!;
        var l = regions[0]; var r2 = regions[1];
        var (lWeight, cWeight) = ((double, double))b!;
        VipsRect r = outRegion.Valid;
        if (l.Prepare(r) != 0) return -1;
        if (r2.Prepare(r) != 0) return -1;

        for (int y = 0; y < r.Height; y++)
        {
            var la = l.GetAddress(r.Left, r.Top + y);
            var ra = r2.GetAddress(r.Left, r.Top + y);
            var oa = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                int o = x * 12;
                double L1 = BinaryPrimitives.ReadSingleLittleEndian(la.Slice(o + 0, 4));
                double a1 = BinaryPrimitives.ReadSingleLittleEndian(la.Slice(o + 4, 4));
                double b1 = BinaryPrimitives.ReadSingleLittleEndian(la.Slice(o + 8, 4));
                double L2 = BinaryPrimitives.ReadSingleLittleEndian(ra.Slice(o + 0, 4));
                double a2 = BinaryPrimitives.ReadSingleLittleEndian(ra.Slice(o + 4, 4));
                double b2 = BinaryPrimitives.ReadSingleLittleEndian(ra.Slice(o + 8, 4));
                double dE = ComputeDECMC(L1, a1, b1, L2, a2, b2, lWeight, cWeight);
                BinaryPrimitives.WriteSingleLittleEndian(oa.Slice(x * 4, 4), (float)dE);
            }
        }
        return 0;
    }
}
