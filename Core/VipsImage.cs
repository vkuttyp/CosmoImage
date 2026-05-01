using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("CosmoImage.Tests")]

namespace CosmoImage.Core;

public delegate int VipsGenerateFn(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop);
public delegate object? VipsStartFn(VipsImage @out, object? a, object? b);
public delegate int VipsStopFn(object? seq, object? a, object? b);

public class VipsImage : IDisposable
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int Bands { get; set; }
    public VipsBandFormat BandFormat { get; set; }
    public VipsCoding Coding { get; set; }
    public VipsInterpretation Interpretation { get; set; }
    public double XRes { get; set; }
    public double YRes { get; set; }
    public int XOffset { get; set; }
    public int YOffset { get; set; }

    /// <summary>
    /// Hint to the IO system about preferred tile geometry for this image.
    /// Coordinate-transforming ops (affine, resize) prefer SmallTile; window
    /// ops (conv) prefer FatStrip; pointwise ops are Any. Sinks consume this
    /// to choose a default tile shape. Mirrors libvips <c>VipsImage.dhint</c>.
    /// </summary>
    public VipsDemandStyle DemandHint { get; set; } = VipsDemandStyle.Any;

    internal VipsGenerateFn? GenerateFn { get; set; }
    internal VipsStartFn? StartFn { get; set; }
    internal VipsStopFn? StopFn { get; set; }
    internal object? ClientA { get; set; }
    internal object? ClientB { get; set; }

    /// <summary>
    /// Fully-materialized pixel buffer for this image. When set, this image is
    /// memory-backed: <see cref="VipsRegion.Prepare"/> aliases this buffer
    /// instead of running <see cref="GenerateFn"/>, giving zero-copy reads to
    /// downstream operations. Buffer layout is row-major, contiguous, with
    /// stride = <c>Width * SizeOfPel</c>; pixel (0,0) is at offset 0.
    /// Mirrors libvips <c>VIPS_IMAGE_SETBUF</c>. Lazy: materialization happens
    /// on first read, so loaders that decode-on-demand still pay the decode
    /// cost only when pixels are first needed.
    /// </summary>
    internal Lazy<byte[]>? PixelsLazy { get; set; }
    internal byte[]? Pixels => PixelsLazy?.Value;

    /// <summary>
    /// Set this image's demand hint to the most restrictive of <paramref name="hint"/>
    /// and the hints of <paramref name="inputs"/>. Called by operations during
    /// Build to propagate hint geometry along the pipeline. Direct port of
    /// libvips <c>vips__demand_hint_array</c>.
    /// </summary>
    public void SetPipeline(VipsDemandStyle hint, params VipsImage[] inputs)
    {
        VipsDemandStyle setHint = hint;
        foreach (var input in inputs)
        {
            if (input == null) continue;
            // Lower numeric value = more restrictive. SmallTile=0 is most
            // restrictive; Any=3 is least.
            if ((int)input.DemandHint < (int)setHint)
                setHint = input.DemandHint;
        }
        DemandHint = setHint;
    }

    public System.Collections.Generic.Dictionary<string, string> Metadata { get; } = new();

    /// <summary>
    /// Raw byte-array payloads associated with this image — typically the
    /// untouched EXIF/XMP/ICC segments captured by loaders and replayed by
    /// savers, so format-specific metadata round-trips losslessly. Keyed by
    /// canonical name: <c>"exif"</c>, <c>"xmp"</c>, <c>"icc"</c>.
    /// </summary>
    public System.Collections.Generic.Dictionary<string, byte[]> MetadataBlobs { get; } = new();

    /// <summary>
    /// Copy <paramref name="source"/>'s metadata entries into this image,
    /// overwriting any existing keys. Pixel-domain operations call this in
    /// Build so EXIF/XMP/ICC and other tags survive transformations like
    /// resize/rotate/colourspace. For multi-input ops, the first (primary)
    /// input wins on key collisions — matches libvips' "lower-numbered input
    /// takes priority" rule from <c>vips_image_pipeline_array</c>.
    /// </summary>
    public void CopyMetadataFrom(VipsImage source)
    {
        foreach (var kvp in source.Metadata)
            Metadata[kvp.Key] = kvp.Value;
        // Blobs are treated as immutable, so sharing the reference is safe and
        // avoids copying potentially large byte arrays through every op.
        foreach (var kvp in source.MetadataBlobs)
            MetadataBlobs[kvp.Key] = kvp.Value;
    }

    public int SizeOfPel => Bands * VipsEnumsExtensions.SizeOf(BandFormat);

    public void Dispose()
    {
        // For now, no native resources to free
    }
}

public static class VipsEnumsExtensions
{
    public static int SizeOf(VipsBandFormat format) => format switch
    {
        VipsBandFormat.UChar or VipsBandFormat.Char => 1,
        VipsBandFormat.UShort or VipsBandFormat.Short => 2,
        VipsBandFormat.UInt or VipsBandFormat.Int or VipsBandFormat.Float => 4,
        VipsBandFormat.Double or VipsBandFormat.Complex => 8,
        VipsBandFormat.DPComplex => 16,
        _ => 0
    };
}

