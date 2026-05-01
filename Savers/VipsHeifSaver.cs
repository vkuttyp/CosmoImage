using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using ImageMagick;

namespace CosmoImage.Savers;

/// <summary>
/// HEIC and AVIF encoder. Goes through Magick.NET's libheif binding — the same
/// dependency that powers <see cref="VipsHeifLoader"/>. Encoder availability
/// (x265 for HEIC, libaom/rav1e/svt-av1 for AVIF) depends on the Magick.NET
/// build; on platforms without an encoder the underlying Write call throws
/// with a clear "no decode delegate / no encode delegate" message.
/// </summary>
public static class VipsHeifSaver
{
    public static Task SaveHeifAsync(VipsImage image, PipeWriter writer, int quality = 75, bool lossless = false, CancellationToken cancellationToken = default)
        => SaveAsync(image, writer, MagickFormat.Heic, quality, lossless, cancellationToken);

    public static Task SaveAvifAsync(VipsImage image, PipeWriter writer, int quality = 75, bool lossless = false, CancellationToken cancellationToken = default)
        => SaveAsync(image, writer, MagickFormat.Avif, quality, lossless, cancellationToken);

    private static async Task SaveAsync(VipsImage image, PipeWriter writer, MagickFormat format, int quality, bool lossless, CancellationToken ct)
    {
        int width = image.Width;
        int height = image.Height;
        int bands = image.Bands;

        if (bands != 1 && bands != 3 && bands != 4)
            throw new NotSupportedException($"HEIF/AVIF save needs 1, 3, or 4 bands; got {bands}");

        // Materialize source pixels. MemorySink picks tile shape from the
        // image's DemandHint; if the image is already memory-backed (loader →
        // saver with no transforms), skip the materialization entirely.
        byte[] pixels;
        if (image.Pixels is { } existing)
        {
            pixels = existing;
        }
        else
        {
            var sink = new MemorySink(image);
            await sink.RunAsync(ct);
            pixels = sink.Pixels;
        }

        var rawFormat = bands switch
        {
            1 => MagickFormat.Gray,
            3 => MagickFormat.Rgb,
            4 => MagickFormat.Rgba,
            _ => throw new InvalidOperationException()
        };

        var readSettings = new MagickReadSettings
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = rawFormat,
            Depth = 8,
        };

        using var magickImage = new MagickImage();
        magickImage.Read(pixels, readSettings);

        magickImage.Format = format;
        magickImage.Quality = (uint)Math.Clamp(quality, 1, 100);

        if (lossless)
            magickImage.Settings.SetDefine(format, "lossless", "true");

        // Round-trip metadata via Magick's profile API. EXIF blobs are stored
        // without the JPEG-specific "Exif\0\0" prefix (just raw TIFF), which is
        // exactly what Magick / libheif expects.
        if (image.MetadataBlobs.TryGetValue("exif", out var exif))
            magickImage.SetProfile(new ImageProfile("exif", exif));
        if (image.MetadataBlobs.TryGetValue("xmp", out var xmp))
            magickImage.SetProfile(new ImageProfile("xmp", xmp));
        if (image.MetadataBlobs.TryGetValue("icc", out var icc))
            magickImage.SetProfile(new ColorProfile(icc));

        // Magick.NET's Write isn't truly async; buffer to memory then forward.
        using var ms = new MemoryStream();
        magickImage.Write(ms);
        ms.Position = 0;
        await ms.CopyToAsync(writer.AsStream(), ct);

        await writer.FlushAsync(ct);
        await writer.CompleteAsync();
    }
}
