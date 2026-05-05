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
    /// <summary>
    /// Save as TIFF. <paramref name="pyramid"/> = true writes a tiled
    /// pyramidal TIFF (Magick's <c>Ptif</c>) — multi-resolution layout used by
    /// deep-zoom viewers (OpenSeadragon, IIIF). Only meaningful for
    /// single-page input; for multi-page input it falls back to standard TIFF.
    ///
    /// <para><paramref name="tileSize"/> = positive value writes a Tiled-TIFF
    /// (organized as fixed-size square tiles instead of horizontal strips).
    /// Useful for cropped reads from huge images. Pass 0 for the default
    /// stripped layout. Each tile is <c>tileSize × tileSize</c> pixels.</para>
    ///
    /// <para>UShort input (16-bit per sample) emits a 16-bit-per-sample
    /// TIFF; UChar emits 8-bit. Other formats cast to UChar first.</para>
    /// </summary>
    public static async Task SaveAsync(VipsImage image, PipeWriter writer,
        bool pyramid = false, int tileSize = 0,
        CancellationToken cancellationToken = default)
    {
        int width = image.Width;
        int height = image.Height;
        int bands = image.Bands;

        if (bands != 1 && bands != 3 && bands != 4)
            throw new NotSupportedException($"TIFF save needs 1, 3, or 4 bands; got {bands}");
        if (tileSize < 0) throw new ArgumentOutOfRangeException(nameof(tileSize));

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

        // Bit-depth dispatch. UShort → 16-bit-per-sample TIFF; UChar →
        // 8-bit. Anything else casts to UChar — there's no lossless
        // generic path for Float / Int formats.
        bool sixteenBit = image.BandFormat == VipsBandFormat.UShort;
        int bytesPerSample = sixteenBit ? 2 : 1;
        VipsImage src = image.BandFormat == VipsBandFormat.UChar || sixteenBit
            ? image
            : VipsImageOps.CastUChar(image);

        // Materialize source pixels.
        byte[] pixels;
        if (src.Pixels is { } existing)
        {
            pixels = existing;
        }
        else
        {
            var sink = new MemorySink(src);
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

        int frameStride = width * bands * bytesPerSample;
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
                Depth = (uint)(bytesPerSample * 8),
            };
            var frame = new MagickImage();
            frame.Read(frameBytes, settings);
            frame.Format = MagickFormat.Tiff;
            // Match the previous saver's compression default. Zip == Deflate.
            frame.Settings.Compression = CompressionMethod.Zip;
            // 16-bit inputs need the depth carried through to the
            // encoder — Magick otherwise quantizes back to 8 bits per
            // sample at write time.
            if (sixteenBit) frame.Depth = 16;
            // Tiled-TIFF: pass tile geometry to libtiff via define.
            // Without the define libtiff defaults to a stripped layout.
            if (tileSize > 0)
            {
                frame.Settings.SetDefine(MagickFormat.Tiff, "tile-geometry", $"{tileSize}x{tileSize}");
            }
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

            // ImageDescription (TIFF tag 270). OME-TIFF callers set
            // Metadata["ome:xml"]; generic TIFF callers set
            // Metadata["tiff:image-description"]. ome:xml wins when both
            // are present so the OME schema authoritatively round-trips.
            string? description = null;
            if (image.Metadata.TryGetValue("ome:xml", out var ome) && !string.IsNullOrEmpty(ome))
                description = ome;
            else if (image.Metadata.TryGetValue("tiff:image-description", out var generic) && !string.IsNullOrEmpty(generic))
                description = generic;
            if (description != null)
                collection[0].SetAttribute("comment", description);
        }

        // Pyramid only applies to single-page output. Multi-page + pyramid is
        // ambiguous (each page would need its own pyramid); fall back to plain
        // multi-page TIFF in that case.
        var outFmt = (pyramid && nPages == 1) ? MagickFormat.Ptif : MagickFormat.Tiff;
        if (outFmt == MagickFormat.Ptif && collection.Count > 0)
            collection[0].Format = MagickFormat.Ptif;

        using var ms = new MemoryStream();
        collection.Write(ms, outFmt);
        ms.Position = 0;
        await ms.CopyToAsync(writer.AsStream(), cancellationToken);

        await writer.FlushAsync(cancellationToken);
        await writer.CompleteAsync();
    }
}
