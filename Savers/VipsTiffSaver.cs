using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using ImageMagick;

namespace CosmoImage.Savers;

/// <summary>
/// TIFF writer. Handles single-page and multi-page output. Multi-page comes
/// from the same metadata convention as <see cref="VipsGifSaver"/> and the
/// animated <see cref="VipsWebpSaver"/>: image height is N × page-height,
/// with <c>n-pages</c> and <c>page-height</c> in <see cref="VipsImage.Metadata"/>.
/// Pages are extracted from the tall buffer and assembled into a
/// <see cref="MagickImageCollection"/>; libtiff under Magick links them via
/// "next IFD" offsets per the TIFF multi-image convention.
/// </summary>
public static class VipsTiffSaver
{
    public static async Task SaveAsync(VipsImage image, PipeWriter writer, CancellationToken cancellationToken = default)
    {
        int width = image.Width;
        int height = image.Height;
        int bands = image.Bands;

        if (bands != 1 && bands != 3 && bands != 4)
            throw new NotSupportedException($"TIFF save needs 1, 3, or 4 bands; got {bands}");

        // Detect multi-page layout. Default to single-page.
        int nPages = 1;
        int pageHeight = height;
        if (image.Metadata.TryGetValue("n-pages", out var npStr) &&
            int.TryParse(npStr, out int nP) && nP > 0 &&
            image.Metadata.TryGetValue("page-height", out var phStr) &&
            int.TryParse(phStr, out int ph) && ph > 0 &&
            nP * ph == height)
        {
            nPages = nP;
            pageHeight = ph;
        }

        // Materialize source pixels.
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

        int frameStride = width * bands;
        int frameSize = frameStride * pageHeight;

        using var collection = new MagickImageCollection();
        for (int p = 0; p < nPages; p++)
        {
            var frameBytes = new byte[frameSize];
            Buffer.BlockCopy(pixels, p * frameSize, frameBytes, 0, frameSize);

            var settings = new MagickReadSettings
            {
                Width = (uint)width,
                Height = (uint)pageHeight,
                Format = rawFormat,
                Depth = 8,
            };
            var frame = new MagickImage();
            frame.Read(frameBytes, settings);
            frame.Format = MagickFormat.Tiff;
            // Match the previous saver's compression default. Zip == Deflate.
            frame.Settings.Compression = CompressionMethod.Zip;
            collection.Add(frame);
        }

        // Round-trip metadata via Magick's profile API. EXIF/XMP/ICC attach to
        // the first page — typical reader convention for multi-page TIFF.
        if (collection.Count > 0)
        {
            if (image.MetadataBlobs.TryGetValue("exif", out var exif))
                collection[0].SetProfile(new ImageProfile("exif", exif));
            if (image.MetadataBlobs.TryGetValue("xmp", out var xmp))
                collection[0].SetProfile(new ImageProfile("xmp", xmp));
            if (image.MetadataBlobs.TryGetValue("icc", out var icc))
                collection[0].SetProfile(new ColorProfile(icc));
        }

        using var ms = new MemoryStream();
        collection.Write(ms, MagickFormat.Tiff);
        ms.Position = 0;
        await ms.CopyToAsync(writer.AsStream(), cancellationToken);

        await writer.FlushAsync(cancellationToken);
        await writer.CompleteAsync();
    }
}
