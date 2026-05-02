using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ImageMagick;

namespace CosmoImage.Loaders;

/// <summary>
/// Generic Magick.NET-backed loader for formats that don't need a custom
/// header parser. Used by TGA, QOI, and PBM/PGM/PPM. The format is detected
/// by the caller's sniff predicate; everything past that — pixel decode,
/// band/colourspace resolution, EXIF/XMP/ICC profile pull — flows through
/// Magick.
/// </summary>
internal static class VipsMagickWrapLoader
{
    /// <summary>
    /// Streaming variant of <see cref="LoadAsync"/>: feeds the source directly
    /// to Magick.NET via <see cref="VipsSourceStream"/>, decodes pixels
    /// eagerly, and drops the encoded bytes. Forfeits lazy materialization
    /// in exchange for not holding the encoded buffer alongside the decoded
    /// pixel buffer. Use when memory matters more than deferring decode.
    /// </summary>
    public static async ValueTask<VipsImage?> LoadStreamingAsync(IVipsSource source, CancellationToken cancellationToken, MagickFormat? formatHint = null)
    {
        // No await needed yet — kept async for API symmetry with LoadAsync
        // and so future Magick.NET async APIs slot in cleanly.
        await Task.Yield();
        var readSettings = formatHint.HasValue ? new MagickReadSettings { Format = formatHint.Value } : null;

        try
        {
            using var stream = source.AsStream();
            using var img = readSettings != null ? new MagickImage(stream, readSettings) : new MagickImage(stream);

            int width = (int)img.Width;
            int height = (int)img.Height;
            int colorBands = img.ColorSpace == ColorSpace.Gray ? 1 : 3;
            int bands = colorBands + (img.HasAlpha ? 1 : 0);
            byte[]? exif = img.GetProfile("exif")?.ToByteArray();
            byte[]? xmp = img.GetProfile("xmp")?.ToByteArray();
            byte[]? icc = img.GetProfile("icc")?.ToByteArray();

            // Decode pixels into a fresh buffer right now.
            int stride = width * bands;
            var buf = new byte[stride * height];
            if (bands == 1) img.ColorSpace = ColorSpace.Gray;
            else if (bands == 3 && img.HasAlpha) img.Alpha(AlphaOption.Off);
            else if (bands == 4 && !img.HasAlpha) img.Alpha(AlphaOption.On);
            using (var pixels = img.GetPixels())
            {
                for (int y = 0; y < height; y++)
                {
                    var row = pixels.GetArea(0, y, (uint)width, 1)
                        ?? throw new InvalidOperationException($"Magick streaming load: pixel row {y} returned null");
                    Array.Copy(row, 0, buf, y * stride, stride);
                }
            }

            var image = new VipsImage
            {
                Width = width,
                Height = height,
                Bands = bands,
                BandFormat = VipsBandFormat.UChar,
                Interpretation = bands <= 2 ? VipsInterpretation.BW : VipsInterpretation.RGB,
                Coding = VipsCoding.None,
                XRes = 1.0,
                YRes = 1.0,
                // Buffer is already materialized; PixelsLazy hands it out
                // verbatim instead of re-decoding.
                PixelsLazy = new Lazy<byte[]>(() => buf),
            };

            if (exif != null) image.MetadataBlobs["exif"] = exif;
            if (xmp != null) image.MetadataBlobs["xmp"] = xmp;
            if (icc != null) image.MetadataBlobs["icc"] = icc;
            return image;
        }
        catch
        {
            return null;
        }
    }

    public static async ValueTask<VipsImage?> LoadAsync(IVipsSource source, CancellationToken cancellationToken, MagickFormat? formatHint = null)
    {
        var ms = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            ms.Write(buffer, 0, read);
        }

        var imageBytes = ms.ToArray();
        // For magic-less formats (TGA) Magick can't auto-detect the codec
        // from the byte buffer alone — it relies on extension or hint.
        var probeSettings = formatHint.HasValue ? new MagickReadSettings { Format = formatHint.Value } : null;

        int width, height, bands;
        byte[]? exif = null, xmp = null, icc = null;
        try
        {
            using var probe = probeSettings != null ? new MagickImage(imageBytes, probeSettings) : new MagickImage(imageBytes);
            width = (int)probe.Width;
            height = (int)probe.Height;
            int colorBands = probe.ColorSpace == ColorSpace.Gray ? 1 : 3;
            bands = colorBands + (probe.HasAlpha ? 1 : 0);
            exif = probe.GetProfile("exif")?.ToByteArray();
            xmp = probe.GetProfile("xmp")?.ToByteArray();
            icc = probe.GetProfile("icc")?.ToByteArray();
        }
        catch
        {
            return null;
        }

        var image = new VipsImage
        {
            Width = width,
            Height = height,
            Bands = bands,
            BandFormat = VipsBandFormat.UChar,
            Interpretation = bands <= 2 ? VipsInterpretation.BW : VipsInterpretation.RGB,
            Coding = VipsCoding.None,
            XRes = 1.0,
            YRes = 1.0,
            PixelsLazy = new Lazy<byte[]>(() =>
            {
                using var img = probeSettings != null ? new MagickImage(imageBytes, probeSettings) : new MagickImage(imageBytes);
                int stride = width * bands;
                var buf = new byte[stride * height];

                if (bands == 1) img.ColorSpace = ColorSpace.Gray;
                else if (bands == 3 && img.HasAlpha) img.Alpha(AlphaOption.Off);
                else if (bands == 4 && !img.HasAlpha) img.Alpha(AlphaOption.On);

                using var pixels = img.GetPixels();
                for (int y = 0; y < height; y++)
                {
                    var row = pixels.GetArea(0, y, (uint)width, 1)
                        ?? throw new InvalidOperationException($"Magick load: pixel row {y} returned null");
                    Array.Copy(row, 0, buf, y * stride, stride);
                }
                return buf;
            })
        };

        if (exif != null) image.MetadataBlobs["exif"] = exif;
        if (xmp != null) image.MetadataBlobs["xmp"] = xmp;
        if (icc != null) image.MetadataBlobs["icc"] = icc;
        return image;
    }
}
