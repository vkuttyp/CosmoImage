using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ImageMagick;

namespace CosmoImage.Loaders;

public static class VipsHeifLoader
{
    private static readonly string[] HeifBrands = { "heic", "heix", "hevc", "heim", "heis", "hevm", "hevs", "mif1", "msf1", "avif", "avis" };

    public static async ValueTask<bool> IsHeifAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        var sniff = await source.SniffAsync(12, cancellationToken);
        if (sniff.Length < 12) return false;

        var span = sniff.Span;
        if (BinaryPrimitives.ReadUInt32BigEndian(span.Slice(0, 4)) < 8) return false;
        if (System.Text.Encoding.ASCII.GetString(span.Slice(4, 4)) != "ftyp") return false;

        string majorBrand = System.Text.Encoding.ASCII.GetString(span.Slice(8, 4));
        foreach (var brand in HeifBrands)
        {
            if (majorBrand.StartsWith(brand)) return true;
        }

        return false;
    }

    public static async ValueTask<VipsImage?> LoadHeaderAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        if (!await IsHeifAsync(source, cancellationToken))
            return null;

        // Manual ISOBMFF Parser for HEIF/AVIF metadata (Fast & works on dummy data)
        var ms = new MemoryStream();
        var buffer = new byte[81920];
        // Note: For header only, we don't consume the source, just sniff or read a bit.
        // But since we can't rewind the pipe, we'll use Sniff to get enough data if possible.
        var sniff = await source.SniffAsync(81920, cancellationToken);
        if (sniff.Length < 16) return null;
        
        ms.Write(sniff.Span);
        ms.Position = 0;

        int width = 0, height = 0;

        while (ms.Position + 8 <= ms.Length)
        {
            var header = new byte[8];
            ms.Read(header, 0, 8);

            uint size = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            string type = System.Text.Encoding.ASCII.GetString(header.AsSpan(4, 4));

            if (type == "ftyp")
            {
                ms.Seek(size - 8, SeekOrigin.Current);
            }
            else if (type == "meta")
            {
                ms.Read(new byte[4], 0, 4);
                continue; 
            }
            else if (type == "iprp" || type == "ipco")
            {
                continue;
            }
            else if (type == "ispe")
            {
                var data = new byte[12];
                ms.Read(data, 0, 12);
                width = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4, 4));
                height = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8, 4));
                
                return new VipsImage
                {
                    Width = width,
                    Height = height,
                    Bands = 3,
                    BandFormat = VipsBandFormat.UChar,
                    Interpretation = VipsInterpretation.RGB,
                    XRes = 1.0,
                    YRes = 1.0
                };
            }
            else
            {
                if (size == 1) // 64-bit size
                {
                    var largeSizeBuf = new byte[8];
                    if (ms.Read(largeSizeBuf, 0, 8) < 8) break;
                    long largeSize = (long)BinaryPrimitives.ReadUInt64BigEndian(largeSizeBuf);
                    if (ms.Position + largeSize - 16 > ms.Length) break;
                    ms.Seek(largeSize - 16, SeekOrigin.Current);
                }
                else if (size == 0) // Till end of file
                {
                    break;
                }
                else
                {
                    if (ms.Position + size - 8 > ms.Length) break;
                    ms.Seek(size - 8, SeekOrigin.Current);
                }
            }
        }

        return null;
    }

    public static async ValueTask<VipsImage?> LoadAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        if (!await IsHeifAsync(source, cancellationToken))
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

        // Try the manual ISOBMFF header parser first — it works for still
        // HEIF/AVIF (brands "heic"/"avif") which have a top-level ispe box.
        var memSource = new PipeVipsSource(System.IO.Pipelines.PipeReader.Create(new MemoryStream(imageBytes)));
        var image = await LoadHeaderAsync(memSource, cancellationToken);
        // AVIF *sequences* (brand "avis") and animated HEIC use a movie-track
        // (mvhd/trak) box layout where ispe isn't at the still-image item
        // location, so the manual parser returns null. Fall through to a
        // Magick-based probe in that case — Count, Width and Height come from
        // there anyway in the multi-frame path below.
        if (image == null)
        {
            try
            {
                using var probe = new MagickImageCollection(imageBytes);
                if (probe.Count == 0) return null;
                image = new VipsImage
                {
                    Width = (int)probe[0].Width,
                    Height = (int)probe[0].Height,
                    Bands = probe[0].HasAlpha ? 4 : 3,
                    BandFormat = VipsBandFormat.UChar,
                    Interpretation = VipsInterpretation.RGB,
                    XRes = 1.0,
                    YRes = 1.0,
                };
            }
            catch
            {
                return null;
            }
        }

        // Probe for animated/sequence HEIF/AVIF and per-frame profiles.
        // Animated HEIC and AVIF sequences carry multiple frames in the
        // same container; we enumerate via MagickImageCollection and stack
        // them into a tall buffer (n-pages × page-height) using the same
        // convention as VipsWebpLoader and VipsGifLoader.
        int width = image.Width;
        int pageHeight = image.Height;
        int bands = image.Bands;
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
                bands = probe[0].HasAlpha ? 4 : 3;

                var sb = new System.Text.StringBuilder();
                bool anyDelay = false;
                for (int i = 0; i < nPages; i++)
                {
                    if (i > 0) sb.Append(',');
                    var d = probe[i].AnimationDelay;
                    if (d > 0) anyDelay = true;
                    sb.Append(d);
                }
                // AVIF/HEIC sequences sometimes encode timing in container
                // boxes that Magick doesn't surface as AnimationDelay; only
                // emit the metadata when we actually have non-zero values.
                if (anyDelay) animationDelays = sb.ToString();
            }

            var exifProfile = probe[0].GetProfile("exif");
            if (exifProfile != null) image.MetadataBlobs["exif"] = exifProfile.ToByteArray();
            var xmpProfile = probe[0].GetProfile("xmp");
            if (xmpProfile != null) image.MetadataBlobs["xmp"] = xmpProfile.ToByteArray();
            var iccProfile = probe[0].GetColorProfile();
            if (iccProfile != null) image.MetadataBlobs["icc"] = iccProfile.ToByteArray();
        }
        catch { /* metadata extraction is best-effort; load shouldn't fail on it */ }

        int totalHeight = pageHeight * nPages;
        if (nPages > 1)
        {
            image.Width = width;
            image.Height = totalHeight;
            image.Bands = bands;
            image.Metadata["n-pages"] = nPages.ToString();
            image.Metadata["page-height"] = pageHeight.ToString();
            if (animationDelays != null)
                image.Metadata["animation-delays"] = animationDelays;
        }

        image.PixelsLazy = new Lazy<byte[]>(() =>
        {
            int stride = width * bands;
            var buf = new byte[stride * totalHeight];

            if (nPages == 1)
            {
                using var magickImage = new MagickImage(imageBytes);
                if (bands == 4) magickImage.ColorSpace = ColorSpace.sRGB;
                else magickImage.Alpha(AlphaOption.Off);
                using var pixels = magickImage.GetPixels();
                for (int y = 0; y < pageHeight; y++)
                {
                    var row = pixels.GetArea(0, y, (uint)width, 1)
                        ?? throw new InvalidOperationException($"HEIF: pixel row {y} returned null");
                    Array.Copy(row, 0, buf, y * stride, stride);
                }
                return buf;
            }

            // Multi-frame: stack frames vertically.
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
                        ?? throw new InvalidOperationException($"HEIF: page {p} row {y} returned null");
                    Array.Copy(row, 0, buf, pageBase + y * stride, stride);
                }
            }
            return buf;
        });

        return image;
    }

    /// <summary>
    /// Streaming HEIF/AVIF load: feeds the source directly to Magick.NET,
    /// decodes pixels eagerly, and drops the encoded buffer. Trades the
    /// laziness of <see cref="LoadAsync"/> for not holding the encoded
    /// HEIF/AVIF alongside the decoded pixel buffer. Animated AVIF/HEIC
    /// sequences decode all frames into a tall <c>page-height × n-pages</c>
    /// buffer using the same convention as <see cref="VipsWebpLoader"/>.
    /// </summary>
    public static async ValueTask<VipsImage?> LoadStreamingAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        if (!await IsHeifAsync(source, cancellationToken)) return null;

        // ISOBMFF box parsing requires random access; VipsSourceStream is
        // forward-only. Drain into a seekable MemoryStream first. Streaming
        // win is preserved: the encoded buffer goes out of scope after the
        // collection is disposed; PixelsLazy returns the decoded pixel buffer.
        using var seekable = new MemoryStream();
        var ringBuffer = new byte[81920];
        while (true)
        {
            int n = await source.ReadAsync(ringBuffer, cancellationToken);
            if (n == 0) break;
            seekable.Write(ringBuffer, 0, n);
        }
        seekable.Position = 0;

        try
        {
            using var collection = new MagickImageCollection(seekable);
            if (collection.Count == 0) return null;

            int width = (int)collection[0].Width;
            int pageHeight = (int)collection[0].Height;
            int bands = collection[0].HasAlpha ? 4 : 3;
            int nPages = collection.Count;
            int totalHeight = pageHeight * nPages;
            int stride = width * bands;
            var buf = new byte[stride * totalHeight];

            string? animationDelays = null;
            if (nPages > 1)
            {
                var sb = new System.Text.StringBuilder();
                bool anyDelay = false;
                for (int i = 0; i < nPages; i++)
                {
                    if (i > 0) sb.Append(',');
                    var d = collection[i].AnimationDelay;
                    if (d > 0) anyDelay = true;
                    sb.Append(d);
                }
                if (anyDelay) animationDelays = sb.ToString();
            }

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
                        ?? throw new InvalidOperationException($"HEIF streaming: page {p} row {y} returned null");
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

            if (nPages > 1)
            {
                image.Metadata["n-pages"] = nPages.ToString();
                image.Metadata["page-height"] = pageHeight.ToString();
                if (animationDelays != null) image.Metadata["animation-delays"] = animationDelays;
            }

            // Profiles attach to the first frame.
            var first = collection[0];
            var exifProfile = first.GetProfile("exif");
            if (exifProfile != null) image.MetadataBlobs["exif"] = exifProfile.ToByteArray();
            var xmpProfile = first.GetProfile("xmp");
            if (xmpProfile != null) image.MetadataBlobs["xmp"] = xmpProfile.ToByteArray();
            var iccProfile = first.GetColorProfile();
            if (iccProfile != null) image.MetadataBlobs["icc"] = iccProfile.ToByteArray();

            return image;
        }
        catch
        {
            return null;
        }
    }
}
