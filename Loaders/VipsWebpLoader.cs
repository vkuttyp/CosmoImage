using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ImageMagick;

namespace CosmoImage.Loaders;

public static class VipsWebpLoader
{
    public static async ValueTask<bool> IsWebpAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        var sniff = await source.SniffAsync(12, cancellationToken);
        if (sniff.Length < 12) return false;

        var span = sniff.Span;
        return span[0] == (byte)'R' && span[1] == (byte)'I' && span[2] == (byte)'F' && span[3] == (byte)'F' &&
               span[8] == (byte)'W' && span[9] == (byte)'E' && span[10] == (byte)'B' && span[11] == (byte)'P';
    }

    public static async ValueTask<VipsImage?> LoadHeaderAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        if (!await IsWebpAsync(source, cancellationToken))
            return null;

        // Skip RIFF header
        var headerBuffer = new byte[12];
        await source.ReadAsync(headerBuffer, cancellationToken);

        var chunkBuffer = new byte[8];
        while (true)
        {
            var read = await source.ReadAsync(chunkBuffer, cancellationToken);
            if (read < 8) break;

            uint type = BinaryPrimitives.ReadUInt32BigEndian(chunkBuffer.AsSpan(0, 4));
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(chunkBuffer.AsSpan(4, 4));

            if (type == 0x56503858) // "VP8X"
            {
                var data = new byte[10];
                read = await source.ReadAsync(data, cancellationToken);
                if (read < 10) break;

                bool hasAlpha = (data[0] & 0x10) != 0;
                int width = 1 + (data[3] | (data[4] << 8) | (data[5] << 16));
                int height = 1 + (data[6] | (data[7] << 8) | (data[8] << 16));

                return new VipsImage
                {
                    Width = width,
                    Height = height,
                    Bands = hasAlpha ? 4 : 3,
                    BandFormat = VipsBandFormat.UChar,
                    Interpretation = VipsInterpretation.RGB,
                    Coding = VipsCoding.None,
                    XRes = 1.0,
                    YRes = 1.0
                };
            }
            else if (type == 0x56503820) // "VP8 "
            {
                var data = new byte[10];
                read = await source.ReadAsync(data, cancellationToken);
                if (read < 10) break;

                if ((data[0] & 1) == 0 && data[3] == 0x9D && data[4] == 0x01 && data[5] == 0x2A)
                {
                    int width = (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6, 2)) & 0x3FFF);
                    int height = (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8, 2)) & 0x3FFF);

                    return new VipsImage
                    {
                        Width = width,
                        Height = height,
                        Bands = 3,
                        BandFormat = VipsBandFormat.UChar,
                        Interpretation = VipsInterpretation.RGB,
                        Coding = VipsCoding.None,
                        XRes = 1.0,
                        YRes = 1.0
                    };
                }
            }
            else if (type == 0x5650384C) // "VP8L"
            {
                var data = new byte[5];
                read = await source.ReadAsync(data, cancellationToken);
                if (read < 5) break;

                if (data[0] == 0x2F)
                {
                    uint val = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(1, 4));
                    int width = (int)((val & 0x3FFF) + 1);
                    int height = (int)(((val >> 14) & 0x3FFF) + 1);
                    bool hasAlpha = ((val >> 28) & 1) != 0;

                    return new VipsImage
                    {
                        Width = width,
                        Height = height,
                        Bands = hasAlpha ? 4 : 3,
                        BandFormat = VipsBandFormat.UChar,
                        Interpretation = VipsInterpretation.RGB,
                        Coding = VipsCoding.None,
                        XRes = 1.0,
                        YRes = 1.0
                    };
                }
            }

            uint toSkip = (length + 1) & ~1u;
            var skipBuffer = new byte[Math.Min(toSkip, 4096)];
            while (toSkip > 0)
            {
                int chunkToRead = (int)Math.Min(toSkip, (uint)skipBuffer.Length);
                read = await source.ReadAsync(skipBuffer.AsMemory(0, chunkToRead), cancellationToken);
                if (read <= 0) break;
                toSkip -= (uint)read;
            }
        }

        return null;
    }

    public static async ValueTask<VipsImage?> LoadAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        var ms = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            int readCount = await source.ReadAsync(buffer, cancellationToken);
            if (readCount == 0) break;
            ms.Write(buffer, 0, readCount);
        }

        var imageBytes = ms.ToArray();
        var memSource = new PipeVipsSource(System.IO.Pipelines.PipeReader.Create(new MemoryStream(imageBytes)));
        var image = await LoadHeaderAsync(memSource, cancellationToken);
        if (image == null) return null;

        int width = image.Width;
        int pageHeight = image.Height;
        int bands = image.Bands;

        // Detect animated WebP via MagickImageCollection. Single-frame WebPs
        // produce a 1-element collection — we always use the collection path
        // for uniformity; the per-frame loop just runs once for non-animated.
        int nPages = 1;
        string? animationDelays = null;
        try
        {
            using var probe = new MagickImageCollection(imageBytes);
            nPages = probe.Count;
            if (nPages > 1)
            {
                width = (int)probe[0].Width;
                pageHeight = (int)probe[0].Height;

                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < nPages; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(probe[i].AnimationDelay);
                }
                animationDelays = sb.ToString();
            }
        }
        catch { /* fall through; nPages stays 1 */ }

        // Eager probe for EXIF/XMP/ICC profiles. Pixels stay lazy.
        try
        {
            using var probe = new MagickImage(imageBytes);
            var exifProfile = probe.GetProfile("exif");
            if (exifProfile != null) image.MetadataBlobs["exif"] = exifProfile.ToByteArray();
            var xmpProfile = probe.GetProfile("xmp");
            if (xmpProfile != null) image.MetadataBlobs["xmp"] = xmpProfile.ToByteArray();
            var iccProfile = probe.GetColorProfile();
            if (iccProfile != null) image.MetadataBlobs["icc"] = iccProfile.ToByteArray();
        }
        catch { /* metadata extraction is best-effort */ }

        // Update image dims for animated layout: tall buffer with frames stacked.
        int totalHeight = pageHeight * nPages;
        if (nPages > 1)
        {
            image.Width = width;
            image.Height = totalHeight;
            image.Metadata["n-pages"] = nPages.ToString();
            image.Metadata["page-height"] = pageHeight.ToString();
            if (animationDelays != null)
                image.Metadata["animation-delays"] = animationDelays;
        }

        image.PixelsLazy = new Lazy<byte[]>(() =>
        {
            int stride = width * bands;
            var buf = new byte[stride * totalHeight];

            using var collection = new MagickImageCollection(imageBytes);
            for (int p = 0; p < nPages; p++)
            {
                var frame = collection[p];
                if (bands == 4) frame.ColorSpace = ColorSpace.sRGB;
                else frame.Alpha(AlphaOption.Off);

                using var pixels = frame.GetPixels();
                int pageBase = p * pageHeight * stride;
                for (int y = 0; y < pageHeight; y++)
                {
                    var row = pixels.GetArea(0, y, (uint)width, 1)
                        ?? throw new InvalidOperationException($"WebP: page {p} row {y} returned null");
                    Array.Copy(row, 0, buf, pageBase + y * stride, stride);
                }
            }
            return buf;
        });

        return image;
    }
}
