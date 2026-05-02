using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using ImageMagick;

namespace CosmoImage.Savers;

/// <summary>
/// Generic Magick.NET-backed saver for single-frame raster formats whose
/// only writer-side knob is the target format enum. Used by TGA, QOI, and
/// PBM/PGM/PPM/PAM. Materializes the source image, hands raw RGB(A) pixels
/// to Magick, and writes the chosen format. EXIF/XMP/ICC profiles ride
/// through where the format supports them.
/// </summary>
internal static class VipsMagickWrapSaver
{
    public static async Task SaveAsync(VipsImage image, PipeWriter writer, MagickFormat format, CancellationToken cancellationToken = default)
    {
        int width = image.Width;
        int height = image.Height;
        int bands = image.Bands;
        if (bands != 1 && bands != 3 && bands != 4)
            throw new NotSupportedException($"Magick wrap save needs 1, 3, or 4 bands; got {bands}");

        byte[] pixels;
        if (image.Pixels is { } existing)
        {
            pixels = existing;
        }
        else
        {
            var sink = new MemorySink(image);
            await sink.RunAsync(cancellationToken);
            pixels = sink.Pixels;
        }

        var rawFormat = bands switch
        {
            1 => MagickFormat.Gray,
            3 => MagickFormat.Rgb,
            4 => MagickFormat.Rgba,
            _ => throw new InvalidOperationException()
        };
        var settings = new MagickReadSettings
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = rawFormat,
            Depth = 8,
        };

        using var img = new MagickImage();
        img.Read(pixels, settings);
        img.Format = format;

        if (image.MetadataBlobs.TryGetValue("exif", out var exif))
            img.SetProfile(new ImageProfile("exif", exif));
        if (image.MetadataBlobs.TryGetValue("xmp", out var xmp))
            img.SetProfile(new ImageProfile("xmp", xmp));
        if (image.MetadataBlobs.TryGetValue("icc", out var icc))
            img.SetProfile(new ColorProfile(icc));

        using var ms = new MemoryStream();
        img.Write(ms, format);
        ms.Position = 0;
        await ms.CopyToAsync(writer.AsStream(), cancellationToken);

        await writer.FlushAsync(cancellationToken);
        await writer.CompleteAsync();
    }
}
