using System;
using CosmoImage.Operations.Metadata;

namespace CosmoImage.Operations.Color;

/// <summary>
/// Pure-managed ICC color management for Matrix/TRC profiles
/// (the dominant form: sRGB, AdobeRGB, ProPhoto, Display-P3, etc.).
/// Builds a forward+reverse transform between two profiles via
/// the D50 XYZ profile connection space (PCS).
///
/// <para>Pipeline per pixel:</para>
/// <list type="number">
///   <item>device RGB → linear RGB (per-channel TRC of source)</item>
///   <item>linear src RGB → D50 XYZ (3×3 matrix, columns rXYZ gXYZ bXYZ)</item>
///   <item>D50 XYZ → linear dst RGB (inverse of dst's matrix)</item>
///   <item>linear dst RGB → device RGB (per-channel inverse TRC of dst)</item>
/// </list>
///
/// <para>For 8-bit input the source curves and the dst inverse curves
/// are precomputed as fixed-size LUTs (256 forward, 4096 inverse), so
/// the inner per-pixel loop is one matrix multiply + six table lookups
/// — fast enough to displace the previous Magick-based fallback for
/// the 90% case.</para>
///
/// <para>LUT-based profiles (mAB / mBA / lutAtoB / lutBtoA), CMYK,
/// and Lab PCS are not yet covered — <see cref="TryBuild"/> returns
/// null and the caller can fall back to Magick.</para>
/// </summary>
public sealed class VipsIccCmm
{
    private const int InverseLutSize = 4096;

    private readonly double[][] _srcLin;        // [3][256]
    private readonly double[,] _matrix;          // 3×3 combined M_dst^-1 * M_src
    private readonly byte[][] _dstInv;           // [3][4096]

    private VipsIccCmm(double[][] srcLin, double[,] matrix, byte[][] dstInv)
    {
        _srcLin = srcLin;
        _matrix = matrix;
        _dstInv = dstInv;
    }

    /// <summary>
    /// Build a CMM transform that converts pixels from
    /// <paramref name="src"/>'s color space to <paramref name="dst"/>'s.
    /// Returns <c>null</c> when either profile isn't a Matrix/TRC RGB
    /// profile (the caller should fall back to a more general CMM).
    /// </summary>
    public static VipsIccCmm? TryBuild(VipsIccProfile src, VipsIccProfile dst)
    {
        if (src == null || dst == null) return null;
        var mSrc = ExtractMatrix(src);
        var mDst = ExtractMatrix(dst);
        if (mSrc == null || mDst == null) return null;

        var srcCurveR = src.GetTagCurveEvaluator("rTRC");
        var srcCurveG = src.GetTagCurveEvaluator("gTRC");
        var srcCurveB = src.GetTagCurveEvaluator("bTRC");
        var dstCurveR = dst.GetTagCurveEvaluator("rTRC");
        var dstCurveG = dst.GetTagCurveEvaluator("gTRC");
        var dstCurveB = dst.GetTagCurveEvaluator("bTRC");
        if (srcCurveR == null || srcCurveG == null || srcCurveB == null
            || dstCurveR == null || dstCurveG == null || dstCurveB == null)
            return null;

        var mDstInv = Invert3x3(mDst);
        if (mDstInv == null) return null;
        var combined = Multiply3x3(mDstInv, mSrc);

        // Precompute per-channel 256→linear LUTs for fast 8-bit input.
        var srcLin = new double[3][];
        srcLin[0] = BuildForwardLut(srcCurveR);
        srcLin[1] = BuildForwardLut(srcCurveG);
        srcLin[2] = BuildForwardLut(srcCurveB);

        // Precompute per-channel inverse LUTs: 4096 sample points across
        // [0, 1] linear → device-space byte. Out-of-gamut linear values
        // (negative or >1) clamp to bounds before lookup.
        var dstInv = new byte[3][];
        dstInv[0] = BuildInverseLut(dstCurveR);
        dstInv[1] = BuildInverseLut(dstCurveG);
        dstInv[2] = BuildInverseLut(dstCurveB);

        return new VipsIccCmm(srcLin, combined, dstInv);
    }

    /// <summary>
    /// Apply the transform to <paramref name="count"/> pixels.
    /// Pixels are tightly packed RGB (<paramref name="bands"/> = 3) or
    /// RGBA (<paramref name="bands"/> = 4); for RGBA the alpha
    /// channel passes through unchanged.
    /// </summary>
    public void Apply(byte[] src, int srcOff, byte[] dst, int dstOff, int count, int bands)
    {
        if (bands != 3 && bands != 4)
            throw new ArgumentException("VipsIccCmm requires 3- or 4-band data", nameof(bands));

        double m00 = _matrix[0, 0], m01 = _matrix[0, 1], m02 = _matrix[0, 2];
        double m10 = _matrix[1, 0], m11 = _matrix[1, 1], m12 = _matrix[1, 2];
        double m20 = _matrix[2, 0], m21 = _matrix[2, 1], m22 = _matrix[2, 2];

        var srcR = _srcLin[0]; var srcG = _srcLin[1]; var srcB = _srcLin[2];
        var dstR = _dstInv[0]; var dstG = _dstInv[1]; var dstB = _dstInv[2];

        for (int i = 0; i < count; i++)
        {
            int sp = srcOff + i * bands;
            int dp = dstOff + i * bands;
            double rL = srcR[src[sp]];
            double gL = srcG[src[sp + 1]];
            double bL = srcB[src[sp + 2]];

            double rO = m00 * rL + m01 * gL + m02 * bL;
            double gO = m10 * rL + m11 * gL + m12 * bL;
            double bO = m20 * rL + m21 * gL + m22 * bL;

            dst[dp]     = LookupInverse(dstR, rO);
            dst[dp + 1] = LookupInverse(dstG, gO);
            dst[dp + 2] = LookupInverse(dstB, bO);
            if (bands == 4) dst[dp + 3] = src[sp + 3];
        }
    }

    private static byte LookupInverse(byte[] inv, double linear)
    {
        if (linear <= 0) return inv[0];
        if (linear >= 1) return inv[InverseLutSize - 1];
        int idx = (int)(linear * (InverseLutSize - 1) + 0.5);
        return inv[idx];
    }

    private static double[] BuildForwardLut(Func<double, double> curve)
    {
        var lut = new double[256];
        for (int i = 0; i < 256; i++) lut[i] = curve(i / 255.0);
        return lut;
    }

    /// <summary>
    /// Build an inverse curve LUT: index <c>i</c> represents linear
    /// value <c>i/(N-1)</c>; the entry stores the corresponding 8-bit
    /// device value such that <c>forward(device/255) ≈ linear</c>.
    /// </summary>
    private static byte[] BuildInverseLut(Func<double, double> forward)
    {
        // First build a forward sample table at byte resolution so we
        // can binary-search for the inverse.
        var fwd = new double[256];
        for (int i = 0; i < 256; i++) fwd[i] = forward(i / 255.0);

        var inv = new byte[InverseLutSize];
        for (int j = 0; j < InverseLutSize; j++)
        {
            double y = j / (double)(InverseLutSize - 1);
            // Bracketed binary search; assumes fwd is monotonically non-decreasing
            // (true for sane TRCs).
            int lo = 0, hi = 255;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (fwd[mid] < y) lo = mid + 1;
                else hi = mid;
            }
            // Pick the closer of fwd[lo-1] and fwd[lo].
            int best = lo;
            if (lo > 0 && Math.Abs(fwd[lo - 1] - y) < Math.Abs(fwd[lo] - y))
                best = lo - 1;
            inv[j] = (byte)best;
        }
        return inv;
    }

    /// <summary>Extract the 3×3 matrix (columns rXYZ gXYZ bXYZ) from a Matrix/TRC profile.</summary>
    private static double[,]? ExtractMatrix(VipsIccProfile p)
    {
        var r = p.RedPrimary;
        var g = p.GreenPrimary;
        var b = p.BluePrimary;
        if (r == null || g == null || b == null) return null;
        return new double[,]
        {
            { r.Value.X, g.Value.X, b.Value.X },
            { r.Value.Y, g.Value.Y, b.Value.Y },
            { r.Value.Z, g.Value.Z, b.Value.Z },
        };
    }

    private static double[,]? Invert3x3(double[,] m)
    {
        double a = m[0, 0], b = m[0, 1], c = m[0, 2];
        double d = m[1, 0], e = m[1, 1], f = m[1, 2];
        double g = m[2, 0], h = m[2, 1], i = m[2, 2];
        double det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        if (Math.Abs(det) < 1e-12) return null;
        double inv = 1.0 / det;
        return new double[,]
        {
            { (e * i - f * h) * inv, -(b * i - c * h) * inv,  (b * f - c * e) * inv },
            { -(d * i - f * g) * inv, (a * i - c * g) * inv, -(a * f - c * d) * inv },
            { (d * h - e * g) * inv, -(a * h - b * g) * inv,  (a * e - b * d) * inv },
        };
    }

    private static double[,] Multiply3x3(double[,] a, double[,] b)
    {
        var r = new double[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                double s = 0;
                for (int k = 0; k < 3; k++) s += a[i, k] * b[k, j];
                r[i, j] = s;
            }
        return r;
    }
}
