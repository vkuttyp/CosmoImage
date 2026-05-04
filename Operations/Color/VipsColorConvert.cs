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

        // 1. From → RGB (the "base" of HSL / HSV / CMYK / XYZ paths)
        VipsColorRgb rgb = from switch
        {
            VipsColorRgb r => r,
            VipsColorHsl h => HslToRgb(h),
            VipsColorHsv h => HsvToRgb(h),
            VipsColorCmyk c => CmykToRgb(c),
            VipsColorXyz x => XyzToRgb(x),
            VipsColorLab l => XyzToRgb(LabToXyz(l)),
            VipsColorLch l => XyzToRgb(LabToXyz(LchToLab(l))),
            _ => throw new ArgumentException(
                $"Unsupported source color type: {typeof(TFrom).Name}"),
        };

        // 2. RGB → target
        object result;
        if (typeof(TTo) == typeof(VipsColorRgb)) result = rgb;
        else if (typeof(TTo) == typeof(VipsColorHsl)) result = RgbToHsl(rgb);
        else if (typeof(TTo) == typeof(VipsColorHsv)) result = RgbToHsv(rgb);
        else if (typeof(TTo) == typeof(VipsColorCmyk)) result = RgbToCmyk(rgb);
        else if (typeof(TTo) == typeof(VipsColorXyz)) result = RgbToXyz(rgb);
        else if (typeof(TTo) == typeof(VipsColorLab)) result = XyzToLab(RgbToXyz(rgb));
        else if (typeof(TTo) == typeof(VipsColorLch)) result = LabToLch(XyzToLab(RgbToXyz(rgb)));
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
