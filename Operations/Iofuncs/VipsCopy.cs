using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Iofuncs;

/// <summary>
/// Stream the input through verbatim, but with optional metadata
/// rewrites. Mirrors libvips <c>vips_copy</c> — the canonical "fix
/// header" op for retagging an image's interpretation, x/y
/// resolution, bands count, or band format without touching the
/// pixels.
///
/// <para>Common uses: an sRGB-ish 3-band image arrives tagged
/// Multiband; <see cref="Copy"/> fixes <see cref="Interpretation"/>.
/// Or a saver wants <c>XRes</c> = 300 dpi but the loaded image
/// reported 72 dpi — Copy adjusts the metadata before save without a
/// full pipeline rerun.</para>
///
/// <para>Pixel data is unchanged — band-format / band-count rewrites
/// reinterpret the same bytes (e.g. 4-band UChar reinterpret as
/// 1-band UInt). The op refuses if the new
/// <c>SizeOfPel × Width × Height</c> doesn't match the input.</para>
/// </summary>
public class VipsCopy : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    /// <summary>Override interpretation; null = inherit.</summary>
    public VipsInterpretation? Interpretation { get; set; }
    /// <summary>Override band format; null = inherit. Reinterprets bytes — pel size must still match.</summary>
    public VipsBandFormat? BandFormat { get; set; }
    /// <summary>Override band count; null = inherit.</summary>
    public int? Bands { get; set; }
    public double? XRes { get; set; }
    public double? YRes { get; set; }
    public VipsCoding? Coding { get; set; }

    public override int Build()
    {
        if (In == null) return -1;

        var newFormat = BandFormat ?? In.BandFormat;
        int newBands = Bands ?? In.Bands;
        int newPelSize = newBands * VipsEnumsExtensions.SizeOf(newFormat);
        if (newPelSize != In.SizeOfPel) return -1;

        Out = new VipsImage
        {
            Width = In.Width, Height = In.Height,
            Bands = newBands, BandFormat = newFormat,
            Interpretation = Interpretation ?? In.Interpretation,
            Coding = Coding ?? In.Coding,
            XRes = XRes ?? In.XRes, YRes = YRes ?? In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Copy", RuntimeHelpers.GetHashCode(In),
            Interpretation, BandFormat, Bands, XRes, YRes, Coding);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inReg = (VipsRegion)seq!;
        VipsRect r = outRegion.Valid;
        if (inReg.Prepare(r) != 0) return -1;

        // Pel size matches by construction in Build, so byte-for-byte copy.
        int rowBytes = r.Width * outRegion.Image.SizeOfPel;
        for (int y = 0; y < r.Height; y++)
        {
            inReg.GetAddress(r.Left, r.Top + y).Slice(0, rowBytes)
                .CopyTo(outRegion.GetAddress(r.Left, r.Top + y));
        }
        return 0;
    }
}
