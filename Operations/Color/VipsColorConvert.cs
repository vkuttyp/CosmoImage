using System;

namespace CosmoImage.Operations.Color;

// ---------- Color value structs ----------
//
// All channel values use floating point. Conventions:
//   • RGB / HSV / HSL / CMYK channels in [0, 1].
//   • Hue in degrees [0, 360).
//   • CIE XYZ normalised so reference-white Y = 1 (D65).
//   • CIE L* in [0, 100]; a*, b* in roughly [-128, 128]; C* ≥ 0.

/// <summary>sRGB color in [0, 1] per channel.</summary>
public readonly record struct VipsColorRgb(double R, double G, double B);

/// <summary>HSL — hue (degrees), saturation [0,1], lightness [0,1].</summary>
public readonly record struct VipsColorHsl(double H, double S, double L);

/// <summary>HSV — hue (degrees), saturation [0,1], value [0,1].</summary>
public readonly record struct VipsColorHsv(double H, double S, double V);

/// <summary>CMYK — cyan, magenta, yellow, key in [0,1] each.</summary>
public readonly record struct VipsColorCmyk(double C, double M, double Y, double K);

/// <summary>CIE 1931 XYZ (D65), normalised so reference-white Y = 1.</summary>
public readonly record struct VipsColorXyz(double X, double Y, double Z);

/// <summary>CIE L*a*b* (D65) — L*: [0, 100]; a*, b*: ~[-128, 128].</summary>
public readonly record struct VipsColorLab(double L, double A, double B);

/// <summary>CIE L*C*h*ab — polar form of L*a*b*. H in degrees.</summary>
public readonly record struct VipsColorLch(double L, double C, double H);

/// <summary>CIE xyY — chromaticity coordinates (x, y) plus luminance Y.</summary>
public readonly record struct VipsColorXyy(double X, double Y, double LargeY);

/// <summary>
/// CIE 1976 L*u*v* (D65). L*: [0, 100]; u*, v*: roughly [-200, 200].
/// </summary>
public readonly record struct VipsColorLuv(double L, double U, double V);

/// <summary>CIE L*C*h*uv — polar form of L*u*v*. H in degrees.</summary>
public readonly record struct VipsColorLchuv(double L, double C, double H);

/// <summary>
/// LMS cone-response space. Used as the intermediate space for
/// chromatic adaptation. Values are transform-method-specific — don't
/// compare LMS values produced by different matrices.
/// </summary>
public readonly record struct VipsColorLms(double L, double M, double S);

/// <summary>Hunter L*a*b* (D65 reference white). Older Lab variant — uses sqrt instead of cube root.</summary>
public readonly record struct VipsColorHunterLab(double L, double A, double B);

/// <summary>YCbCr per ITU-R BT.601 (the JPEG variant). All channels in [0, 1]; chroma is offset (0.5 = neutral).</summary>
public readonly record struct VipsColorYCbCr(double Y, double Cb, double Cr);

/// <summary>Chromatic adaptation matrix family. Each gives different LMS coordinates.</summary>
public enum VipsLmsAdaptation
{
    /// <summary>Bradford von Kries — most common, high accuracy.</summary>
    Bradford = 0,
    /// <summary>CIECAM02 chromatic adaptation matrix.</summary>
    Cat02 = 1,
    /// <summary>CIECAT97 sharpened.</summary>
    Cat97s = 2,
}

/// <summary>Reference white points used for chromatic adaptation.</summary>
public enum VipsWhitePoint
{
    /// <summary>Daylight 6504K — sRGB / Rec. 709 native white.</summary>
    D65 = 0,
    /// <summary>Daylight 5003K — print / ICC v2 reference.</summary>
    D50 = 1,
    /// <summary>Daylight 5503K.</summary>
    D55 = 2,
    /// <summary>Daylight 7504K.</summary>
    D75 = 3,
    /// <summary>Tungsten incandescent (~2856K).</summary>
    A = 4,
    /// <summary>Equal-energy white.</summary>
    E = 5,
}

/// <summary>
/// Per-pixel color-space conversion. Mirrors ImageSharp's
/// <c>ColorSpaceConverter</c> family.
///
/// <para>Direct pairwise conversions are provided between adjacent
/// spaces (RGB↔HSL, RGB↔HSV, RGB↔CMYK, RGB↔XYZ, XYZ↔Lab, Lab↔Lch).
/// The generic <see cref="Convert{TFrom, TTo}"/> chains them
/// through RGB or XYZ as appropriate; common cases (RGB↔HSL etc.)
/// route directly without going through XYZ to avoid precision loss.</para>
///
/// <para>RGB is treated as sRGB with the standard
/// gamma-encoding curve. XYZ uses D65 reference white.</para>
/// </summary>
public static class VipsColorConvert
{
    // ---------- RGB ↔ HSL ----------

    public static VipsColorHsl RgbToHsl(VipsColorRgb rgb)
    {
        double r = rgb.R, g = rgb.G, b = rgb.B;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2;
        double c = max - min;
        if (c < 1e-12) return new VipsColorHsl(0, 0, l);
        double s = c / (1 - Math.Abs(2 * l - 1));
        double h = HueFromRgb(r, g, b, max, c);
        return new VipsColorHsl(h, s, l);
    }

    public static VipsColorRgb HslToRgb(VipsColorHsl hsl)
    {
        double h = hsl.H, s = hsl.S, l = hsl.L;
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double m = l - c / 2;
        return RgbFromHueChroma(h, c, m);
    }

    // ---------- RGB ↔ HSV ----------

    public static VipsColorHsv RgbToHsv(VipsColorRgb rgb)
    {
        double r = rgb.R, g = rgb.G, b = rgb.B;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double c = max - min;
        if (c < 1e-12) return new VipsColorHsv(0, 0, max);
        double s = max < 1e-12 ? 0 : c / max;
        double h = HueFromRgb(r, g, b, max, c);
        return new VipsColorHsv(h, s, max);
    }

    public static VipsColorRgb HsvToRgb(VipsColorHsv hsv)
    {
        double h = hsv.H, s = hsv.S, v = hsv.V;
        double c = v * s;
        double m = v - c;
        return RgbFromHueChroma(h, c, m);
    }

    // ---------- RGB ↔ CMYK ----------

    public static VipsColorCmyk RgbToCmyk(VipsColorRgb rgb)
    {
        double r = rgb.R, g = rgb.G, b = rgb.B;
        double k = 1 - Math.Max(r, Math.Max(g, b));
        if (k >= 1 - 1e-12) return new VipsColorCmyk(0, 0, 0, 1);
        double inv = 1 - k;
        return new VipsColorCmyk((1 - r - k) / inv, (1 - g - k) / inv, (1 - b - k) / inv, k);
    }

    public static VipsColorRgb CmykToRgb(VipsColorCmyk cmyk)
    {
        double inv = 1 - cmyk.K;
        return new VipsColorRgb(
            (1 - cmyk.C) * inv,
            (1 - cmyk.M) * inv,
            (1 - cmyk.Y) * inv);
    }

    // ---------- RGB ↔ XYZ (D65, sRGB) ----------

    public static VipsColorXyz RgbToXyz(VipsColorRgb rgb)
    {
        double r = SrgbToLinear(rgb.R);
        double g = SrgbToLinear(rgb.G);
        double b = SrgbToLinear(rgb.B);
        return new VipsColorXyz(
            0.4124564 * r + 0.3575761 * g + 0.1804375 * b,
            0.2126729 * r + 0.7151522 * g + 0.0721750 * b,
            0.0193339 * r + 0.1191920 * g + 0.9503041 * b);
    }

    public static VipsColorRgb XyzToRgb(VipsColorXyz xyz)
    {
        double x = xyz.X, y = xyz.Y, z = xyz.Z;
        double r =  3.2404542 * x - 1.5371385 * y - 0.4985314 * z;
        double g = -0.9692660 * x + 1.8760108 * y + 0.0415560 * z;
        double b =  0.0556434 * x - 0.2040259 * y + 1.0572252 * z;
        return new VipsColorRgb(LinearToSrgb(r), LinearToSrgb(g), LinearToSrgb(b));
    }

    // ---------- XYZ ↔ Lab (D65) ----------

    private const double Xn = 0.95047, Yn = 1.0, Zn = 1.08883;     // D65
    private const double Delta = 6.0 / 29;
    private const double Delta2 = Delta * Delta;
    private const double Delta3 = Delta * Delta * Delta;

    public static VipsColorLab XyzToLab(VipsColorXyz xyz)
    {
        double fx = LabF(xyz.X / Xn);
        double fy = LabF(xyz.Y / Yn);
        double fz = LabF(xyz.Z / Zn);
        return new VipsColorLab(
            116 * fy - 16,
            500 * (fx - fy),
            200 * (fy - fz));
    }

    public static VipsColorXyz LabToXyz(VipsColorLab lab)
    {
        double fy = (lab.L + 16) / 116;
        double fx = lab.A / 500 + fy;
        double fz = fy - lab.B / 200;
        return new VipsColorXyz(Xn * LabFInv(fx), Yn * LabFInv(fy), Zn * LabFInv(fz));
    }

    private static double LabF(double t)
        => t > Delta3 ? Math.Cbrt(t) : t / (3 * Delta2) + 4.0 / 29;
    private static double LabFInv(double t)
        => t > Delta ? t * t * t : 3 * Delta2 * (t - 4.0 / 29);

    // ---------- Lab ↔ Lch (polar) ----------

    public static VipsColorLch LabToLch(VipsColorLab lab)
    {
        double c = Math.Sqrt(lab.A * lab.A + lab.B * lab.B);
        double h = Math.Atan2(lab.B, lab.A) * 180 / Math.PI;
        if (h < 0) h += 360;
        return new VipsColorLch(lab.L, c, h);
    }

    public static VipsColorLab LchToLab(VipsColorLch lch)
    {
        double rad = lch.H * Math.PI / 180;
        return new VipsColorLab(lch.L, lch.C * Math.Cos(rad), lch.C * Math.Sin(rad));
    }

    // ---------- XYZ ↔ xyY ----------

    public static VipsColorXyy XyzToXyy(VipsColorXyz xyz)
    {
        double sum = xyz.X + xyz.Y + xyz.Z;
        if (sum < 1e-12)
        {
            // Black — convention: chromaticity at the D65 white point.
            return new VipsColorXyy(0.31271, 0.32902, 0);
        }
        return new VipsColorXyy(xyz.X / sum, xyz.Y / sum, xyz.Y);
    }

    public static VipsColorXyz XyyToXyz(VipsColorXyy xyy)
    {
        if (xyy.Y < 1e-12) return new VipsColorXyz(0, 0, 0);
        return new VipsColorXyz(
            xyy.X * xyy.LargeY / xyy.Y,
            xyy.LargeY,
            (1 - xyy.X - xyy.Y) * xyy.LargeY / xyy.Y);
    }

    // ---------- XYZ ↔ Luv (D65) ----------

    // u'_n, v'_n at D65 (standard reference).
    private const double UnD65 = 0.19783000664283185;
    private const double VnD65 = 0.46831999493879866;

    public static VipsColorLuv XyzToLuv(VipsColorXyz xyz)
    {
        double denom = xyz.X + 15 * xyz.Y + 3 * xyz.Z;
        double up = denom < 1e-12 ? UnD65 : 4 * xyz.X / denom;
        double vp = denom < 1e-12 ? VnD65 : 9 * xyz.Y / denom;
        double yr = xyz.Y / Yn;
        double l = yr > Delta3 ? 116 * Math.Cbrt(yr) - 16 : Math.Pow(29.0 / 3, 3) * yr;
        return new VipsColorLuv(l, 13 * l * (up - UnD65), 13 * l * (vp - VnD65));
    }

    public static VipsColorXyz LuvToXyz(VipsColorLuv luv)
    {
        if (luv.L < 1e-9) return new VipsColorXyz(0, 0, 0);
        double up = luv.U / (13 * luv.L) + UnD65;
        double vp = luv.V / (13 * luv.L) + VnD65;
        double y = luv.L > 8
            ? Yn * Math.Pow((luv.L + 16) / 116, 3)
            : Yn * luv.L * Math.Pow(3.0 / 29, 3);
        if (vp < 1e-12) return new VipsColorXyz(0, y, 0);
        double x = y * 9 * up / (4 * vp);
        double z = y * (12 - 3 * up - 20 * vp) / (4 * vp);
        return new VipsColorXyz(x, y, z);
    }

    // ---------- Luv ↔ Lchuv (polar) ----------

    public static VipsColorLchuv LuvToLchuv(VipsColorLuv luv)
    {
        double c = Math.Sqrt(luv.U * luv.U + luv.V * luv.V);
        double h = Math.Atan2(luv.V, luv.U) * 180 / Math.PI;
        if (h < 0) h += 360;
        return new VipsColorLchuv(luv.L, c, h);
    }

    public static VipsColorLuv LchuvToLuv(VipsColorLchuv lch)
    {
        double rad = lch.H * Math.PI / 180;
        return new VipsColorLuv(lch.L, lch.C * Math.Cos(rad), lch.C * Math.Sin(rad));
    }

    // ---------- XYZ ↔ LMS (Bradford / CAT02 / CAT97s) + chromatic adaptation ----------

    private static readonly double[] BradfordFwd = {
         0.8951,  0.2664, -0.1614,
        -0.7502,  1.7135,  0.0367,
         0.0389, -0.0685,  1.0296,
    };
    private static readonly double[] BradfordInv = Invert3x3(BradfordFwd);

    private static readonly double[] Cat02Fwd = {
         0.7328,  0.4296, -0.1624,
        -0.7036,  1.6975,  0.0061,
         0.0030,  0.0136,  0.9834,
    };
    private static readonly double[] Cat02Inv = Invert3x3(Cat02Fwd);

    private static readonly double[] Cat97sFwd = {
         0.8562,  0.3372, -0.1934,
        -0.8360,  1.8327,  0.0033,
         0.0357, -0.0469,  1.0112,
    };
    private static readonly double[] Cat97sInv = Invert3x3(Cat97sFwd);

    private static double[] FwdMatrix(VipsLmsAdaptation m) => m switch
    {
        VipsLmsAdaptation.Bradford => BradfordFwd,
        VipsLmsAdaptation.Cat02 => Cat02Fwd,
        VipsLmsAdaptation.Cat97s => Cat97sFwd,
        _ => BradfordFwd,
    };

    private static double[] InvMatrix(VipsLmsAdaptation m) => m switch
    {
        VipsLmsAdaptation.Bradford => BradfordInv,
        VipsLmsAdaptation.Cat02 => Cat02Inv,
        VipsLmsAdaptation.Cat97s => Cat97sInv,
        _ => BradfordInv,
    };

    public static VipsColorLms XyzToLms(VipsColorXyz xyz, VipsLmsAdaptation method = VipsLmsAdaptation.Bradford)
    {
        var t = Mul3(FwdMatrix(method), xyz.X, xyz.Y, xyz.Z);
        return new VipsColorLms(t.a, t.b, t.c);
    }

    public static VipsColorXyz LmsToXyz(VipsColorLms lms, VipsLmsAdaptation method = VipsLmsAdaptation.Bradford)
    {
        var t = Mul3(InvMatrix(method), lms.L, lms.M, lms.S);
        return new VipsColorXyz(t.a, t.b, t.c);
    }

    private static (double a, double b, double c) Mul3(double[] m, double x, double y, double z)
        => (m[0] * x + m[1] * y + m[2] * z,
            m[3] * x + m[4] * y + m[5] * z,
            m[6] * x + m[7] * y + m[8] * z);

    private static double[] Invert3x3(double[] m)
    {
        double det =
            m[0] * (m[4] * m[8] - m[5] * m[7]) -
            m[1] * (m[3] * m[8] - m[5] * m[6]) +
            m[2] * (m[3] * m[7] - m[4] * m[6]);
        if (Math.Abs(det) < 1e-18)
            throw new InvalidOperationException("Singular 3×3 matrix");
        double inv = 1.0 / det;
        return new double[]
        {
            inv * (m[4] * m[8] - m[5] * m[7]),
            inv * (m[2] * m[7] - m[1] * m[8]),
            inv * (m[1] * m[5] - m[2] * m[4]),
            inv * (m[5] * m[6] - m[3] * m[8]),
            inv * (m[0] * m[8] - m[2] * m[6]),
            inv * (m[2] * m[3] - m[0] * m[5]),
            inv * (m[3] * m[7] - m[4] * m[6]),
            inv * (m[1] * m[6] - m[0] * m[7]),
            inv * (m[0] * m[4] - m[1] * m[3]),
        };
    }

    // ---------- XYZ ↔ Hunter Lab (D65) ----------
    //
    // Forward:
    //   yr = Y / Yn ; sqrt_yr = sqrt(yr)
    //   L = 100 · sqrt_yr
    //   a = Ka · ((X / Xn) - yr) / sqrt_yr
    //   b = Kb · (yr - (Z / Zn)) / sqrt_yr
    // Ka, Kb are illuminant-dependent constants. For D65:
    //   Ka = 175.0 / 198.04 · (Xn + Yn) ≈ 1.7237
    //   Kb = 70.0 / 218.11 · (Yn + Zn) ≈ 0.6705
    // Older sources just use 175.0 / 70.0 directly; we use the scaled
    // values which match recent CIE references.

    private static readonly double HunterKaD65 = 175.0 / 198.04 * (Xn + Yn);
    private static readonly double HunterKbD65 = 70.0 / 218.11 * (Yn + Zn);

    public static VipsColorHunterLab XyzToHunterLab(VipsColorXyz xyz)
    {
        if (xyz.Y < 1e-12) return new VipsColorHunterLab(0, 0, 0);
        double yr = xyz.Y / Yn;
        double sqrtYr = Math.Sqrt(yr);
        return new VipsColorHunterLab(
            100 * sqrtYr,
            HunterKaD65 * (xyz.X / Xn - yr) / sqrtYr,
            HunterKbD65 * (yr - xyz.Z / Zn) / sqrtYr);
    }

    public static VipsColorXyz HunterLabToXyz(VipsColorHunterLab lab)
    {
        if (lab.L < 1e-9) return new VipsColorXyz(0, 0, 0);
        double sqrtYr = lab.L / 100;
        double yr = sqrtYr * sqrtYr;
        double y = yr * Yn;
        double x = (lab.A * sqrtYr / HunterKaD65 + yr) * Xn;
        double z = (yr - lab.B * sqrtYr / HunterKbD65) * Zn;
        return new VipsColorXyz(x, y, z);
    }

    // ---------- RGB ↔ YCbCr (BT.601 / JPEG) ----------
    //
    // Standard JPEG (full-range BT.601). All channels in [0, 1]; Cb / Cr
    // are offset (0.5 = chromatic neutral).

    public static VipsColorYCbCr RgbToYCbCr(VipsColorRgb rgb)
    {
        double r = rgb.R, g = rgb.G, b = rgb.B;
        return new VipsColorYCbCr(
            0.299 * r + 0.587 * g + 0.114 * b,
            -0.168736 * r - 0.331264 * g + 0.5 * b + 0.5,
            0.5 * r - 0.418688 * g - 0.081312 * b + 0.5);
    }

    public static VipsColorRgb YCbCrToRgb(VipsColorYCbCr yc)
    {
        double y = yc.Y, cb = yc.Cb - 0.5, cr = yc.Cr - 0.5;
        return new VipsColorRgb(
            y + 1.402 * cr,
            y - 0.344136 * cb - 0.714136 * cr,
            y + 1.772 * cb);
    }

    /// <summary>
    /// Chromatic adaptation — convert <paramref name="xyz"/> assumed to
    /// be measured under <paramref name="from"/> reference white into
    /// the equivalent stimulus under <paramref name="to"/>. Pivots
    /// through LMS using the chosen adaptation matrix, scales by
    /// the ratio of source / target white-point LMS, pivots back.
    /// </summary>
    public static VipsColorXyz ChromaticAdapt(VipsColorXyz xyz, VipsWhitePoint from, VipsWhitePoint to,
        VipsLmsAdaptation method = VipsLmsAdaptation.Bradford)
    {
        if (from == to) return xyz;
        var fromW = WhitePointXyz(from);
        var toW = WhitePointXyz(to);
        var fLms = XyzToLms(fromW, method);
        var tLms = XyzToLms(toW, method);
        var lms = XyzToLms(xyz, method);
        var adapted = new VipsColorLms(
            lms.L * tLms.L / fLms.L,
            lms.M * tLms.M / fLms.M,
            lms.S * tLms.S / fLms.S);
        return LmsToXyz(adapted, method);
    }

    /// <summary>
    /// XYZ tristimulus values for the named reference white (Y=1).
    /// </summary>
    public static VipsColorXyz WhitePointXyz(VipsWhitePoint wp) => wp switch
    {
        VipsWhitePoint.D65 => new VipsColorXyz(0.95047, 1.0, 1.08883),
        VipsWhitePoint.D50 => new VipsColorXyz(0.96422, 1.0, 0.82521),
        VipsWhitePoint.D55 => new VipsColorXyz(0.95682, 1.0, 0.92149),
        VipsWhitePoint.D75 => new VipsColorXyz(0.94972, 1.0, 1.22638),
        VipsWhitePoint.A => new VipsColorXyz(1.09850, 1.0, 0.35585),
        VipsWhitePoint.E => new VipsColorXyz(1.0, 1.0, 1.0),
        _ => throw new ArgumentException($"Unknown white point: {wp}"),
    };

    // ---------- Generic dispatcher ----------

    /// <summary>
    /// Convert <paramref name="from"/> to color space <typeparamref name="TTo"/>.
    /// Routes through RGB (for HSL / HSV / CMYK) or XYZ (for Lab / Lch)
    /// to find the shortest direct path. Throws for unknown types.
    /// </summary>
    public static TTo Convert<TFrom, TTo>(TFrom from)
        where TFrom : struct
        where TTo : struct
    {
        // Identity
        if (typeof(TFrom) == typeof(TTo))
            return (TTo)(object)from;

        // Universal pivot: XYZ. sRGB-family round-trips through XYZ
        // are mathematically identity (within float precision).
        VipsColorXyz xyz = from switch
        {
            VipsColorRgb r => RgbToXyz(r),
            VipsColorHsl h => RgbToXyz(HslToRgb(h)),
            VipsColorHsv h => RgbToXyz(HsvToRgb(h)),
            VipsColorCmyk c => RgbToXyz(CmykToRgb(c)),
            VipsColorYCbCr y => RgbToXyz(YCbCrToRgb(y)),
            VipsColorXyz x => x,
            VipsColorXyy x => XyyToXyz(x),
            VipsColorLab l => LabToXyz(l),
            VipsColorLch l => LabToXyz(LchToLab(l)),
            VipsColorLuv l => LuvToXyz(l),
            VipsColorLchuv l => LuvToXyz(LchuvToLuv(l)),
            VipsColorLms l => LmsToXyz(l),
            VipsColorHunterLab l => HunterLabToXyz(l),
            _ => throw new ArgumentException(
                $"Unsupported source color type: {typeof(TFrom).Name}"),
        };

        object result;
        if (typeof(TTo) == typeof(VipsColorXyz)) result = xyz;
        else if (typeof(TTo) == typeof(VipsColorRgb)) result = XyzToRgb(xyz);
        else if (typeof(TTo) == typeof(VipsColorHsl)) result = RgbToHsl(XyzToRgb(xyz));
        else if (typeof(TTo) == typeof(VipsColorHsv)) result = RgbToHsv(XyzToRgb(xyz));
        else if (typeof(TTo) == typeof(VipsColorCmyk)) result = RgbToCmyk(XyzToRgb(xyz));
        else if (typeof(TTo) == typeof(VipsColorYCbCr)) result = RgbToYCbCr(XyzToRgb(xyz));
        else if (typeof(TTo) == typeof(VipsColorXyy)) result = XyzToXyy(xyz);
        else if (typeof(TTo) == typeof(VipsColorLab)) result = XyzToLab(xyz);
        else if (typeof(TTo) == typeof(VipsColorLch)) result = LabToLch(XyzToLab(xyz));
        else if (typeof(TTo) == typeof(VipsColorLuv)) result = XyzToLuv(xyz);
        else if (typeof(TTo) == typeof(VipsColorLchuv)) result = LuvToLchuv(XyzToLuv(xyz));
        else if (typeof(TTo) == typeof(VipsColorLms)) result = XyzToLms(xyz);
        else if (typeof(TTo) == typeof(VipsColorHunterLab)) result = XyzToHunterLab(xyz);
        else throw new ArgumentException(
            $"Unsupported target color type: {typeof(TTo).Name}");

        return (TTo)result;
    }

    // ---------- Helpers ----------

    private static double HueFromRgb(double r, double g, double b, double max, double c)
    {
        double h;
        if (max == r) h = ((g - b) / c) % 6;
        else if (max == g) h = (b - r) / c + 2;
        else h = (r - g) / c + 4;
        h *= 60;
        if (h < 0) h += 360;
        return h;
    }

    /// <summary>Build RGB from a hue (degrees) + chroma + offset (the HSL/HSV "m" term).</summary>
    private static VipsColorRgb RgbFromHueChroma(double h, double c, double m)
    {
        h = h % 360;
        if (h < 0) h += 360;
        double hp = h / 60;
        double x = c * (1 - Math.Abs(hp % 2 - 1));
        double r1, g1, b1;
        if (hp < 1) { r1 = c; g1 = x; b1 = 0; }
        else if (hp < 2) { r1 = x; g1 = c; b1 = 0; }
        else if (hp < 3) { r1 = 0; g1 = c; b1 = x; }
        else if (hp < 4) { r1 = 0; g1 = x; b1 = c; }
        else if (hp < 5) { r1 = x; g1 = 0; b1 = c; }
        else { r1 = c; g1 = 0; b1 = x; }
        return new VipsColorRgb(r1 + m, g1 + m, b1 + m);
    }

    private static double SrgbToLinear(double c)
        => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    private static double LinearToSrgb(double c)
        => c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1.0 / 2.4) - 0.055;
}
