using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Color;

/// <summary>
/// Adjust HSL Lightness. Distinct from <see cref="VipsImageOps.Brightness"/>
/// (multiplicative scale of RGB values): Lightness shifts the L axis in HSL
/// space, preserving hue and saturation. <paramref name="Amount"/> in -1..+1:
/// negative darkens, positive brightens. 0 is identity. ±1 saturates to
/// pure black / pure white.
///
/// Per-pixel RGB↔HSL conversion; alpha pass-through. For 1-band grayscale
/// inputs the L axis is just the pixel value itself.
/// </summary>
public class VipsLightness : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }

    /// <summary>HSL L delta. Range -1..+1.</summary>
    public double Amount { get; set; }

    public override int Build()
    {
        if (In == null) return -1;
        if (Amount < -1 || Amount > 1) return -1;

        Out = new VipsImage
        {
            Width = In.Width,
            Height = In.Height,
            Bands = In.Bands,
            BandFormat = In.BandFormat,
            Interpretation = In.Interpretation,
            Coding = In.Coding,
            XRes = In.XRes,
            YRes = In.YRes,
            StartFn = VipsSeq.StartOne,
            GenerateFn = Generate,
            StopFn = VipsSeq.StopOne,
            ClientA = In,
            ClientB = Amount
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Lightness", RuntimeHelpers.GetHashCode(In), Amount);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        double amount = (double)b!;
        VipsRect r = outRegion.Valid;
        if (inRegion.Prepare(r) != 0) return -1;

        int bands = @in.Bands;
        int pelSize = @in.SizeOfPel;
        bool hasAlpha = bands == 2 || bands == 4;
        int colorBands = hasAlpha ? bands - 1 : bands;

        for (int y = 0; y < r.Height; y++)
        {
            var inAddr = inRegion.GetAddress(r.Left, r.Top + y);
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);

            for (int x = 0; x < r.Width; x++)
            {
                int o = x * pelSize;

                if (colorBands == 1)
                {
                    // Grayscale: L axis is the pixel value itself.
                    double L = inAddr[o] / 255.0;
                    L = Math.Clamp(L + amount, 0, 1);
                    outAddr[o] = (byte)(L * 255 + 0.5);
                }
                else
                {
                    double R = inAddr[o + 0] / 255.0;
                    double G = inAddr[o + 1] / 255.0;
                    double B = inAddr[o + 2] / 255.0;

                    RgbToHsl(R, G, B, out double h, out double s, out double l);
                    l = Math.Clamp(l + amount, 0, 1);
                    HslToRgb(h, s, l, out R, out G, out B);

                    outAddr[o + 0] = (byte)Math.Clamp(R * 255 + 0.5, 0, 255);
                    outAddr[o + 1] = (byte)Math.Clamp(G * 255 + 0.5, 0, 255);
                    outAddr[o + 2] = (byte)Math.Clamp(B * 255 + 0.5, 0, 255);
                }

                if (hasAlpha) outAddr[o + colorBands] = inAddr[o + colorBands];
            }
        }
        return 0;
    }

    private static void RgbToHsl(double r, double g, double b, out double h, out double s, out double l)
    {
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        l = (max + min) * 0.5;

        if (max == min)
        {
            h = 0; s = 0;
            return;
        }

        double d = max - min;
        s = l > 0.5 ? d / (2 - max - min) : d / (max + min);

        if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
        else if (max == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        h /= 6;
    }

    private static void HslToRgb(double h, double s, double l, out double r, out double g, out double b)
    {
        if (s == 0)
        {
            r = g = b = l;
            return;
        }

        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;
        r = HueToRgb(p, q, h + 1.0 / 3);
        g = HueToRgb(p, q, h);
        b = HueToRgb(p, q, h - 1.0 / 3);
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 0.5) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }
}
