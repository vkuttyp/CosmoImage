using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using CosmoImage.Core;

namespace CosmoImage.Loaders;

public enum VipsImageFormat
{
    Unknown = 0,
    Png, Jpeg, Webp, Gif, Tiff, Bmp, Qoi,
    Heif, Avif, Jxl, Jp2k, Pdf, Svg, Hdr,
    Pnm, Tga, Fits, Nifti, Mat,
}

/// <summary>
/// Result of a unified <see cref="VipsIdentify.IdentifyAsync(Stream, CancellationToken)"/>
/// call: the detected format, plus a header-only <see cref="VipsImage"/>
/// when the matching loader supports header-only reads.
/// </summary>
public sealed class VipsIdentifyResult
{
    public required VipsImageFormat Format { get; init; }
    /// <summary>
    /// Header-only image (no pixel data) if the loader supports
    /// <c>LoadHeaderAsync</c>; <c>null</c> otherwise (or when format
    /// detection failed). Format-detect alone is still useful even
    /// without a header.
    /// </summary>
    public VipsImage? Header { get; init; }
}

/// <summary>
/// Unified format detection + load entry points. Mirrors ImageSharp's
/// <c>Image.IdentifyAsync</c> / <c>Image.LoadAsync</c>. Sniffs the
/// stream's magic bytes against every loader we ship and dispatches.
///
/// <para>The sniff order matters — distinctive magic numbers go first
/// (PNG / JPEG / WebP / GIF / HEIF), ambiguous-or-magic-less formats
/// (PNM, TGA) go last so they don't false-positive on more
/// distinctive formats.</para>
///
/// <para>Source must be re-readable from byte 0 after sniffing —
/// <see cref="PipeVipsSource"/> uses <c>SniffAsync</c> as a peek
/// (doesn't advance the consumed position), so each sniffer sees the
/// stream from byte 0.</para>
/// </summary>
public static class VipsIdentify
{
    /// <summary>
    /// Sniff the stream and return the detected format + a header-only
    /// image where supported.
    /// </summary>
    public static ValueTask<VipsIdentifyResult> IdentifyAsync(Stream stream, CancellationToken ct = default)
    {
        var source = new PipeVipsSource(PipeReader.Create(stream));
        return IdentifyAsync(source, ct);
    }

    public static async ValueTask<VipsIdentifyResult> IdentifyAsync(IVipsSource source, CancellationToken ct = default)
    {
        var format = await DetectAsync(source, ct);
        VipsImage? header = format switch
        {
            VipsImageFormat.Png => await TryHeader(VipsPngLoader.LoadHeaderAsync(source, ct)),
            VipsImageFormat.Jpeg => await TryHeader(VipsJpegLoader.LoadHeaderAsync(source, ct)),
            VipsImageFormat.Webp => await TryHeader(VipsWebpLoader.LoadHeaderAsync(source, ct)),
            VipsImageFormat.Gif => await TryHeader(VipsGifLoader.LoadHeaderAsync(source, ct)),
            VipsImageFormat.Tiff => await TryHeader(VipsTiffLoader.LoadHeaderAsync(source, ct)),
            VipsImageFormat.Bmp => await TryHeader(VipsBmpLoader.LoadHeaderAsync(source, ct)),
            // Other loaders don't all expose a header-only path; fall
            // through to a null header. The detected format alone is
            // still useful (e.g. for picking a downstream codec).
            _ => null,
        };
        return new VipsIdentifyResult { Format = format, Header = header };
    }

    /// <summary>
    /// Sniff the stream and load it. Throws when the format isn't
    /// recognised; returns a fully-decoded <see cref="VipsImage"/> on
    /// success (the same image you'd get by calling the per-format
    /// <c>LoadAsync</c> directly).
    /// </summary>
    public static ValueTask<VipsImage?> LoadAsync(Stream stream, CancellationToken ct = default)
    {
        var source = new PipeVipsSource(PipeReader.Create(stream));
        return LoadAsync(source, ct);
    }

    public static async ValueTask<VipsImage?> LoadAsync(IVipsSource source, CancellationToken ct = default)
    {
        var format = await DetectAsync(source, ct);
        return format switch
        {
            VipsImageFormat.Png => await VipsPngLoader.LoadAsync(source, ct),
            VipsImageFormat.Jpeg => await VipsJpegLoader.LoadAsync(source, ct),
            VipsImageFormat.Webp => await VipsWebpLoader.LoadAsync(source, ct),
            VipsImageFormat.Gif => await VipsGifLoader.LoadAsync(source, ct),
            VipsImageFormat.Tiff => await VipsTiffLoader.LoadAsync(source, ct),
            VipsImageFormat.Bmp => await VipsBmpLoader.LoadAsync(source, ct),
            VipsImageFormat.Qoi => await VipsQoiLoader.LoadAsync(source, ct),
            VipsImageFormat.Heif => await VipsHeifLoader.LoadAsync(source, ct),
            VipsImageFormat.Avif => await VipsHeifLoader.LoadAsync(source, ct),
            // JXL / JP2K only have header-only readers in CosmoImage; full
            // pixel decode would need a real codec. Surface as NotSupported
            // so callers know to use a typed loader directly.
            VipsImageFormat.Jxl =>
                throw new NotSupportedException("JXL pixel load is not supported; use VipsJxlLoader.LoadHeaderAsync."),
            VipsImageFormat.Jp2k =>
                throw new NotSupportedException("JP2K pixel load is not supported; use VipsJp2kLoader.LoadHeaderAsync."),
            VipsImageFormat.Pdf => await VipsPdfLoader.LoadAsync(source,
                page: 0, n: 1, dpi: 72, cancellationToken: ct),
            VipsImageFormat.Svg => await VipsSvgLoader.LoadAsync(source, 0, 0, ct),
            VipsImageFormat.Hdr => await VipsHdrLoader.LoadAsync(source, ct),
            VipsImageFormat.Fits => await VipsFitsLoader.LoadAsync(source, ct),
            VipsImageFormat.Nifti => await VipsNiftiLoader.LoadAsync(source, ct),
            VipsImageFormat.Mat => await VipsMatLoader.LoadAsync(source, ct),
            VipsImageFormat.Pnm => await VipsPnmLoader.LoadAsync(source, ct),
            VipsImageFormat.Tga => await VipsTgaLoader.LoadAsync(source, ct),
            _ => throw new NotSupportedException("Could not detect image format from stream content."),
        };
    }

    /// <summary>
    /// Walk every sniffer. Most distinctive magic numbers first; the
    /// magic-less formats (PNM, TGA) sit at the bottom and only
    /// match if nothing earlier did. HEIF and AVIF share the same
    /// loader but use the FTYP brand to distinguish, which the
    /// loader itself handles.
    /// </summary>
    private static async ValueTask<VipsImageFormat> DetectAsync(IVipsSource source, CancellationToken ct)
    {
        if (await VipsPngLoader.IsPngAsync(source, ct)) return VipsImageFormat.Png;
        if (await VipsJpegLoader.IsJpegAsync(source, ct)) return VipsImageFormat.Jpeg;
        if (await VipsWebpLoader.IsWebpAsync(source, ct)) return VipsImageFormat.Webp;
        if (await VipsGifLoader.IsGifAsync(source, ct)) return VipsImageFormat.Gif;
        if (await VipsHeifLoader.IsHeifAsync(source, ct)) return VipsImageFormat.Heif;
        if (await VipsTiffLoader.IsTiffAsync(source, ct)) return VipsImageFormat.Tiff;
        if (await VipsBmpLoader.IsBmpAsync(source, ct)) return VipsImageFormat.Bmp;
        if (await VipsQoiLoader.IsQoiAsync(source, ct)) return VipsImageFormat.Qoi;
        if (await VipsJxlLoader.IsJxlAsync(source, ct)) return VipsImageFormat.Jxl;
        if (await VipsJp2kLoader.IsJp2kAsync(source, ct)) return VipsImageFormat.Jp2k;
        if (await VipsPdfLoader.IsPdfAsync(source, ct)) return VipsImageFormat.Pdf;
        if (await VipsSvgLoader.IsSvgAsync(source, ct)) return VipsImageFormat.Svg;
        if (await VipsHdrLoader.IsHdrAsync(source, ct)) return VipsImageFormat.Hdr;
        if (await VipsFitsLoader.IsFitsAsync(source, ct)) return VipsImageFormat.Fits;
        if (await VipsNiftiLoader.IsNiftiAsync(source, ct)) return VipsImageFormat.Nifti;
        if (await VipsMatLoader.IsMatAsync(source, ct)) return VipsImageFormat.Mat;
        // Magic-less or weak-magic formats last — TGA notably has no
        // magic and would match almost any file via its weak heuristic.
        if (await VipsPnmLoader.IsPnmAsync(source, ct)) return VipsImageFormat.Pnm;
        if (await VipsTgaLoader.IsTgaAsync(source, ct)) return VipsImageFormat.Tga;
        return VipsImageFormat.Unknown;
    }

    private static async ValueTask<VipsImage?> TryHeader(ValueTask<VipsImage?> task)
    {
        try { return await task; }
        catch { return null; }
    }
}
