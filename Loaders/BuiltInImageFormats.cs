using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using CosmoImage.Core;

namespace CosmoImage.Loaders;

/// <summary>
/// <see cref="IVipsImageFormat"/> wrapper for the static built-in
/// loader classes. Captures a sniffer + loader (+ optional saver)
/// function pair behind the interface so built-in formats compose
/// with user-registered customs uniformly via <see cref="VipsConfiguration"/>.
/// </summary>
internal sealed class BuiltInFormat : IVipsImageFormat
{
    public string Name { get; }
    private readonly Func<IVipsSource, CancellationToken, ValueTask<bool>> _sniff;
    private readonly Func<IVipsSource, CancellationToken, ValueTask<VipsImage?>> _load;
    private readonly Func<VipsImage, Stream, CancellationToken, ValueTask>? _save;
    private readonly string[] _extensions;

    public BuiltInFormat(string name,
        Func<IVipsSource, CancellationToken, ValueTask<bool>> sniff,
        Func<IVipsSource, CancellationToken, ValueTask<VipsImage?>> load,
        Func<VipsImage, Stream, CancellationToken, ValueTask>? save = null,
        params string[] extensions)
    {
        Name = name;
        _sniff = sniff;
        _load = load;
        _save = save;
        _extensions = extensions ?? Array.Empty<string>();
    }

    public ValueTask<bool> CanDecodeAsync(IVipsSource source, CancellationToken cancellationToken = default)
        => _sniff(source, cancellationToken);

    public ValueTask<VipsImage?> LoadAsync(IVipsSource source, CancellationToken cancellationToken = default)
        => _load(source, cancellationToken);

    public bool CanEncode => _save != null;

    public ValueTask SaveAsync(VipsImage image, Stream stream, CancellationToken cancellationToken = default)
        => _save != null
            ? _save(image, stream, cancellationToken)
            : throw new NotSupportedException($"Format '{Name}' does not support encoding");

    public IReadOnlyList<string> FileExtensions => _extensions;
}

/// <summary>
/// Adapter that lets the existing <see cref="PipeWriter"/>-based
/// savers in <c>VipsImageOps</c> implement <see cref="IVipsImageFormat.SaveAsync"/>
/// (which takes a plain <see cref="Stream"/>). Wraps the stream as a
/// <see cref="PipeWriter"/>, runs the saver, then completes the writer.
/// </summary>
internal static class StreamSaverAdapter
{
    public static async ValueTask Save(Func<VipsImage, PipeWriter, Task> saver,
        VipsImage image, Stream stream, CancellationToken ct)
    {
        var writer = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
        try
        {
            await saver(image, writer);
            await writer.CompleteAsync();
        }
        catch (Exception ex)
        {
            await writer.CompleteAsync(ex);
            throw;
        }
    }
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
        new BuiltInFormat("TGA", VipsTgaLoader.IsTgaAsync, VipsTgaLoader.LoadAsync,
            (img, s, ct) => StreamSaverAdapter.Save(VipsImageOps.SaveTgaAsync, img, s, ct),
            ".tga"),
        new BuiltInFormat("PNM", VipsPnmLoader.IsPnmAsync, VipsPnmLoader.LoadAsync,
            (img, s, ct) => StreamSaverAdapter.Save(
                (i, w) => VipsImageOps.SavePnmAsync(i, w), img, s, ct),
            ".pnm", ".ppm", ".pgm", ".pbm"),

        // Scientific / niche formats.
        new BuiltInFormat("MAT", VipsMatLoader.IsMatAsync, VipsMatLoader.LoadAsync,
            null, ".mat"),
        new BuiltInFormat("NIFTI", VipsNiftiLoader.IsNiftiAsync, VipsNiftiLoader.LoadAsync,
            (img, s, ct) => StreamSaverAdapter.Save(VipsImageOps.SaveNiftiAsync, img, s, ct),
            ".nii"),
        new BuiltInFormat("FITS", VipsFitsLoader.IsFitsAsync, VipsFitsLoader.LoadAsync,
            (img, s, ct) => StreamSaverAdapter.Save(VipsImageOps.SaveFitsAsync, img, s, ct),
            ".fits", ".fit"),
        new BuiltInFormat("HDR", VipsHdrLoader.IsHdrAsync, VipsHdrLoader.LoadAsync,
            (img, s, ct) => StreamSaverAdapter.Save(VipsImageOps.SaveHdrAsync, img, s, ct),
            ".hdr"),

        // Document / vector formats.
        new BuiltInFormat("SVG", VipsSvgLoader.IsSvgAsync,
            (s, ct) => VipsSvgLoader.LoadAsync(s, 0, 0, ct),
            null, ".svg"),
        new BuiltInFormat("PDF", VipsPdfLoader.IsPdfAsync,
            (s, ct) => VipsPdfLoader.LoadAsync(s, page: 0, n: 1, dpi: 72, cancellationToken: ct),
            null, ".pdf"),

        // Header-only codecs (pixel decode not implemented).
        new BuiltInFormat("JP2K", VipsJp2kLoader.IsJp2kAsync,
            (s, ct) => ValueTask.FromException<VipsImage?>(new NotSupportedException(
                "JP2K pixel load is not supported; use VipsJp2kLoader.LoadHeaderAsync.")),
            null, ".jp2", ".jpx", ".jpf"),
        new BuiltInFormat("JXL", VipsJxlLoader.IsJxlAsync,
            (s, ct) => ValueTask.FromException<VipsImage?>(new NotSupportedException(
                "JXL pixel load is not supported; use VipsJxlLoader.LoadHeaderAsync.")),
            null, ".jxl"),

        // Bitmap formats — most common at the end so reverse-walk in
        // FindMatchAsync hits them first.
        new BuiltInFormat("QOI", VipsQoiLoader.IsQoiAsync, VipsQoiLoader.LoadAsync,
            (img, s, ct) => StreamSaverAdapter.Save(VipsImageOps.SaveQoiAsync, img, s, ct),
            ".qoi"),
        new BuiltInFormat("BMP", VipsBmpLoader.IsBmpAsync, VipsBmpLoader.LoadAsync,
            (img, s, ct) => StreamSaverAdapter.Save(VipsImageOps.SaveBmpAsync, img, s, ct),
            ".bmp"),
        new BuiltInFormat("TIFF", VipsTiffLoader.IsTiffAsync, VipsTiffLoader.LoadAsync,
            (img, s, ct) => StreamSaverAdapter.Save(
                (i, w) => VipsImageOps.SaveTiffAsync(i, w), img, s, ct),
            ".tif", ".tiff"),
        new BuiltInFormat("HEIF", VipsHeifLoader.IsHeifAsync, VipsHeifLoader.LoadAsync,
            (img, s, ct) => StreamSaverAdapter.Save(
                (i, w) => VipsImageOps.SaveHeifAsync(i, w), img, s, ct),
            ".heif", ".heic", ".avif"),
        new BuiltInFormat("GIF", VipsGifLoader.IsGifAsync, VipsGifLoader.LoadAsync,
            (img, s, ct) => StreamSaverAdapter.Save(VipsImageOps.SaveGifAsync, img, s, ct),
            ".gif"),
        new BuiltInFormat("WEBP", VipsWebpLoader.IsWebpAsync, VipsWebpLoader.LoadAsync,
            (img, s, ct) => StreamSaverAdapter.Save(
                (i, w) => VipsImageOps.SaveWebpAsync(i, w), img, s, ct),
            ".webp"),
        new BuiltInFormat("JPEG", VipsJpegLoader.IsJpegAsync, VipsJpegLoader.LoadAsync,
            (img, s, ct) => StreamSaverAdapter.Save(
                (i, w) => VipsImageOps.SaveJpegAsync(i, w), img, s, ct),
            ".jpg", ".jpeg", ".jpe"),
        new BuiltInFormat("PNG", VipsPngLoader.IsPngAsync, VipsPngLoader.LoadAsync,
            (img, s, ct) => StreamSaverAdapter.Save(
                (i, w) => VipsImageOps.SavePngAsync(i, w), img, s, ct),
            ".png"),
    };
}
