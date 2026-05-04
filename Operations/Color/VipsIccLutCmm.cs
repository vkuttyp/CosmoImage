using System;
using CosmoImage.Operations.Metadata;

namespace CosmoImage.Operations.Color;

/// <summary>
/// Pure-managed ICC color management for LUT-based profiles
/// (lut16Type / "mft2"). Handles the legacy ICC v2 LUT pipeline:
/// per-channel input curves → 3×3 matrix → 3D CLUT (trilinear
/// interpolation) → per-channel output curves.
///
/// <para>The transform composes two mft2 profiles via the profile
/// connection space (PCS): source's <c>A2B0</c> takes device → PCS,
/// destination's <c>B2A0</c> takes PCS → device. Caller is
/// responsible for ensuring both profiles use the same PCS (XYZ
/// or Lab); this class doesn't insert XYZ↔Lab conversion.</para>
///
/// <para>Slower than <see cref="VipsIccCmm"/>'s precomputed
/// Matrix/TRC LUTs (the per-pixel pipeline does ~16 LUT lookups
/// + an 8-corner trilinear interp), but covers profile types the
/// fast path can't model: printer profiles, scanner profiles,
/// most ICC v2 device profiles.</para>
///
/// <para>Modern v4 mAB / mBA tags ("multiProcessElementsType") are
/// out of scope for this round and fall back to Magick.</para>
/// </summary>
public sealed class VipsIccLutCmm
{
    private readonly IccMft2 _src;
    private readonly IccMft2 _dst;

    private VipsIccLutCmm(IccMft2 src, IccMft2 dst)
    {
        _src = src;
        _dst = dst;
    }

    /// <summary>
    /// Build a LUT-based transform from <paramref name="src"/> to
    /// <paramref name="dst"/>. Reads the source's <c>A2B0</c> and
    /// destination's <c>B2A0</c> mft2 tags; returns <c>null</c> if
    /// either is missing or the profile uses a different LUT form
    /// (mAB/mBA or mft1).
    /// </summary>
    public static VipsIccLutCmm? TryBuild(VipsIccProfile src, VipsIccProfile dst)
    {
        if (src == null || dst == null) return null;
        var a2b = src.GetTagMft2("A2B0");
        var b2a = dst.GetTagMft2("B2A0");
        if (a2b == null || b2a == null) return null;
        // We only handle 3-channel device spaces here (RGB / Lab / XYZ);
        // CMYK (4-channel) needs n-linear interpolation which is a
        // separate exercise.
        if (a2b.InputChannels != 3 || a2b.OutputChannels != 3) return null;
        if (b2a.InputChannels != 3 || b2a.OutputChannels != 3) return null;
        return new VipsIccLutCmm(a2b, b2a);
    }

    /// <summary>
    /// Apply the transform to <paramref name="count"/> pixels. Pixels
    /// are tightly packed RGB (<paramref name="bands"/> = 3) or RGBA
    /// (4); for RGBA the alpha channel passes through unchanged.
    /// </summary>
    public void Apply(byte[] src, int srcOff, byte[] dst, int dstOff, int count, int bands)
    {
        if (bands != 3 && bands != 4)
            throw new ArgumentException("VipsIccLutCmm requires 3- or 4-band data", nameof(bands));

        var inBuf = new double[3];
        var pcs = new double[3];
        var outBuf = new double[3];

        for (int i = 0; i < count; i++)
        {
            int sp = srcOff + i * bands;
            int dp = dstOff + i * bands;
            inBuf[0] = src[sp]     / 255.0;
            inBuf[1] = src[sp + 1] / 255.0;
            inBuf[2] = src[sp + 2] / 255.0;

            ApplyMft2(_src, inBuf, pcs);
            ApplyMft2(_dst, pcs, outBuf);

            dst[dp]     = ToByte(outBuf[0]);
            dst[dp + 1] = ToByte(outBuf[1]);
            dst[dp + 2] = ToByte(outBuf[2]);
            if (bands == 4) dst[dp + 3] = src[sp + 3];
        }
    }

    private static byte ToByte(double v)
    {
        if (v <= 0) return 0;
        if (v >= 1) return 255;
        return (byte)(v * 255.0 + 0.5);
    }

    /// <summary>
    /// Apply the mft2 pipeline: input curves → matrix → 3D CLUT
    /// (trilinear) → output curves. <paramref name="input"/> and
    /// <paramref name="output"/> are 3-channel normalized [0, 1]
    /// values.
    /// </summary>
    private static void ApplyMft2(IccMft2 m, double[] input, double[] output)
    {
        Span<double> v = stackalloc double[3];

        // Input curves.
        v[0] = LookupCurve(m.InputTables[0], input[0]);
        v[1] = LookupCurve(m.InputTables[1], input[1]);
        v[2] = LookupCurve(m.InputTables[2], input[2]);

        // 3×3 matrix (identity for most non-XYZ profiles, but apply
        // unconditionally — the matrix in non-XYZ inputs IS identity
        // per the encoder, so the multiply is a no-op there).
        Span<double> mv = stackalloc double[3];
        for (int i = 0; i < 3; i++)
            mv[i] = m.Matrix[i, 0] * v[0] + m.Matrix[i, 1] * v[1] + m.Matrix[i, 2] * v[2];
        v[0] = mv[0]; v[1] = mv[1]; v[2] = mv[2];

        // 3D CLUT trilinear interpolation.
        Span<double> clutOut = stackalloc double[3];
        TrilinearLookup(m, v, clutOut);

        // Output curves.
        output[0] = LookupCurve(m.OutputTables[0], clutOut[0]);
        output[1] = LookupCurve(m.OutputTables[1], clutOut[1]);
        output[2] = LookupCurve(m.OutputTables[2], clutOut[2]);
    }

    private static double LookupCurve(ushort[] table, double x)
    {
        if (x <= 0) return table[0] / 65535.0;
        if (x >= 1) return table[^1] / 65535.0;
        double pos = x * (table.Length - 1);
        int i = (int)pos;
        if (i >= table.Length - 1) return table[^1] / 65535.0;
        double frac = pos - i;
        double a = table[i] / 65535.0;
        double b = table[i + 1] / 65535.0;
        return a + (b - a) * frac;
    }

    /// <summary>
    /// 3D trilinear interpolation through a regular grid. The CLUT is
    /// laid out so that the input axes vary in row-major order
    /// (channel 0 is the slowest, channel 2 is the fastest).
    /// </summary>
    private static void TrilinearLookup(IccMft2 m, ReadOnlySpan<double> input, Span<double> output)
    {
        int g = m.GridSize;
        int oc = m.OutputChannels;
        var clut = m.Clut;

        double xa = Math.Clamp(input[0], 0, 1) * (g - 1);
        double xb = Math.Clamp(input[1], 0, 1) * (g - 1);
        double xc = Math.Clamp(input[2], 0, 1) * (g - 1);
        int ia0 = (int)xa, ib0 = (int)xb, ic0 = (int)xc;
        int ia1 = Math.Min(ia0 + 1, g - 1);
        int ib1 = Math.Min(ib0 + 1, g - 1);
        int ic1 = Math.Min(ic0 + 1, g - 1);
        double fa = xa - ia0, fb = xb - ib0, fc = xc - ic0;

        int Idx(int aa, int bb, int cc, int ch) => ((aa * g + bb) * g + cc) * oc + ch;

        for (int ch = 0; ch < oc && ch < output.Length; ch++)
        {
            double v000 = clut[Idx(ia0, ib0, ic0, ch)] / 65535.0;
            double v001 = clut[Idx(ia0, ib0, ic1, ch)] / 65535.0;
            double v010 = clut[Idx(ia0, ib1, ic0, ch)] / 65535.0;
            double v011 = clut[Idx(ia0, ib1, ic1, ch)] / 65535.0;
            double v100 = clut[Idx(ia1, ib0, ic0, ch)] / 65535.0;
            double v101 = clut[Idx(ia1, ib0, ic1, ch)] / 65535.0;
            double v110 = clut[Idx(ia1, ib1, ic0, ch)] / 65535.0;
            double v111 = clut[Idx(ia1, ib1, ic1, ch)] / 65535.0;

            // Interpolate along axis c first, then b, then a.
            double v00 = v000 * (1 - fc) + v001 * fc;
            double v01 = v010 * (1 - fc) + v011 * fc;
            double v10 = v100 * (1 - fc) + v101 * fc;
            double v11 = v110 * (1 - fc) + v111 * fc;

            double v0 = v00 * (1 - fb) + v01 * fb;
            double v1 = v10 * (1 - fb) + v11 * fb;

            output[ch] = v0 * (1 - fa) + v1 * fa;
        }
    }
}
