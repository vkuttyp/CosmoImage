using System;
using CosmoImage.Operations.Metadata;

namespace CosmoImage.Operations.Color;

/// <summary>
/// Pure-managed ICC color management for LUT-based profiles.
/// Handles both legacy lut16Type ('mft2') and modern
/// lutAtoBType / lutBtoAType ('mAB ' / 'mBA ') tags via a unified
/// <see cref="LutTransform"/> abstraction. Composes two profiles
/// via the profile connection space (PCS): source's <c>A2B0</c>
/// takes device → PCS, destination's <c>B2A0</c> takes PCS → device.
///
/// <para>Slower than <see cref="VipsIccCmm"/>'s precomputed
/// Matrix/TRC LUTs (per-pixel pipeline does multiple curve lookups
/// + multidim CLUT interpolation + matrix multiplies), but covers
/// printer / scanner profiles and other LUT-based device profiles
/// the fast path can't model.</para>
///
/// <para>Caller is responsible for ensuring both profiles use the
/// same PCS (XYZ or Lab); this class doesn't insert PCS conversion.</para>
/// </summary>
public sealed class VipsIccLutCmm
{
    private readonly LutTransform _forward;
    private readonly LutTransform _reverse;

    private VipsIccLutCmm(LutTransform forward, LutTransform reverse)
    {
        _forward = forward;
        _reverse = reverse;
    }

    public static VipsIccLutCmm? TryBuild(VipsIccProfile src, VipsIccProfile dst)
    {
        if (src == null || dst == null) return null;
        var fwd = LutTransform.TryFromTag(src, "A2B0");
        var rev = LutTransform.TryFromTag(dst, "B2A0");
        if (fwd == null || rev == null) return null;
        // RGB-ish only: 3-channel device input (or output for B2A) for now.
        if (fwd.InputChannels != 3 || fwd.OutputChannels != 3) return null;
        if (rev.InputChannels != 3 || rev.OutputChannels != 3) return null;
        return new VipsIccLutCmm(fwd, rev);
    }

    public void Apply(byte[] src, int srcOff, byte[] dst, int dstOff, int count, int bands)
    {
        if (bands != 3 && bands != 4)
            throw new ArgumentException("VipsIccLutCmm requires 3- or 4-band data", nameof(bands));

        Span<double> inBuf = stackalloc double[3];
        Span<double> pcs = stackalloc double[3];
        Span<double> outBuf = stackalloc double[3];

        for (int i = 0; i < count; i++)
        {
            int sp = srcOff + i * bands;
            int dp = dstOff + i * bands;
            inBuf[0] = src[sp]     / 255.0;
            inBuf[1] = src[sp + 1] / 255.0;
            inBuf[2] = src[sp + 2] / 255.0;

            _forward.Apply(inBuf, pcs);
            _reverse.Apply(pcs, outBuf);

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
}

/// <summary>
/// Per-direction LUT pipeline. Subclasses adapt the underlying tag
/// type (mft2 vs mAB/mBA) to a uniform Apply signature.
/// </summary>
internal abstract class LutTransform
{
    public abstract int InputChannels { get; }
    public abstract int OutputChannels { get; }
    public abstract void Apply(ReadOnlySpan<double> input, Span<double> output);

    /// <summary>
    /// Read a profile's tag at <paramref name="tagSig"/> and pick the
    /// best LUT representation: mft2 first (more common in older v2
    /// profiles), mAB/mBA second (modern v4). Returns <c>null</c> when
    /// neither tag is present in a recognised form.
    /// </summary>
    public static LutTransform? TryFromTag(VipsIccProfile profile, string tagSig)
    {
        var mft2 = profile.GetTagMft2(tagSig);
        if (mft2 != null) return new Mft2Transform(mft2);
        var lutAB = profile.GetTagLutAB(tagSig);
        if (lutAB != null) return new LutABTransform(lutAB);
        return null;
    }

    protected static double LookupCurve(ushort[] table, double x)
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
    /// 3D trilinear interpolation through a flat CLUT. Rows iterate
    /// the slowest axis, columns the fastest — same convention used
    /// by both mft2 and mAB CLUT layouts.
    /// </summary>
    protected static void TrilinearLookup3(ushort[] clut, int[] grids, int outCh,
        ReadOnlySpan<double> input, Span<double> output)
    {
        int g0 = grids[0], g1 = grids[1], g2 = grids[2];
        double xa = Math.Clamp(input[0], 0, 1) * (g0 - 1);
        double xb = Math.Clamp(input[1], 0, 1) * (g1 - 1);
        double xc = Math.Clamp(input[2], 0, 1) * (g2 - 1);
        int ia0 = (int)xa, ib0 = (int)xb, ic0 = (int)xc;
        int ia1 = Math.Min(ia0 + 1, g0 - 1);
        int ib1 = Math.Min(ib0 + 1, g1 - 1);
        int ic1 = Math.Min(ic0 + 1, g2 - 1);
        double fa = xa - ia0, fb = xb - ib0, fc = xc - ic0;

        int Idx(int aa, int bb, int cc, int ch) => ((aa * g1 + bb) * g2 + cc) * outCh + ch;

        for (int ch = 0; ch < outCh && ch < output.Length; ch++)
        {
            double v000 = clut[Idx(ia0, ib0, ic0, ch)] / 65535.0;
            double v001 = clut[Idx(ia0, ib0, ic1, ch)] / 65535.0;
            double v010 = clut[Idx(ia0, ib1, ic0, ch)] / 65535.0;
            double v011 = clut[Idx(ia0, ib1, ic1, ch)] / 65535.0;
            double v100 = clut[Idx(ia1, ib0, ic0, ch)] / 65535.0;
            double v101 = clut[Idx(ia1, ib0, ic1, ch)] / 65535.0;
            double v110 = clut[Idx(ia1, ib1, ic0, ch)] / 65535.0;
            double v111 = clut[Idx(ia1, ib1, ic1, ch)] / 65535.0;

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

/// <summary>mft2 (lut16Type) pipeline: input curves → matrix → 3D CLUT → output curves.</summary>
internal sealed class Mft2Transform : LutTransform
{
    private readonly IccMft2 _m;
    public Mft2Transform(IccMft2 m) { _m = m; }
    public override int InputChannels => _m.InputChannels;
    public override int OutputChannels => _m.OutputChannels;

    public override void Apply(ReadOnlySpan<double> input, Span<double> output)
    {
        Span<double> v = stackalloc double[3];
        // Input curves.
        v[0] = LookupCurve(_m.InputTables[0], input[0]);
        v[1] = LookupCurve(_m.InputTables[1], input[1]);
        v[2] = LookupCurve(_m.InputTables[2], input[2]);

        // 3×3 matrix. (Identity for non-XYZ inputs in well-formed profiles.)
        Span<double> mv = stackalloc double[3];
        for (int i = 0; i < 3; i++)
            mv[i] = _m.Matrix[i, 0] * v[0] + _m.Matrix[i, 1] * v[1] + _m.Matrix[i, 2] * v[2];

        // 3D CLUT trilinear.
        Span<double> clutOut = stackalloc double[3];
        var grids = new[] { _m.GridSize, _m.GridSize, _m.GridSize };
        TrilinearLookup3(_m.Clut, grids, _m.OutputChannels, mv, clutOut);

        // Output curves.
        output[0] = LookupCurve(_m.OutputTables[0], clutOut[0]);
        output[1] = LookupCurve(_m.OutputTables[1], clutOut[1]);
        output[2] = LookupCurve(_m.OutputTables[2], clutOut[2]);
    }
}

/// <summary>
/// mAB / mBA pipeline. Direction-dependent component order:
///   mAB (A→B): A curves → CLUT → M curves → matrix → B curves
///   mBA (B→A): B curves → matrix → M curves → CLUT → A curves
/// Each component is optional — missing pieces are skipped.
/// </summary>
internal sealed class LutABTransform : LutTransform
{
    private readonly IccLutAB _t;
    public LutABTransform(IccLutAB t) { _t = t; }
    public override int InputChannels => _t.InputChannels;
    public override int OutputChannels => _t.OutputChannels;

    public override void Apply(ReadOnlySpan<double> input, Span<double> output)
    {
        Span<double> tmp = stackalloc double[3];
        input.CopyTo(tmp);

        if (_t.IsAtoB)
        {
            ApplyCurves(_t.ACurves, tmp);
            ApplyClut(tmp, tmp);
            ApplyCurves(_t.MCurves, tmp);
            ApplyMatrix(tmp);
            ApplyCurves(_t.BCurves, tmp);
        }
        else
        {
            ApplyCurves(_t.BCurves, tmp);
            ApplyMatrix(tmp);
            ApplyCurves(_t.MCurves, tmp);
            ApplyClut(tmp, tmp);
            ApplyCurves(_t.ACurves, tmp);
        }

        for (int i = 0; i < 3 && i < output.Length; i++) output[i] = tmp[i];
    }

    private static void ApplyCurves(Func<double, double>[]? curves, Span<double> v)
    {
        if (curves == null) return;
        int n = Math.Min(curves.Length, v.Length);
        for (int i = 0; i < n; i++) v[i] = curves[i](v[i]);
    }

    private void ApplyMatrix(Span<double> v)
    {
        var m = _t.Matrix;
        if (m == null) return;
        // Output = M[3×3] * input + M[*, 3].
        double r0 = m[0, 0] * v[0] + m[0, 1] * v[1] + m[0, 2] * v[2] + m[0, 3];
        double r1 = m[1, 0] * v[0] + m[1, 1] * v[1] + m[1, 2] * v[2] + m[1, 3];
        double r2 = m[2, 0] * v[0] + m[2, 1] * v[1] + m[2, 2] * v[2] + m[2, 3];
        v[0] = r0; v[1] = r1; v[2] = r2;
    }

    private void ApplyClut(ReadOnlySpan<double> input, Span<double> output)
    {
        var clut = _t.Clut;
        if (clut == null) return;
        // Currently only 3-input CLUTs supported; higher-dim (CMYK) is
        // future work.
        if (clut.InputChannels != 3) return;
        TrilinearLookup3(clut.Data, clut.GridSizes, clut.OutputChannels, input, output);
    }
}
