using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Drawing;

/// <summary>
/// Alpha-only composite modes mirroring libvips' <c>VipsBlendMode</c>
/// (Porter-Duff 1984 + Plus). Each mode picks coefficients (Fa, Fb)
/// for the per-pixel formula
/// <code>
///   αo = Fa·αs + Fb·αd
///   Co (premultiplied) = Fa·αs·Cs + Fb·αd·Cd
///   out.color = Co / αo  (when αo &gt; 0; else 0)
///   out.alpha = αo
/// </code>
/// where Cs/Cd are the unpremultiplied source / destination colour
/// channels and αs/αd are their alphas. Modes that depend on αd treat
/// a missing destination alpha as <c>1</c> (RGB / greyscale base).
/// Color-modulation modes (Multiply, Screen, …) live on
/// <see cref="VipsBlend"/> — they affect colour, not alpha geometry.
/// </summary>
public enum VipsCompositeMode
{
    /// <summary>Result is fully transparent everywhere. (Fa=0, Fb=0)</summary>
    Clear = 0,
    /// <summary>Result is the source. (Fa=1, Fb=0) — destination is replaced.</summary>
    Source,
    /// <summary>Result is the destination. (Fa=0, Fb=1) — source is ignored.</summary>
    Dest,
    /// <summary>Source-over-destination. (Fa=1, Fb=1−αs) — default mode.</summary>
    Over,
    /// <summary>Destination-over-source. (Fa=1−αd, Fb=1)</summary>
    DestOver,
    /// <summary>Source clipped to destination's alpha. (Fa=αd, Fb=0)</summary>
    In,
    /// <summary>Destination clipped to source's alpha. (Fa=0, Fb=αs)</summary>
    DestIn,
    /// <summary>Source where destination is transparent. (Fa=1−αd, Fb=0)</summary>
    Out,
    /// <summary>Destination where source is transparent. (Fa=0, Fb=1−αs)</summary>
    DestOut,
    /// <summary>Source-over but only inside destination's alpha. (Fa=αd, Fb=1−αs)</summary>
    Atop,
    /// <summary>Destination-over but only inside source's alpha. (Fa=1−αd, Fb=αs)</summary>
    DestAtop,
    /// <summary>Symmetric difference of the alpha regions. (Fa=1−αd, Fb=1−αs)</summary>
    Xor,
    /// <summary>Plus / additive blending. Output is clamped to [0,1] post-add.</summary>
    Add,
}

public class VipsComposite : VipsOperation
{
    public VipsImage? Base { get; set; }
    public VipsImage? Overlay { get; set; }
    public VipsImage? Out { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public VipsCompositeMode Mode { get; set; } = VipsCompositeMode.Over;

    public override int Build()
    {
        if (Base == null || Overlay == null) return -1;

        Out = new VipsImage
        {
            Width = Base.Width,
            Height = Base.Height,
            Bands = Base.Bands,
            BandFormat = Base.BandFormat,
            Interpretation = Base.Interpretation,
            Coding = Base.Coding,
            XRes = Base.XRes,
            YRes = Base.YRes,
            StartFn = VipsSeq.StartMany,
            GenerateFn = Generate,
            StopFn = VipsSeq.StopMany,
            ClientA = new[] { Base, Overlay },
            ClientB = (X, Y, Mode),
        };
        Out.CopyMetadataFrom(Base);
        Out.SetPipeline(VipsDemandStyle.Any, Base, Overlay);

        return 0;
    }

    public override int GetCacheKey()
    {
        return HashCode.Combine("Composite",
            RuntimeHelpers.GetHashCode(Base), RuntimeHelpers.GetHashCode(Overlay),
            X, Y, Mode);
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var regions = (VipsRegion[])seq!;
        var baseRegion = regions[0];
        var overlayRegion = regions[1];
        VipsImage @base = baseRegion.Image;
        VipsImage overlay = overlayRegion.Image;
        var (ox, oy, mode) = ((int, int, VipsCompositeMode))b!;
        VipsRect r = outRegion.Valid;

        if (baseRegion.Prepare(r) != 0) return -1;

        VipsRect overlayRect = new VipsRect(ox, oy, overlay.Width, overlay.Height);
        VipsRect overlap = VipsRect.Intersect(r, overlayRect);

        bool isFloat = @base.BandFormat == VipsBandFormat.Float;
        int pelSize = @base.SizeOfPel;

        // For modes whose Fb only depends on αs (Source / Clear / Out /
        // DestOut), pixels of the base outside the overlay get specific
        // treatment: αs=0 there, so Fb·dst is the formula. We start from
        // a base-copy and then rewrite the *non-overlap* region for
        // those two modes; for all other modes leaving base verbatim is
        // already correct (αs=0 means Co=Fb·Cd with Fb=1, i.e. dst).
        for (int y = 0; y < r.Height; y++)
        {
            baseRegion.GetAddress(r.Left, r.Top + y).Slice(0, r.Width * pelSize)
                .CopyTo(outRegion.GetAddress(r.Left, r.Top + y));
        }

        // Modes where source-empty regions don't pass through dst as-is
        // need a full-region rewrite (everything outside the overlay
        // sees αs=0 → out = (Fb at αs=0)·Cd). Clear/Source/In/Out
        // collapse outside the overlay; DestIn/DestAtop become
        // mostly-transparent.
        bool needsFullRegionRewrite = mode == VipsCompositeMode.Clear ||
                                       mode == VipsCompositeMode.Source ||
                                       mode == VipsCompositeMode.In ||
                                       mode == VipsCompositeMode.Out ||
                                       mode == VipsCompositeMode.DestIn ||
                                       mode == VipsCompositeMode.DestAtop;
        if (needsFullRegionRewrite)
        {
            // Outside the overlay: src is absent (αs=0). Apply mode to
            // dst-only pixels.
            ApplyToNonOverlap(outRegion, r, overlap, mode, isFloat, @base.Bands, pelSize);
        }

        if (overlap.IsEmpty) return 0;

        VipsRect overlayRequest = new VipsRect(overlap.Left - ox, overlap.Top - oy, overlap.Width, overlap.Height);
        if (overlayRegion.Prepare(overlayRequest) != 0) return -1;

        if (isFloat)
            return BlendFloat(outRegion, overlayRegion, overlap, overlayRequest, mode, @base.Bands, overlay.Bands);
        else
            return BlendUChar(outRegion, overlayRegion, overlap, overlayRequest, mode, @base.Bands, overlay.Bands);
    }

    /// <summary>Rewrite the part of the output that the overlay does NOT cover (αs=0 there).</summary>
    private static void ApplyToNonOverlap(VipsRegion outRegion, VipsRect r, VipsRect overlap,
        VipsCompositeMode mode, bool isFloat, int bands, int pelSize)
    {
        for (int y = r.Top; y < r.Top + r.Height; y++)
        {
            for (int x = r.Left; x < r.Left + r.Width; x++)
            {
                if (!overlap.IsEmpty &&
                    x >= overlap.Left && x < overlap.Left + overlap.Width &&
                    y >= overlap.Top && y < overlap.Top + overlap.Height)
                    continue; // inside overlay — handled by main blend loop
                ApplyEmptySrc(outRegion, x, y, mode, isFloat, bands, pelSize);
            }
        }
    }

    /// <summary>Apply mode to a single destination pixel where αs=0 (source absent).</summary>
    private static void ApplyEmptySrc(VipsRegion outRegion, int x, int y,
        VipsCompositeMode mode, bool isFloat, int bands, int pelSize)
    {
        // αs=0 makes Fa irrelevant. Fb at αs=0 gives the dst-multiplier:
        //   Clear:    Fb=0       → all zero
        //   Source:   Fb=0       → all zero
        //   Dest:     Fb=1       → unchanged (caller doesn't reach here for Dest)
        //   Over:     Fb=1       → unchanged
        //   In:       Fb=0       → all zero
        //   DestIn:   Fb=αs=0    → all zero
        //   Out:      Fb=0       → all zero
        //   DestOut:  Fb=1−αs=1  → unchanged
        //   Atop:     Fb=1−αs=1  → unchanged
        //   DestAtop: Fb=αs=0    → all zero
        //   Xor:      Fb=1−αs=1  → unchanged
        //   Add:      Fb=αd      → α·dst (= dst since coeff is 1·dst·αd / αo)
        bool zeroOut = mode == VipsCompositeMode.Clear ||
                       mode == VipsCompositeMode.Source ||
                       mode == VipsCompositeMode.In ||
                       mode == VipsCompositeMode.Out ||
                       mode == VipsCompositeMode.DestIn ||
                       mode == VipsCompositeMode.DestAtop;
        if (!zeroOut) return;

        var addr = outRegion.GetAddress(x, y);
        if (isFloat)
            addr.Slice(0, pelSize).Clear();
        else
            addr.Slice(0, bands).Clear();
    }

    /// <summary>UChar Porter-Duff blend over the (overlap) region.</summary>
    private static int BlendUChar(VipsRegion outRegion, VipsRegion overlayRegion,
        VipsRect overlap, VipsRect overlayRequest, VipsCompositeMode mode, int baseBands, int overlayBands)
    {
        bool baseHasAlpha = baseBands == 2 || baseBands == 4;
        bool overHasAlpha = overlayBands == 2 || overlayBands == 4;
        int baseAlphaIdx = baseBands - 1;
        int overAlphaIdx = overlayBands - 1;
        int colorBands = Math.Min(baseBands - (baseHasAlpha ? 1 : 0),
                                   overlayBands - (overHasAlpha ? 1 : 0));

        for (int y = 0; y < overlap.Height; y++)
        {
            var srcAddr = overlayRegion.GetAddress(overlayRequest.Left, overlayRequest.Top + y);
            var destAddr = outRegion.GetAddress(overlap.Left, overlap.Top + y);

            for (int x = 0; x < overlap.Width; x++)
            {
                int ovOff = x * overlayBands;
                int baseOff = x * baseBands;
                double aS = overHasAlpha ? srcAddr[ovOff + overAlphaIdx] / 255.0 : 1.0;
                double aD = baseHasAlpha ? destAddr[baseOff + baseAlphaIdx] / 255.0 : 1.0;
                ComputePorterDuffCoefficients(mode, aS, aD, out double Fa, out double Fb);

                if (mode == VipsCompositeMode.Add)
                {
                    // Add/Plus: direct premultiplied sum, clamp to [0,1].
                    for (int i = 0; i < colorBands; i++)
                    {
                        double cS = srcAddr[ovOff + i] / 255.0;
                        double cD = destAddr[baseOff + i] / 255.0;
                        double co = Math.Min(1, aS * cS + aD * cD);
                        destAddr[baseOff + i] = (byte)Math.Round(co * 255);
                    }
                    if (baseHasAlpha)
                        destAddr[baseOff + baseAlphaIdx] = (byte)Math.Round(Math.Min(1, aS + aD) * 255);
                    continue;
                }

                double aO = Fa * aS + Fb * aD;
                for (int i = 0; i < colorBands; i++)
                {
                    double cS = srcAddr[ovOff + i] / 255.0;
                    double cD = destAddr[baseOff + i] / 255.0;
                    double coPremult = Fa * aS * cS + Fb * aD * cD;
                    double co = aO > 0 ? coPremult / aO : 0;
                    destAddr[baseOff + i] = (byte)Math.Round(Math.Clamp(co, 0, 1) * 255);
                }
                if (baseHasAlpha)
                    destAddr[baseOff + baseAlphaIdx] = (byte)Math.Round(Math.Clamp(aO, 0, 1) * 255);
            }
        }
        return 0;
    }

    /// <summary>Float Porter-Duff blend; alpha is nominal [0,1].</summary>
    private static int BlendFloat(VipsRegion outRegion, VipsRegion overlayRegion,
        VipsRect overlap, VipsRect overlayRequest, VipsCompositeMode mode, int baseBands, int overlayBands)
    {
        bool baseHasAlpha = baseBands == 2 || baseBands == 4;
        bool overHasAlpha = overlayBands == 2 || overlayBands == 4;
        int baseAlphaIdx = baseBands - 1;
        int overAlphaIdx = overlayBands - 1;
        int colorBands = Math.Min(baseBands - (baseHasAlpha ? 1 : 0),
                                   overlayBands - (overHasAlpha ? 1 : 0));

        for (int y = 0; y < overlap.Height; y++)
        {
            var srcAddr = overlayRegion.GetAddress(overlayRequest.Left, overlayRequest.Top + y);
            var destAddr = outRegion.GetAddress(overlap.Left, overlap.Top + y);

            for (int x = 0; x < overlap.Width; x++)
            {
                int ovOff = x * overlayBands * 4;
                int baseOff = x * baseBands * 4;
                double aS = overHasAlpha
                    ? BinaryPrimitives.ReadSingleLittleEndian(srcAddr.Slice(ovOff + overAlphaIdx * 4, 4))
                    : 1.0;
                double aD = baseHasAlpha
                    ? BinaryPrimitives.ReadSingleLittleEndian(destAddr.Slice(baseOff + baseAlphaIdx * 4, 4))
                    : 1.0;
                ComputePorterDuffCoefficients(mode, aS, aD, out double Fa, out double Fb);

                if (mode == VipsCompositeMode.Add)
                {
                    for (int i = 0; i < colorBands; i++)
                    {
                        double cS = BinaryPrimitives.ReadSingleLittleEndian(srcAddr.Slice(ovOff + i * 4, 4));
                        double cD = BinaryPrimitives.ReadSingleLittleEndian(destAddr.Slice(baseOff + i * 4, 4));
                        double co = Math.Min(1, aS * cS + aD * cD);
                        BinaryPrimitives.WriteSingleLittleEndian(destAddr.Slice(baseOff + i * 4, 4), (float)co);
                    }
                    if (baseHasAlpha)
                        BinaryPrimitives.WriteSingleLittleEndian(
                            destAddr.Slice(baseOff + baseAlphaIdx * 4, 4),
                            (float)Math.Min(1, aS + aD));
                    continue;
                }

                double aO = Fa * aS + Fb * aD;
                for (int i = 0; i < colorBands; i++)
                {
                    double cS = BinaryPrimitives.ReadSingleLittleEndian(srcAddr.Slice(ovOff + i * 4, 4));
                    double cD = BinaryPrimitives.ReadSingleLittleEndian(destAddr.Slice(baseOff + i * 4, 4));
                    double coPremult = Fa * aS * cS + Fb * aD * cD;
                    double co = aO > 0 ? coPremult / aO : 0;
                    BinaryPrimitives.WriteSingleLittleEndian(destAddr.Slice(baseOff + i * 4, 4), (float)co);
                }
                if (baseHasAlpha)
                    BinaryPrimitives.WriteSingleLittleEndian(
                        destAddr.Slice(baseOff + baseAlphaIdx * 4, 4),
                        (float)Math.Clamp(aO, 0, 1));
            }
        }
        return 0;
    }

    /// <summary>Porter-Duff (Fa, Fb) coefficient table, 1984 paper §5.</summary>
    private static void ComputePorterDuffCoefficients(VipsCompositeMode mode, double aS, double aD,
        out double Fa, out double Fb)
    {
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
            case VipsCompositeMode.Add:      Fa = 1;       Fb = 1;       break;
            default: Fa = 1; Fb = 1 - aS; break; // fallback = Over
        }
    }
}
