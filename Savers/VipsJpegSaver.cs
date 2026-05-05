using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace CosmoImage.Savers;

public static class VipsJpegSaver
{
    public static async Task SaveAsync(VipsImage image, PipeWriter writer, int quality = 75, CancellationToken cancellationToken = default)
    {
        int width = image.Width;
        int height = image.Height;
        int bands = image.Bands;
        int pelSize = image.SizeOfPel;

        // Materialize the source pixels in parallel. MemorySink picks tile shape
        // from the image's DemandHint so SmallTile pipelines (e.g. resize chains)
        // get 128×128 tiles instead of being forced into full-width strips.
        // If the image is already memory-backed (loaded directly from a loader,
        // no transforms in between), skip the materialization entirely.
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

        // Pure-C# encoder handles 1-band greyscale + 3-band RGB. JPEG has
        // no alpha channel, so 4-band RGBA gets the alpha stripped to RGB
        // before encoding — alpha would be discarded by any JPEG codec.
        // Other band counts (2 = greyscale+alpha, etc.) drop to single-
        // channel by taking the first band (lossy but matches JPEG's
        // fundamental "no alpha" constraint).
        byte[] jpegBytes;
        if (bands == 1 || bands == 3)
        {
            jpegBytes = PureJpegEncoder.Encode(pixels, width, height, bands, quality);
        }
        else if (bands == 4)
        {
            // Strip alpha: drop the 4th band per pixel.
            var rgb = new byte[width * height * 3];
            for (int i = 0, j = 0; i < pixels.Length; i += 4, j += 3)
            {
                rgb[j + 0] = pixels[i + 0];
                rgb[j + 1] = pixels[i + 1];
                rgb[j + 2] = pixels[i + 2];
            }
            jpegBytes = PureJpegEncoder.Encode(rgb, width, height, 3, quality);
        }
        else if (bands == 2)
        {
            // Greyscale + alpha → drop the alpha plane.
            var grey = new byte[width * height];
            for (int i = 0, j = 0; i < pixels.Length; i += 2, j++)
                grey[j] = pixels[i];
            jpegBytes = PureJpegEncoder.Encode(grey, width, height, 1, quality);
        }
        else
        {
            throw new NotSupportedException($"JPEG save needs 1/2/3/4 bands; got {bands}");
        }

        // Splice metadata: SOI → EXIF (APP1) → XMP (APP1) → rest of encoded JPEG.
        // The encoded stream starts with SOI and may include APP0/JFIF; injecting
        // our APP1 markers right after SOI keeps them in valid position.
        var jpegMemory = jpegBytes.AsMemory();
        if (jpegMemory.Length < 2)
            throw new InvalidOperationException("JPEG encoder produced no output");

        var stream = writer.AsStream();
        await stream.WriteAsync(jpegMemory.Slice(0, 2), cancellationToken); // SOI

        if (image.MetadataBlobs.TryGetValue("exif", out var exifBlob))
            await WriteApp1MarkerAsync(stream, VipsJpegLoader.ExifIdentifier, exifBlob, cancellationToken);
        if (image.MetadataBlobs.TryGetValue("xmp", out var xmpBlob))
            await WriteApp1MarkerAsync(stream, VipsJpegLoader.XmpIdentifier, xmpBlob, cancellationToken);
        if (image.MetadataBlobs.TryGetValue("icc", out var iccBlob))
            await WriteIccApp2MarkersAsync(stream, iccBlob, cancellationToken);

        await stream.WriteAsync(jpegMemory.Slice(2), cancellationToken);

        await writer.FlushAsync(cancellationToken);
        await writer.CompleteAsync();
    }

    private static async Task WriteApp1MarkerAsync(System.IO.Stream stream, byte[] identifier, byte[] payload, CancellationToken ct)
    {
        // APP1 segment length includes the 2 length bytes themselves, the
        // identifier (with its trailing NUL already in the array), and payload.
        // Hard cap at 65533 (length field is uint16, minus the 2 bytes for length).
        int totalLen = 2 + identifier.Length + payload.Length;
        if (totalLen > 65535)
            throw new NotSupportedException(
                $"APP1 segment too large to write in a single marker ({totalLen} > 65535). " +
                "Multi-segment write is not yet implemented.");

        var header = new byte[4];
        header[0] = 0xFF;
        header[1] = 0xE1;
        header[2] = (byte)(totalLen >> 8);
        header[3] = (byte)(totalLen & 0xFF);

        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(identifier, ct);
        await stream.WriteAsync(payload, ct);
    }

    /// <summary>
    /// Write an ICC profile as a sequence of APP2 markers per ICC.1:1998-09
    /// Annex B. Each marker carries "ICC_PROFILE\0" + 1-byte 1-based seq +
    /// 1-byte total + chunk data. Max payload per marker is 65519 bytes.
    /// </summary>
    private static async Task WriteIccApp2MarkersAsync(System.IO.Stream stream, byte[] icc, CancellationToken ct)
    {
        const int chunkSize = 65535 - 2 - 12 - 2; // = 65519
        int totalChunks = (icc.Length + chunkSize - 1) / chunkSize;
        if (totalChunks > 255)
            throw new NotSupportedException($"ICC profile too large ({icc.Length} bytes); APP2 spec allows at most 255 chunks.");

        for (int seq = 1; seq <= totalChunks; seq++)
        {
            int offset = (seq - 1) * chunkSize;
            int chunkLen = Math.Min(chunkSize, icc.Length - offset);
            int markerLen = 2 + VipsJpegLoader.IccIdentifier.Length + 2 + chunkLen;

            var header = new byte[4];
            header[0] = 0xFF;
            header[1] = 0xE2;
            header[2] = (byte)(markerLen >> 8);
            header[3] = (byte)(markerLen & 0xFF);
            await stream.WriteAsync(header, ct);
            await stream.WriteAsync(VipsJpegLoader.IccIdentifier, ct);
            await stream.WriteAsync(new byte[] { (byte)seq, (byte)totalChunks }, ct);
            await stream.WriteAsync(icc.AsMemory(offset, chunkLen), ct);
        }
    }
}
