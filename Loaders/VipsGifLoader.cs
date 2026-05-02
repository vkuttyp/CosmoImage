using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ImageMagick;

namespace CosmoImage.Loaders;

public static class VipsGifLoader
{
    public static async ValueTask<bool> IsGifAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        var sniff = await source.SniffAsync(4, cancellationToken);
        if (sniff.Length < 4) return false;

        var span = sniff.Span;
        return span[0] == 'G' && span[1] == 'I' && span[2] == 'F' && span[3] == '8';
    }

    public static async ValueTask<VipsImage?> LoadHeaderAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        // For GIF, we use LoadAsync for header as well so we accurately get page counts.
        return await LoadAsync(source, cancellationToken);
    }

    public static async ValueTask<VipsImage?> LoadAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        if (!await IsGifAsync(source, cancellationToken))
            return null;

        var ms = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            int readCount = await source.ReadAsync(buffer, cancellationToken);
            if (readCount == 0) break;
            ms.Write(buffer, 0, readCount);
        }

        var imageBytes = ms.ToArray();

        // Probe the collection once to get dimensions, page count, and
        // per-frame animation delays. The lazy re-opens for full pixel decode.
        // Frames are stacked top-to-bottom into a tall VipsImage; n-pages,
        // page-height, and animation-delays metadata let savers reconstruct
        // the multi-frame structure on output.
        int width, pageHeight, nPages;
        string? animationDelays = null;
        byte[]? exifBlob = null, xmpBlob = null, iccBlob = null;
        string? comment = null;
        try
        {
            using var probe = new MagickImageCollection(imageBytes);
            if (probe.Count == 0) return null;
            width = (int)probe[0].Width;
            pageHeight = (int)probe[0].Height;
            nPages = probe.Count;

            var delays = new System.Text.StringBuilder();
            for (int i = 0; i < nPages; i++)
            {
                if (i > 0) delays.Append(',');
                delays.Append(probe[i].AnimationDelay);
            }
            animationDelays = delays.ToString();

            // Profile + comment metadata attaches to the first frame in
            // typical GIF authoring tools.
            var first = probe[0];
            exifBlob = first.GetProfile("exif")?.ToByteArray();
            xmpBlob = first.GetProfile("xmp")?.ToByteArray();
            iccBlob = first.GetProfile("icc")?.ToByteArray();
            var commentAttr = first.GetAttribute("comment");
            if (!string.IsNullOrEmpty(commentAttr)) comment = commentAttr;
        }
        catch
        {
            return null;
        }

        int totalHeight = pageHeight * nPages;
        const int bands = 4; // GIF expanded to RGBA

        var image = new VipsImage
        {
            Width = width,
            Height = totalHeight,
            Bands = bands,
            BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            Coding = VipsCoding.None,
            XRes = 1.0,
            YRes = 1.0,
            PixelsLazy = new Lazy<byte[]>(() =>
            {
                using var collection = new MagickImageCollection(imageBytes);
                int stride = width * bands;
                var buf = new byte[stride * totalHeight];

                for (int p = 0; p < nPages; p++)
                {
                    var frame = collection[p];
                    if (!frame.HasAlpha) frame.Alpha(AlphaOption.On);
                    frame.ColorSpace = ColorSpace.sRGB;

                    using var pixels = frame.GetPixels();
                    int pageBase = p * pageHeight * stride;
                    for (int y = 0; y < pageHeight; y++)
                    {
                        var row = pixels.GetArea(0, y, (uint)width, 1)
                            ?? throw new InvalidOperationException($"GIF page {p} row {y} returned null");
                        Array.Copy(row, 0, buf, pageBase + y * stride, stride);
                    }
                }
                return buf;
            })
        };

        image.Metadata["n-pages"] = nPages.ToString();
        image.Metadata["page-height"] = pageHeight.ToString();
        if (animationDelays != null)
            image.Metadata["animation-delays"] = animationDelays;
        if (exifBlob != null) image.MetadataBlobs["exif"] = exifBlob;
        if (xmpBlob != null) image.MetadataBlobs["xmp"] = xmpBlob;
        if (iccBlob != null) image.MetadataBlobs["icc"] = iccBlob;
        if (comment != null) image.Metadata["comment"] = comment;

        return image;
    }

    /// <summary>
    /// Streaming GIF load: feeds the source directly to Magick.NET, decodes
    /// every frame eagerly into a tall buffer, and drops the encoded buffer.
    /// Single-frame GIFs decode through the same path with n-pages=1 and no
    /// animation-delays metadata.
    /// </summary>
    public static async ValueTask<VipsImage?> LoadStreamingAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        if (!await IsGifAsync(source, cancellationToken)) return null;
        await Task.Yield();

        try
        {
            using var stream = source.AsStream();
            using var collection = new MagickImageCollection(stream);
            if (collection.Count == 0) return null;

            int width = (int)collection[0].Width;
            int pageHeight = (int)collection[0].Height;
            int nPages = collection.Count;
            int totalHeight = pageHeight * nPages;
            const int bands = 4; // GIF expanded to RGBA, matching LoadAsync

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < nPages; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(collection[i].AnimationDelay);
            }
            string animationDelays = sb.ToString();

            int stride = width * bands;
            var buf = new byte[stride * totalHeight];

            for (int p = 0; p < nPages; p++)
            {
                var frame = collection[p];
                if (!frame.HasAlpha) frame.Alpha(AlphaOption.On);
                frame.ColorSpace = ColorSpace.sRGB;

                using var pixels = frame.GetPixels();
                int pageBase = p * pageHeight * stride;
                for (int y = 0; y < pageHeight; y++)
                {
                    var row = pixels.GetArea(0, y, (uint)width, 1)
                        ?? throw new InvalidOperationException($"GIF streaming: page {p} row {y} returned null");
                    Array.Copy(row, 0, buf, pageBase + y * stride, stride);
                }
            }

            var image = new VipsImage
            {
                Width = width,
                Height = totalHeight,
                Bands = bands,
                BandFormat = VipsBandFormat.UChar,
                Interpretation = VipsInterpretation.RGB,
                Coding = VipsCoding.None,
                XRes = 1.0,
                YRes = 1.0,
                PixelsLazy = new Lazy<byte[]>(() => buf),
            };

            image.Metadata["n-pages"] = nPages.ToString();
            image.Metadata["page-height"] = pageHeight.ToString();
            image.Metadata["animation-delays"] = animationDelays;

            // Profile + comment metadata attaches to the first frame.
            var first = collection[0];
            var exifBlob = first.GetProfile("exif")?.ToByteArray();
            var xmpBlob = first.GetProfile("xmp")?.ToByteArray();
            var iccBlob = first.GetProfile("icc")?.ToByteArray();
            var commentAttr = first.GetAttribute("comment");
            if (exifBlob != null) image.MetadataBlobs["exif"] = exifBlob;
            if (xmpBlob != null) image.MetadataBlobs["xmp"] = xmpBlob;
            if (iccBlob != null) image.MetadataBlobs["icc"] = iccBlob;
            if (!string.IsNullOrEmpty(commentAttr)) image.Metadata["comment"] = commentAttr;

            return image;
        }
        catch
        {
            return null;
        }
    }
}
