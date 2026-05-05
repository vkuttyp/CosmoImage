using System;
using CosmoImage.Operations.Drawing;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 170 — Porter-Duff composite mode coverage. The legacy
/// <c>VipsComposite</c> only supported Over; this round adds the full
/// Porter-Duff (1984) family plus Plus/Add. Each test pins a single mode
/// against a tightly-controlled 1×1 RGBA fixture so the per-pixel
/// arithmetic is verifiable by hand.
///
/// Fixture: src=(200,200,200,128), dst=(100,100,100,200) — both
/// non-trivial alphas, distinct colours. Expected values use the standard
/// premultiplied formula <c>Co = (Fa·αs·Cs + Fb·αd·Cd) / αo</c>.
/// </summary>
public class Round170Tests
{
    private const byte Sr = 200, Sg = 200, Sb = 200, Sa = 128; // ≈ 0.5019
    private const byte Dr = 100, Dg = 100, Db = 100, Da = 200; // ≈ 0.7843

    private static VipsImage Rgba(int w, int h, byte r, byte g, byte b, byte a) =>
        new VipsImage
        {
            Width = w, Height = h, Bands = 4, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? aArg, object? bArg, ref bool stop) =>
            {
                for (int yy = 0; yy < reg.Valid.Height; yy++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + yy);
                    for (int xx = 0; xx < reg.Valid.Width; xx++)
                    {
                        addr[xx * 4 + 0] = r;
                        addr[xx * 4 + 1] = g;
                        addr[xx * 4 + 2] = b;
                        addr[xx * 4 + 3] = a;
                    }
                }
                return 0;
            }
        };

    private static (byte R, byte G, byte B, byte A) ReadPixel(VipsImage img, int x, int y)
    {
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, img.Width, img.Height));
        var addr = reg.GetAddress(x, y);
        return (addr[0], addr[1], addr[2], addr[3]);
    }

    /// <summary>
    /// Reference Porter-Duff computation: returns the expected (R, G, B, A)
    /// in 0..255 byte range for the fixture's per-pixel maths.
    /// </summary>
    private static (byte R, byte G, byte B, byte A) Expect(VipsCompositeMode mode)
    {
        double aS = Sa / 255.0, aD = Da / 255.0;
        double cS = Sr / 255.0, cD = Dr / 255.0; // single channel — R/G/B all identical
        double Fa, Fb;
        switch (mode)
        {
            case VipsCompositeMode.Clear:    Fa = 0;       Fb = 0;       break;
            case VipsCompositeMode.Source:   Fa = 1;       Fb = 0;       break;
            case VipsCompositeMode.Dest:     Fa = 0;       Fb = 1;       break;
            case VipsCompositeMode.Over:     Fa = 1;       Fb = 1 - aS;  break;
            case VipsCompositeMode.DestOver: Fa = 1 - aD;  Fb = 1;       break;
            case VipsCompositeMode.In:       Fa = aD;      Fb = 0;       break;
            case VipsCompositeMode.DestIn:   Fa = 0;       Fb = aS;      break;
            case VipsCompositeMode.Out:      Fa = 1 - aD;  Fb = 0;       break;
            case VipsCompositeMode.DestOut:  Fa = 0;       Fb = 1 - aS;  break;
            case VipsCompositeMode.Atop:     Fa = aD;      Fb = 1 - aS;  break;
            case VipsCompositeMode.DestAtop: Fa = 1 - aD;  Fb = aS;      break;
            case VipsCompositeMode.Xor:      Fa = 1 - aD;  Fb = 1 - aS;  break;
            case VipsCompositeMode.Add:
                {
                    double co = Math.Min(1, aS * cS + aD * cD);
                    double ao = Math.Min(1, aS + aD);
                    byte cb = (byte)Math.Round(co * 255);
                    byte ab = (byte)Math.Round(ao * 255);
                    return (cb, cb, cb, ab);
                }
            default: Fa = 1; Fb = 1 - aS; break;
        }
        double aO = Fa * aS + Fb * aD;
        double cOPremult = Fa * aS * cS + Fb * aD * cD;
        double cOO = aO > 0 ? cOPremult / aO : 0;
        byte color = (byte)Math.Round(Math.Clamp(cOO, 0, 1) * 255);
        byte alpha = (byte)Math.Round(Math.Clamp(aO, 0, 1) * 255);
        return (color, color, color, alpha);
    }

    [Theory]
    [InlineData(VipsCompositeMode.Clear)]
    [InlineData(VipsCompositeMode.Source)]
    [InlineData(VipsCompositeMode.Dest)]
    [InlineData(VipsCompositeMode.Over)]
    [InlineData(VipsCompositeMode.DestOver)]
    [InlineData(VipsCompositeMode.In)]
    [InlineData(VipsCompositeMode.DestIn)]
    [InlineData(VipsCompositeMode.Out)]
    [InlineData(VipsCompositeMode.DestOut)]
    [InlineData(VipsCompositeMode.Atop)]
    [InlineData(VipsCompositeMode.DestAtop)]
    [InlineData(VipsCompositeMode.Xor)]
    [InlineData(VipsCompositeMode.Add)]
    public void EachMode_Matches_PorterDuffReference(VipsCompositeMode mode)
    {
        var dst = Rgba(1, 1, Dr, Dg, Db, Da);
        var src = Rgba(1, 1, Sr, Sg, Sb, Sa);
        var result = dst.Composite(src, 0, 0, mode);

        var (r, g, b, a) = ReadPixel(result, 0, 0);
        var (er, eg, eb, ea) = Expect(mode);

        // Allow ±1 tolerance for float-rounding noise across the two
        // distinct rounding paths (impl premultiplies once internally; the
        // reference does the same arithmetic in doubles).
        Assert.InRange(r - er, -1, 1);
        Assert.InRange(g - eg, -1, 1);
        Assert.InRange(b - eb, -1, 1);
        Assert.InRange(a - ea, -1, 1);
    }

    /// <summary>
    /// Modes whose source-empty regions are "transparent" (Clear, Source, In,
    /// Out, DestIn, DestAtop) clear the dst outside the overlay rectangle.
    /// All other modes leave dst pixels there untouched.
    /// </summary>
    [Theory]
    [InlineData(VipsCompositeMode.Clear,    0)]
    [InlineData(VipsCompositeMode.Source,   0)]
    [InlineData(VipsCompositeMode.In,       0)]
    [InlineData(VipsCompositeMode.Out,      0)]
    [InlineData(VipsCompositeMode.DestIn,   0)]
    [InlineData(VipsCompositeMode.DestAtop, 0)]
    [InlineData(VipsCompositeMode.Over,     Dr)]
    [InlineData(VipsCompositeMode.DestOver, Dr)]
    [InlineData(VipsCompositeMode.DestOut,  Dr)]
    [InlineData(VipsCompositeMode.Atop,     Dr)]
    [InlineData(VipsCompositeMode.Xor,      Dr)]
    public void NonOverlap_OutsideOverlay_BehavesPerMode(VipsCompositeMode mode, byte expectedRedOutside)
    {
        // 4×4 dst with a 1×1 overlay at (0,0). Pixel (3,3) lies outside the overlay.
        var dst = Rgba(4, 4, Dr, Dg, Db, Da);
        var src = Rgba(1, 1, Sr, Sg, Sb, Sa);
        var result = dst.Composite(src, 0, 0, mode);

        var (r, _, _, _) = ReadPixel(result, 3, 3);
        Assert.Equal(expectedRedOutside, r);
    }
}
