using System;
using System.Threading;
using System.Threading.Tasks;
using CosmoImage.Core;

namespace CosmoImage.Loaders;

/// <summary>
/// <see cref="IVipsImageFormat"/> wrapper for the static built-in
/// loader classes. Captures a sniffer + loader function pair behind
/// the interface so built-in formats compose with user-registered
/// customs uniformly via <see cref="VipsConfiguration"/>.
/// </summary>
internal sealed class BuiltInFormat : IVipsImageFormat
{
    public string Name { get; }
    private readonly Func<IVipsSource, CancellationToken, ValueTask<bool>> _sniff;
    private readonly Func<IVipsSource, CancellationToken, ValueTask<VipsImage?>> _load;

    public BuiltInFormat(string name,
        Func<IVipsSource, CancellationToken, ValueTask<bool>> sniff,
        Func<IVipsSource, CancellationToken, ValueTask<VipsImage?>> load)
    {
        Name = name;
        _sniff = sniff;
        _load = load;
    }

    public ValueTask<bool> CanDecodeAsync(IVipsSource source, CancellationToken cancellationToken = default)
        => _sniff(source, cancellationToken);

    public ValueTask<VipsImage?> LoadAsync(IVipsSource source, CancellationToken cancellationToken = default)
        => _load(source, cancellationToken);
}

/// <summary>
/// Built-in format catalogue. <see cref="All"/> returns the canonical
/// list in REVERSE priority order — registering them in this order
/// then walking the registry in reverse (newer wins) hits the most
/// distinctive magic first (PNG → JPEG → WebP → …) and the magic-less
/// fallback formats last (PNM, TGA).
/// </summary>
internal static class BuiltInImageFormats
{
    public static IVipsImageFormat[] All() => new IVipsImageFormat[]
    {
        // Magic-less / weakest heuristics first — they only match if
        // nothing earlier did.
        new BuiltInFormat("TGA", VipsTgaLoader.IsTgaAsync, VipsTgaLoader.LoadAsync),
        new BuiltInFormat("PNM", VipsPnmLoader.IsPnmAsync, VipsPnmLoader.LoadAsync),

        // Scientific / niche formats.
        new BuiltInFormat("MAT", VipsMatLoader.IsMatAsync, VipsMatLoader.LoadAsync),
        new BuiltInFormat("NIFTI", VipsNiftiLoader.IsNiftiAsync, VipsNiftiLoader.LoadAsync),
        new BuiltInFormat("FITS", VipsFitsLoader.IsFitsAsync, VipsFitsLoader.LoadAsync),
        new BuiltInFormat("HDR", VipsHdrLoader.IsHdrAsync, VipsHdrLoader.LoadAsync),

        // Document / vector formats.
        new BuiltInFormat("SVG", VipsSvgLoader.IsSvgAsync,
            (s, ct) => VipsSvgLoader.LoadAsync(s, 0, 0, ct)),
        new BuiltInFormat("PDF", VipsPdfLoader.IsPdfAsync,
            (s, ct) => VipsPdfLoader.LoadAsync(s, page: 0, n: 1, dpi: 72, cancellationToken: ct)),

        // Header-only codecs (pixel decode not implemented).
        new BuiltInFormat("JP2K", VipsJp2kLoader.IsJp2kAsync,
            (s, ct) => ValueTask.FromException<VipsImage?>(new NotSupportedException(
                "JP2K pixel load is not supported; use VipsJp2kLoader.LoadHeaderAsync."))),
        new BuiltInFormat("JXL", VipsJxlLoader.IsJxlAsync,
            (s, ct) => ValueTask.FromException<VipsImage?>(new NotSupportedException(
                "JXL pixel load is not supported; use VipsJxlLoader.LoadHeaderAsync."))),

        // Bitmap formats.
        new BuiltInFormat("QOI", VipsQoiLoader.IsQoiAsync, VipsQoiLoader.LoadAsync),
        new BuiltInFormat("BMP", VipsBmpLoader.IsBmpAsync, VipsBmpLoader.LoadAsync),
        new BuiltInFormat("TIFF", VipsTiffLoader.IsTiffAsync, VipsTiffLoader.LoadAsync),
        new BuiltInFormat("HEIF", VipsHeifLoader.IsHeifAsync, VipsHeifLoader.LoadAsync),
        new BuiltInFormat("GIF", VipsGifLoader.IsGifAsync, VipsGifLoader.LoadAsync),
        new BuiltInFormat("WEBP", VipsWebpLoader.IsWebpAsync, VipsWebpLoader.LoadAsync),
        new BuiltInFormat("JPEG", VipsJpegLoader.IsJpegAsync, VipsJpegLoader.LoadAsync),
        new BuiltInFormat("PNG", VipsPngLoader.IsPngAsync, VipsPngLoader.LoadAsync),
    };
}
