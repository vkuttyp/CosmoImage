using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Color;

/// <summary>
/// Per-pixel CIE76 colour-difference between two Lab images. Just the
/// Euclidean distance in Lab space:
/// <code>ΔE76 = √((L₁ − L₂)² + (a₁ − a₂)² + (b₁ − b₂)²)</code>
/// Mirrors libvips <c>vips_dE76</c>. Both inputs must be Float Lab
/// 3-band; output is single-band Float.
///
/// <para>CIE76 is the simplest perceptual-difference metric and is
/// roughly proportional to "just-noticeable" differences for small
/// gaps. Use <see cref="VipsdE2000"/> if you need accuracy on
/// saturated greens/blues — CIE76 systematically over-estimates
/// difference there.</para>
/// </summary>
public class VipsdE76 : VipsOperation
{
    public VipsImage? Left { get; set; }
    public VipsImage? Right { get; set; }
    public VipsImage? Out { get; set; }

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
            ClientA = new[] { Left, Right },
        };
        Out.CopyMetadataFrom(Left);
        Out.SetPipeline(VipsDemandStyle.Any, Left, Right);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("dE76", RuntimeHelpers.GetHashCode(Left), RuntimeHelpers.GetHashCode(Right));

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var regions = (VipsRegion[])seq!;
        var l = regions[0]; var r2 = regions[1];
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
                double dL = BinaryPrimitives.ReadSingleLittleEndian(la.Slice(o + 0, 4))
                          - BinaryPrimitives.ReadSingleLittleEndian(ra.Slice(o + 0, 4));
                double da = BinaryPrimitives.ReadSingleLittleEndian(la.Slice(o + 4, 4))
                          - BinaryPrimitives.ReadSingleLittleEndian(ra.Slice(o + 4, 4));
                double db = BinaryPrimitives.ReadSingleLittleEndian(la.Slice(o + 8, 4))
                          - BinaryPrimitives.ReadSingleLittleEndian(ra.Slice(o + 8, 4));
                double dE = Math.Sqrt(dL * dL + da * da + db * db);
                BinaryPrimitives.WriteSingleLittleEndian(oa.Slice(x * 4, 4), (float)dE);
            }
        }
        return 0;
    }
}

/// <summary>
/// CIEDE2000 colour-difference (Sharma, Wu, Dalal 2005). Per-pixel
/// distance between two Lab images, weighted to better match human
/// perception of saturated colours and small differences than the
/// plain CIE76 Euclidean. Mirrors libvips <c>vips_dE00</c>.
///
/// <para>Both inputs must be Float Lab 3-band; output is Float
/// single-band. The math is mostly trig and a handful of weighted
/// terms — see <see cref="ComputeDE2000"/> for the full reference
/// implementation, which is the form Sharma published as a sanity
/// check for the Bruce Lindbloom test cases.</para>
/// </summary>
public class VipsdE2000 : VipsOperation
{
    public VipsImage? Left { get; set; }
    public VipsImage? Right { get; set; }
    public VipsImage? Out { get; set; }

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
            ClientA = new[] { Left, Right },
        };
        Out.CopyMetadataFrom(Left);
        Out.SetPipeline(VipsDemandStyle.Any, Left, Right);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("dE2000", RuntimeHelpers.GetHashCode(Left), RuntimeHelpers.GetHashCode(Right));

    /// <summary>
    /// CIEDE2000 ΔE between two Lab triplets. Public so callers can
    /// invoke it on individual colours without spinning up an op
    /// pipeline.
    /// </summary>
    public static double ComputeDE2000(double L1, double a1, double b1, double L2, double a2, double b2)
    {
        const double kL = 1, kC = 1, kH = 1;
        double C1 = Math.Sqrt(a1 * a1 + b1 * b1);
        double C2 = Math.Sqrt(a2 * a2 + b2 * b2);
        double Cbar = (C1 + C2) / 2;
        double C7 = Math.Pow(Cbar, 7);
        double G = 0.5 * (1 - Math.Sqrt(C7 / (C7 + Math.Pow(25, 7))));
        double a1p = (1 + G) * a1;
        double a2p = (1 + G) * a2;
        double C1p = Math.Sqrt(a1p * a1p + b1 * b1);
        double C2p = Math.Sqrt(a2p * a2p + b2 * b2);
        double h1p = AtanDeg(b1, a1p);
        double h2p = AtanDeg(b2, a2p);

        double dLp = L2 - L1;
        double dCp = C2p - C1p;
        double dhp;
        if (C1p * C2p == 0) dhp = 0;
        else
        {
            double diff = h2p - h1p;
            if (diff > 180) diff -= 360;
            else if (diff < -180) diff += 360;
            dhp = diff;
        }
        double dHp = 2 * Math.Sqrt(C1p * C2p) * Math.Sin(dhp * Math.PI / 360);

        double Lbar = (L1 + L2) / 2;
        double Cbarp = (C1p + C2p) / 2;
        double hbarp;
        if (C1p * C2p == 0) hbarp = h1p + h2p;
        else
        {
            double sum = h1p + h2p;
            double diff = Math.Abs(h1p - h2p);
            if (diff <= 180) hbarp = sum / 2;
            else hbarp = (sum + (sum < 360 ? 360 : -360)) / 2;
        }

        double T = 1 - 0.17 * Math.Cos((hbarp - 30) * Math.PI / 180)
                     + 0.24 * Math.Cos(2 * hbarp * Math.PI / 180)
                     + 0.32 * Math.Cos((3 * hbarp + 6) * Math.PI / 180)
                     - 0.20 * Math.Cos((4 * hbarp - 63) * Math.PI / 180);
        double dTheta = 30 * Math.Exp(-Math.Pow((hbarp - 275) / 25, 2));
        double Cbarp7 = Math.Pow(Cbarp, 7);
        double Rc = 2 * Math.Sqrt(Cbarp7 / (Cbarp7 + Math.Pow(25, 7)));
        double Lbar50 = Lbar - 50;
        double Sl = 1 + (0.015 * Lbar50 * Lbar50) / Math.Sqrt(20 + Lbar50 * Lbar50);
        double Sc = 1 + 0.045 * Cbarp;
        double Sh = 1 + 0.015 * Cbarp * T;
        double Rt = -Math.Sin(2 * dTheta * Math.PI / 180) * Rc;

        double termL = dLp / (kL * Sl);
        double termC = dCp / (kC * Sc);
        double termH = dHp / (kH * Sh);
        return Math.Sqrt(termL * termL + termC * termC + termH * termH
            + Rt * termC * termH);
    }

    private static double AtanDeg(double y, double x)
    {
        if (y == 0 && x == 0) return 0;
        double d = Math.Atan2(y, x) * 180 / Math.PI;
        return d < 0 ? d + 360 : d;
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var regions = (VipsRegion[])seq!;
        var l = regions[0]; var r2 = regions[1];
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
                double dE = ComputeDE2000(L1, a1, b1, L2, a2, b2);
                BinaryPrimitives.WriteSingleLittleEndian(oa.Slice(x * 4, 4), (float)dE);
            }
        }
        return 0;
    }
}
