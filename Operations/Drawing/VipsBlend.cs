using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Drawing;

/// <summary>
/// Blend modes used by <see cref="VipsBlend"/>. The mathematical
/// definitions match ImageSharp's <c>PixelColorBlendingMode</c>
/// (which in turn follows the W3C Compositing and Blending spec).
/// All modes operate per-channel on UChar values; the result is
/// alpha-blended onto the base using source-over.
/// </summary>
public enum VipsBlendMode
{
    /// <summary>Standard source-over (no per-channel blend).</summary>
    Normal = 0,
    Multiply,
    Screen,
    Overlay,
    Darken,
    Lighten,
    HardLight,
    SoftLight,
    Difference,
    Exclusion,
    /// <summary>Channel-wise sum, clamp at 255.</summary>
    Add,
    /// <summary>base − overlay, clamp at 0.</summary>
    Subtract,
    ColorDodge,
}

/// <summary>
/// Per-pixel blended composite: paints <see cref="Overlay"/> onto
/// <see cref="Base"/> at (<see cref="X"/>, <see cref="Y"/>) using
/// the chosen <see cref="Mode"/> and <see cref="Opacity"/>. Mirrors
/// ImageSharp's <c>DrawImage(source, location, opacity, blendMode)</c>.
///
/// <para>Both inputs must be UChar 3- or 4-band. The base may be RGB
/// (3-band) — in that case the overlay's alpha is used for blending
/// only, the base is treated as fully opaque. Output keeps the
/// base's band count and format.</para>
///
/// <para>Out-of-bounds overlay pixels (when X / Y push the overlay
/// off the base) are simply skipped — the base shows through
/// untouched there.</para>
/// </summary>
public class VipsBlend : VipsOperation
{
    public VipsImage? Base { get; set; }
    public VipsImage? Overlay { get; set; }
    public VipsImage? Out { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public VipsBlendMode Mode { get; set; } = VipsBlendMode.Normal;
    /// <summary>Per-blend opacity multiplier (0..1).</summary>
    public double Opacity { get; set; } = 1.0;

    public override int Build()
    {
        if (Base == null || Overlay == null) return -1;
        if (Base.BandFormat != VipsBandFormat.UChar) return -1;
        if (Overlay.BandFormat != VipsBandFormat.UChar) return -1;
        if (Base.Bands != 3 && Base.Bands != 4) return -1;
        if (Overlay.Bands != 3 && Overlay.Bands != 4) return -1;
        if (Opacity < 0 || Opacity > 1) return -1;

        Out = new VipsImage
        {
            Width = Base.Width, Height = Base.Height,
            Bands = Base.Bands, BandFormat = VipsBandFormat.UChar,
            Interpretation = Base.Interpretation,
            Coding = Base.Coding, XRes = Base.XRes, YRes = Base.YRes,
            StartFn = VipsSeq.StartMany, GenerateFn = Generate, StopFn = VipsSeq.StopMany,
            ClientA = new[] { Base, Overlay },
            ClientB = (X, Y, Mode, Opacity),
        };
        Out.CopyMetadataFrom(Base);
        Out.SetPipeline(VipsDemandStyle.Any, Base, Overlay);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Blend", RuntimeHelpers.GetHashCode(Base),
            RuntimeHelpers.GetHashCode(Overlay), X, Y, Mode, Opacity);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var regions = (VipsRegion[])seq!;
        var (px, py, mode, opacity) = ((int, int, VipsBlendMode, double))b!;
        var baseReg = regions[0];
        var ovReg = regions[1];
        VipsImage baseImg = baseReg.Image;
        VipsImage ovImg = ovReg.Image;
        VipsRect r = outRegion.Valid;

        if (baseReg.Prepare(r) != 0) return -1;
        int baseBands = baseImg.Bands;

        // Copy base verbatim first.
        int rowBytes = r.Width * baseBands;
        for (int y = 0; y < r.Height; y++)
            baseReg.GetAddress(r.Left, r.Top + y).Slice(0, rowBytes)
                .CopyTo(outRegion.GetAddress(r.Left, r.Top + y));

        // Blend overlay where it overlaps.
        int x0 = Math.Max(r.Left, px);
        int y0 = Math.Max(r.Top, py);
        int x1 = Math.Min(r.Left + r.Width, px + ovImg.Width);
        int y1 = Math.Min(r.Top + r.Height, py + ovImg.Height);
        if (x0 >= x1 || y0 >= y1) return 0;
        var ovRect = new VipsRect(x0 - px, y0 - py, x1 - x0, y1 - y0);
        if (ovReg.Prepare(ovRect) != 0) return -1;

        int ovBands = ovImg.Bands;
        for (int sy = 0; sy < ovRect.Height; sy++)
        {
            var ovAddr = ovReg.GetAddress(ovRect.Left, ovRect.Top + sy);
            var outAddr = outRegion.GetAddress(x0, y0 + sy);
            for (int sx = 0; sx < ovRect.Width; sx++)
            {
                int ovOff = sx * ovBands;
                int baseOff = sx * baseBands;
                byte oR = ovAddr[ovOff + 0];
                byte oG = ovBands > 1 ? ovAddr[ovOff + 1] : oR;
                byte oB = ovBands > 2 ? ovAddr[ovOff + 2] : oR;
                byte oA = ovBands == 4 ? ovAddr[ovOff + 3] : (byte)255;
                // Apply opacity multiplier to overlay alpha.
                double aOver = oA / 255.0 * opacity;

                byte bR = outAddr[baseOff + 0];
                byte bG = outAddr[baseOff + 1];
                byte bB = outAddr[baseOff + 2];

                // Per-channel blend; result is the blended overlay colour.
                byte sR = Blend(bR, oR, mode);
                byte sG = Blend(bG, oG, mode);
                byte sB = Blend(bB, oB, mode);

                // Source-over composite: out = blend·a + base·(1−a).
                outAddr[baseOff + 0] = (byte)Math.Round(sR * aOver + bR * (1 - aOver));
                outAddr[baseOff + 1] = (byte)Math.Round(sG * aOver + bG * (1 - aOver));
                outAddr[baseOff + 2] = (byte)Math.Round(sB * aOver + bB * (1 - aOver));
                if (baseBands == 4)
                {
                    // Alpha out = aOver + base.alpha · (1 − aOver).
                    double bAfrac = outAddr[baseOff + 3] / 255.0;
                    double aOut = aOver + bAfrac * (1 - aOver);
                    outAddr[baseOff + 3] = (byte)Math.Clamp(Math.Round(aOut * 255), 0, 255);
                }
            }
        }
        return 0;
    }

    /// <summary>Per-channel blend math (8-bit). Inputs / output are 0..255.</summary>
    private static byte Blend(byte b, byte s, VipsBlendMode mode)
    {
        // Many formulas are simpler in [0, 1]; convert and back at the ends.
        double db = b / 255.0, ds = s / 255.0;
        double r = mode switch
        {
            VipsBlendMode.Normal => ds,
            VipsBlendMode.Multiply => db * ds,
            VipsBlendMode.Screen => 1 - (1 - db) * (1 - ds),
            VipsBlendMode.Overlay => db < 0.5 ? 2 * db * ds : 1 - 2 * (1 - db) * (1 - ds),
            VipsBlendMode.Darken => Math.Min(db, ds),
            VipsBlendMode.Lighten => Math.Max(db, ds),
            VipsBlendMode.HardLight => ds < 0.5 ? 2 * db * ds : 1 - 2 * (1 - db) * (1 - ds),
            VipsBlendMode.SoftLight => SoftLight(db, ds),
            VipsBlendMode.Difference => Math.Abs(db - ds),
            VipsBlendMode.Exclusion => db + ds - 2 * db * ds,
            VipsBlendMode.Add => Math.Min(1, db + ds),
            VipsBlendMode.Subtract => Math.Max(0, db - ds),
            VipsBlendMode.ColorDodge => ds >= 1 ? 1 : Math.Min(1, db / (1 - ds)),
            _ => ds,
        };
        return (byte)Math.Clamp(Math.Round(r * 255), 0, 255);
    }

    /// <summary>Pegtop soft-light formula (smooth, no W3C-spec discontinuity).</summary>
    private static double SoftLight(double b, double s)
        => (1 - 2 * s) * b * b + 2 * s * b;
}
