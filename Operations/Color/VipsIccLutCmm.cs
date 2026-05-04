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
    private enum Pcs { Xyz, Lab }

    private readonly LutTransform _forward;
    private readonly LutTransform _reverse;
    private readonly Pcs _srcPcs;
    private readonly Pcs _dstPcs;

    private VipsIccLutCmm(LutTransform forward, LutTransform reverse, Pcs srcPcs, Pcs dstPcs)
    {
        _forward = forward;
        _reverse = reverse;
        _srcPcs = srcPcs;
        _dstPcs = dstPcs;
    }

    /// <summary>Channels expected on the source side (3 for RGB, 4 for CMYK).</summary>
    public int SrcChannels => _forward.InputChannels;
    /// <summary>Channels produced on the destination side.</summary>
    public int DstChannels => _reverse.OutputChannels;

    public static VipsIccLutCmm? TryBuild(VipsIccProfile src, VipsIccProfile dst)
    {
        if (src == null || dst == null) return null;
        var fwd = LutTransform.TryFromTag(src, "A2B0");
        var rev = LutTransform.TryFromTag(dst, "B2A0");
        if (fwd == null || rev == null) return null;
        // PCS is always 3D — both transforms must agree at the seam.
        if (fwd.OutputChannels != 3 || rev.InputChannels != 3) return null;
        // Device sides can be 1..4 (Gray / Lab / RGB / CMYK).
        if (fwd.InputChannels < 1 || fwd.InputChannels > 4) return null;
        if (rev.OutputChannels < 1 || rev.OutputChannels > 4) return null;

        // Determine each profile's PCS. Default to XYZ for unknown
        // profiles (matches ICC v2 default).
        Pcs srcPcs = src.ConnectionColorSpace == VipsIccColorSpace.Lab ? Pcs.Lab : Pcs.Xyz;
        Pcs dstPcs = dst.ConnectionColorSpace == VipsIccColorSpace.Lab ? Pcs.Lab : Pcs.Xyz;

        return new VipsIccLutCmm(fwd, rev, srcPcs, dstPcs);
    }

    /// <summary>
    /// Apply the transform pixel-by-pixel.
    /// <paramref name="srcBands"/> may include trailing alpha
    /// (<c>srcBands</c> = <see cref="SrcChannels"/> + 1); same for
    /// <paramref name="dstBands"/>. Alpha passes through to the output
    /// when both sides have one — otherwise it's dropped.
    /// </summary>
    public void Apply(byte[] src, int srcOff, int srcBands,
        byte[] dst, int dstOff, int dstBands, int count)
    {
        int srcCh = SrcChannels;
        int dstCh = DstChannels;
        if (srcBands < srcCh) throw new ArgumentException($"srcBands {srcBands} < required {srcCh}", nameof(srcBands));
        if (dstBands < dstCh) throw new ArgumentException($"dstBands {dstBands} < required {dstCh}", nameof(dstBands));

        Span<double> inBuf = stackalloc double[4];
        Span<double> pcs = stackalloc double[3];
        Span<double> outBuf = stackalloc double[4];

        bool passAlpha = srcBands > srcCh && dstBands > dstCh;

        for (int i = 0; i < count; i++)
        {
            int sp = srcOff + i * srcBands;
            int dp = dstOff + i * dstBands;
            for (int c = 0; c < srcCh; c++) inBuf[c] = src[sp + c] / 255.0;

            _forward.Apply(inBuf.Slice(0, srcCh), pcs);
            if (_srcPcs != _dstPcs) ConvertPcs(pcs, _srcPcs, _dstPcs);
            _reverse.Apply(pcs, outBuf.Slice(0, dstCh));

            for (int c = 0; c < dstCh; c++) dst[dp + c] = ToByte(outBuf[c]);
            if (passAlpha) dst[dp + dstCh] = src[sp + srcCh];
        }
    }

    /// <summary>
    /// Convenience overload for same-band-count transforms (the common
    /// case where srcBands == dstBands == channels [+ alpha]).
    /// </summary>
    public void Apply(byte[] src, int srcOff, byte[] dst, int dstOff, int count, int bands)
        => Apply(src, srcOff, bands, dst, dstOff, bands, count);

    private static byte ToByte(double v)
    {
        if (v <= 0) return 0;
        if (v >= 1) return 255;
        return (byte)(v * 255.0 + 0.5);
    }

    /// <summary>
    /// Convert normalized PCS values from <paramref name="from"/> to
    /// <paramref name="to"/> in place. Decodes the source's encoding
    /// to absolute Lab/XYZ, runs the standard CIE conversion, then
    /// re-encodes for the destination's normalized [0, 1] range.
    /// </summary>
    private static void ConvertPcs(Span<double> v, Pcs from, Pcs to)
    {
        // ICC encoded XYZ: the maximum normalized value 1.0 = uint16 65535
        // maps to ABS XYZ ≈ 65535/32768 = 1.999969...
        const double XyzScale = 65535.0 / 32768.0;

        if (from == Pcs.Lab && to == Pcs.Xyz)
        {
            double L = v[0] * 100.0;
            double a = v[1] * 256.0 - 128.0;
            double b = v[2] * 256.0 - 128.0;
            LabToXyz(L, a, b, out double X, out double Y, out double Z);
            v[0] = Math.Clamp(X / XyzScale, 0, 1);
            v[1] = Math.Clamp(Y / XyzScale, 0, 1);
            v[2] = Math.Clamp(Z / XyzScale, 0, 1);
        }
        else if (from == Pcs.Xyz && to == Pcs.Lab)
        {
            double X = v[0] * XyzScale;
            double Y = v[1] * XyzScale;
            double Z = v[2] * XyzScale;
            XyzToLab(X, Y, Z, out double L, out double a, out double b);
            v[0] = Math.Clamp(L / 100.0, 0, 1);
            v[1] = Math.Clamp((a + 128.0) / 256.0, 0, 1);
            v[2] = Math.Clamp((b + 128.0) / 256.0, 0, 1);
        }
    }

    // CIE D50 white point as used by ICC profiles.
    private const double D50X = 0.96422;
    private const double D50Y = 1.00000;
    private const double D50Z = 0.82521;
    private const double LabEpsilon = 216.0 / 24389.0;       // (6/29)^3
    private const double LabKappa = 24389.0 / 27.0;          // (29/3)^3

    private static void LabToXyz(double L, double a, double b, out double X, out double Y, out double Z)
    {
        double fy = (L + 16.0) / 116.0;
        double fx = a / 500.0 + fy;
        double fz = fy - b / 200.0;
        X = D50X * LabFInv(fx);
        Y = D50Y * LabFInv(fy);
        Z = D50Z * LabFInv(fz);
    }

    private static void XyzToLab(double X, double Y, double Z, out double L, out double a, out double b)
    {
        double fx = LabF(X / D50X);
        double fy = LabF(Y / D50Y);
        double fz = LabF(Z / D50Z);
        L = 116.0 * fy - 16.0;
        a = 500.0 * (fx - fy);
        b = 200.0 * (fy - fz);
    }

    private static double LabF(double t)
    {
        if (t > LabEpsilon) return Math.Cbrt(t);
        return (LabKappa * t + 16.0) / 116.0;
    }

    private static double LabFInv(double t)
    {
        double t3 = t * t * t;
        if (t3 > LabEpsilon) return t3;
        return (116.0 * t - 16.0) / LabKappa;
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
        // Try forms in newest-to-oldest order: mAB/mBA (v4 modern),
        // mft2 (v2 16-bit), mft1 (v2 8-bit). mft1 maps to IccMft2 with
        // 8→16-bit scaling so the same pipeline runs for both.
        var lutAB = profile.GetTagLutAB(tagSig);
        if (lutAB != null) return new LutABTransform(lutAB);
        var mft2 = profile.GetTagMft2(tagSig);
        if (mft2 != null) return new Mft2Transform(mft2);
        var mft1 = profile.GetTagMft1(tagSig);
        if (mft1 != null) return new Mft2Transform(mft1);
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
    /// N-linear interpolation through a flat CLUT (1..4 input dimensions).
    /// Visits every corner of the surrounding hypercube (2^n corners),
    /// computing the product of axis weights and accumulating into each
    /// output channel. Slower than hand-rolled trilinear for 3D, but
    /// uniform code that handles CMYK 4D CLUTs without separate paths.
    /// </summary>
    protected static void NLinearLookup(ushort[] clut, int[] grids, int outCh,
        ReadOnlySpan<double> input, Span<double> output)
    {
        int n = grids.Length;
        Span<int> i0 = stackalloc int[8];   // up to 8 input dims
        Span<double> frac = stackalloc double[8];
        for (int k = 0; k < n; k++)
        {
            double v = Math.Clamp(input[k], 0, 1) * (grids[k] - 1);
            int ik = (int)v;
            if (ik >= grids[k] - 1) { ik = grids[k] - 1; frac[k] = 0; }
            else frac[k] = v - ik;
            i0[k] = ik;
        }

        int totalCorners = 1 << n;
        for (int ch = 0; ch < outCh && ch < output.Length; ch++)
        {
            double sum = 0;
            for (int corner = 0; corner < totalCorners; corner++)
            {
                long idx = 0;
                double weight = 1.0;
                for (int k = 0; k < n; k++)
                {
                    bool high = (corner & (1 << k)) != 0;
                    int ik = high ? Math.Min(i0[k] + 1, grids[k] - 1) : i0[k];
                    idx = idx * grids[k] + ik;
                    weight *= high ? frac[k] : (1 - frac[k]);
                }
                sum += clut[idx * outCh + ch] * weight;
            }
            output[ch] = sum / 65535.0;
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
        int inCh = _m.InputChannels;
        int outCh = _m.OutputChannels;
        Span<double> v = stackalloc double[4];

        // Input curves.
        for (int i = 0; i < inCh; i++)
            v[i] = LookupCurve(_m.InputTables[i], input[i]);

        // 3×3 matrix only applies to 3-channel input (XYZ profiles).
        if (inCh == 3)
        {
            Span<double> mv = stackalloc double[3];
            for (int i = 0; i < 3; i++)
                mv[i] = _m.Matrix[i, 0] * v[0] + _m.Matrix[i, 1] * v[1] + _m.Matrix[i, 2] * v[2];
            v[0] = mv[0]; v[1] = mv[1]; v[2] = mv[2];
        }

        // CLUT n-linear lookup.
        Span<double> clutOut = stackalloc double[4];
        Span<int> grids = stackalloc int[inCh];
        for (int i = 0; i < inCh; i++) grids[i] = _m.GridSize;
        NLinearLookup(_m.Clut, grids.ToArray(), outCh, v.Slice(0, inCh), clutOut);

        // Output curves.
        for (int c = 0; c < outCh; c++)
            output[c] = LookupCurve(_m.OutputTables[c], clutOut[c]);
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
        Span<double> tmp = stackalloc double[4];
        Span<double> tmp2 = stackalloc double[4];
        int curLen = input.Length;
        for (int i = 0; i < curLen; i++) tmp[i] = input[i];

        if (_t.IsAtoB)
        {
            // mAB: A curves (inCh) → CLUT (inCh→outCh, normally outCh=3)
            // → M curves (3) → matrix (3×4) → B curves (outCh).
            ApplyCurves(_t.ACurves, tmp, curLen);
            curLen = ApplyClut(tmp, curLen, tmp2);
            ApplyCurves(_t.MCurves, tmp, Math.Min(curLen, 3));
            if (curLen >= 3) ApplyMatrix(tmp);
            ApplyCurves(_t.BCurves, tmp, curLen);
        }
        else
        {
            // mBA: B curves (3) → matrix (3×4) → M curves (3)
            // → CLUT (3→outCh) → A curves (outCh).
            ApplyCurves(_t.BCurves, tmp, curLen);
            if (curLen >= 3) ApplyMatrix(tmp);
            ApplyCurves(_t.MCurves, tmp, Math.Min(curLen, 3));
            curLen = ApplyClut(tmp, curLen, tmp2);
            ApplyCurves(_t.ACurves, tmp, curLen);
        }

        for (int i = 0; i < curLen && i < output.Length; i++) output[i] = tmp[i];
    }

    private static void ApplyCurves(Func<double, double>[]? curves, Span<double> v, int n)
    {
        if (curves == null) return;
        int count = Math.Min(curves.Length, n);
        for (int i = 0; i < count; i++) v[i] = curves[i](v[i]);
    }

    private void ApplyMatrix(Span<double> v)
    {
        var m = _t.Matrix;
        if (m == null) return;
        double r0 = m[0, 0] * v[0] + m[0, 1] * v[1] + m[0, 2] * v[2] + m[0, 3];
        double r1 = m[1, 0] * v[0] + m[1, 1] * v[1] + m[1, 2] * v[2] + m[1, 3];
        double r2 = m[2, 0] * v[0] + m[2, 1] * v[1] + m[2, 2] * v[2] + m[2, 3];
        v[0] = r0; v[1] = r1; v[2] = r2;
    }

    /// <summary>
    /// Apply the CLUT step in place. Input has <paramref name="inLen"/>
    /// channels; on return, the buffer holds the CLUT's output (which
    /// may be a different number of channels). Returns the new channel
    /// count. Uses <paramref name="scratch"/> as a temp so the source
    /// values aren't overwritten before being used as indices.
    /// </summary>
    private int ApplyClut(Span<double> v, int inLen, Span<double> scratch)
    {
        var clut = _t.Clut;
        if (clut == null) return inLen;
        if (clut.InputChannels != inLen) return inLen;  // shape mismatch; skip
        NLinearLookup(clut.Data, clut.GridSizes, clut.OutputChannels, v.Slice(0, inLen), scratch);
        for (int i = 0; i < clut.OutputChannels; i++) v[i] = scratch[i];
        return clut.OutputChannels;
    }
}
