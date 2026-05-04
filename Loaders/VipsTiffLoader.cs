using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ImageMagick;

namespace CosmoImage.Loaders;

public static class VipsTiffLoader
{
    public static async ValueTask<bool> IsTiffAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        var sniff = await source.SniffAsync(4, cancellationToken);
        if (sniff.Length < 4) return false;

        var span = sniff.Span;
        if (span[0] == 0x49 && span[1] == 0x49 && span[2] == 0x2A && span[3] == 0x00) return true;
        if (span[0] == 0x4D && span[1] == 0x4D && span[2] == 0x00 && span[3] == 0x2A) return true;
        // BigTIFF
        if (span[0] == 0x49 && span[1] == 0x49 && span[2] == 0x2B && span[3] == 0x00) return true;
        if (span[0] == 0x4D && span[1] == 0x4D && span[2] == 0x00 && span[3] == 0x2B) return true;

        return false;
    }

    /// <summary>
    /// Header-only TIFF parse: walks IFD0 by hand to extract dimensions,
    /// samples-per-pixel, and orientation without decoding pixels. Faster than
    /// going through Magick.NET, and works on the IFD-only synthetic TIFFs
    /// used in tests. Returns null if the TIFF is BigTIFF (different format)
    /// or otherwise unparseable; callers should fall back to LoadAsync.
    /// </summary>
    public static async ValueTask<VipsImage?> LoadHeaderAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        if (!await IsTiffAsync(source, cancellationToken)) return null;

        var buf = new byte[8192];
        int totalRead = 0;
        while (totalRead < buf.Length)
        {
            int read = await source.ReadAsync(buf.AsMemory(totalRead, buf.Length - totalRead), cancellationToken);
            if (read == 0) break;
            totalRead += read;
        }
        if (totalRead < 8) return null;

        bool le;
        if (buf[0] == 0x49 && buf[1] == 0x49) le = true;
        else if (buf[0] == 0x4D && buf[1] == 0x4D) le = false;
        else return null;

        ushort magic = ReadU16(buf, 2, le);
        // BigTIFF (0x002B) has 8-byte offsets — different parsing; punt.
        if (magic != 0x002A) return null;

        uint ifdOffset = ReadU32(buf, 4, le);
        if (ifdOffset + 2 > totalRead) return null;

        ushort numEntries = ReadU16(buf, (int)ifdOffset, le);
        int entriesStart = (int)ifdOffset + 2;
        if (entriesStart + numEntries * 12 > totalRead) return null;

        int width = 0, height = 0, samples = 1, bps = 8, orientation = 0;
        for (int i = 0; i < numEntries; i++)
        {
            int e = entriesStart + i * 12;
            ushort tag = ReadU16(buf, e, le);
            ushort type = ReadU16(buf, e + 2, le);
            // value-or-offset at e+8; for SHORT/LONG count==1 it's inline
            long val;
            if (type == 3) val = ReadU16(buf, e + 8, le);
            else if (type == 4) val = ReadU32(buf, e + 8, le);
            else continue;

            switch (tag)
            {
                case 256: width = (int)val; break;       // ImageWidth
                case 257: height = (int)val; break;      // ImageLength
                case 258: bps = (int)val; break;         // BitsPerSample
                case 274: orientation = (int)val; break; // Orientation
                case 277: samples = (int)val; break;     // SamplesPerPixel
            }
        }

        if (width <= 0 || height <= 0) return null;

        var image = new VipsImage
        {
            Width = width,
            Height = height,
            Bands = samples,
            BandFormat = bps > 8 ? VipsBandFormat.UShort : VipsBandFormat.UChar,
            Interpretation = samples <= 2 ? VipsInterpretation.BW : VipsInterpretation.RGB,
            Coding = VipsCoding.None,
            XRes = 1.0,
            YRes = 1.0,
        };
        if (orientation >= 1 && orientation <= 8)
            image.Metadata["orientation"] = orientation.ToString();
        return image;
    }

    public static async ValueTask<VipsImage?> LoadAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        if (!await IsTiffAsync(source, cancellationToken))
            return null;

        var ms = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            ms.Write(buffer, 0, read);
        }

        var imageBytes = ms.ToArray();

        // Pure-managed fast path for baseline uncompressed single-page TIFFs.
        // Returns null for compressed / multi-page / tiled / planar / BigTIFF —
        // those still flow through Magick below.
        var pure = PureTiffDecoder.TryDecode(imageBytes);
        if (pure != null)
        {
            AttachTiffMetadata(imageBytes, pure);
            return pure;
        }

        // Probe via Magick.NET for dimensions, colorspace, and page count.
        // Multi-page TIFFs are detected via collection.Count > 1; pages are
        // stacked into a tall buffer with n-pages/page-height metadata, same
        // convention as animated GIF/WebP. Heterogeneous-page TIFFs (different
        // dims per page) fall back to single-page mode — flat-buffer layout
        // can't represent them.
        int width, pageHeight, bands, nPages;
        try
        {
            using var pages = new MagickImageCollection(imageBytes);
            if (pages.Count == 0) return null;

            width = (int)pages[0].Width;
            pageHeight = (int)pages[0].Height;
            int colorBands = pages[0].ColorSpace == ColorSpace.Gray ? 1 : 3;
            bands = colorBands + (pages[0].HasAlpha ? 1 : 0);

            bool uniform = true;
            for (int i = 1; i < pages.Count; i++)
            {
                if ((int)pages[i].Width != width || (int)pages[i].Height != pageHeight)
                {
                    uniform = false;
                    break;
                }
            }
            nPages = uniform ? pages.Count : 1;
        }
        catch
        {
            return null;
        }

        int totalHeight = pageHeight * nPages;

        var image = new VipsImage
        {
            Width = width,
            Height = totalHeight,
            Bands = bands,
            BandFormat = VipsBandFormat.UChar,
            Interpretation = bands <= 2 ? VipsInterpretation.BW : VipsInterpretation.RGB,
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
                    if (bands == 1) frame.ColorSpace = ColorSpace.Gray;
                    else if (bands == 3 && frame.HasAlpha) frame.Alpha(AlphaOption.Off);
                    else if (bands == 4 && !frame.HasAlpha) frame.Alpha(AlphaOption.On);

                    using var pixels = frame.GetPixels();
                    int pageBase = p * pageHeight * stride;
                    for (int y = 0; y < pageHeight; y++)
                    {
                        var row = pixels.GetArea(0, y, (uint)width, 1)
                            ?? throw new InvalidOperationException($"TIFF: page {p} row {y} returned null");
                        Array.Copy(row, 0, buf, pageBase + y * stride, stride);
                    }
                }
                return buf;
            })
        };

        if (nPages > 1)
        {
            image.Metadata["n-pages"] = nPages.ToString();
            image.Metadata["page-height"] = pageHeight.ToString();
        }

        AttachTiffMetadata(imageBytes, image);

        return image;
    }

    /// <summary>
    /// Eager probe via Magick for EXIF/XMP/ICC profiles + orientation +
    /// ImageDescription. Best-effort: silently swallows Magick failures so
    /// metadata extraction never fails an otherwise successful load.
    /// </summary>
    private static void AttachTiffMetadata(byte[] imageBytes, VipsImage image)
    {
        try
        {
            using var probe = new MagickImage(imageBytes);
            var exifProfile = probe.GetProfile("exif");
            if (exifProfile != null) image.MetadataBlobs["exif"] = exifProfile.ToByteArray();
            var xmpProfile = probe.GetProfile("xmp");
            if (xmpProfile != null) image.MetadataBlobs["xmp"] = xmpProfile.ToByteArray();
            var iccProfile = probe.GetColorProfile();
            if (iccProfile != null) image.MetadataBlobs["icc"] = iccProfile.ToByteArray();

            int orient = (int)probe.Orientation;
            if (orient >= 1 && orient <= 8)
                image.Metadata["orientation"] = orient.ToString();

            // ImageDescription (TIFF tag 270). Magick exposes it under the
            // "comment" attribute. Carrier for OME-XML in microscopy /
            // pathology (OME-TIFF) — see VipsOmeTiff for typed accessors.
            CapturImageDescription(probe, image);
        }
        catch { /* best-effort */ }
    }

    private static void CapturImageDescription(IMagickImage<byte> probe, VipsImage image)
    {
        var imageDescription = probe.GetAttribute("comment");
        if (string.IsNullOrEmpty(imageDescription)) return;

        image.Metadata["tiff:image-description"] = imageDescription;

        // OME-TIFF carries OME-XML in this same tag. Surface the XML under a
        // dedicated key and try to populate XRes/YRes from PhysicalSizeX/Y.
        if (CosmoImage.Core.VipsOmeTiff.LooksLikeOmeXml(imageDescription))
        {
            image.Metadata["ome:xml"] = imageDescription;
            CosmoImage.Core.VipsOmeTiff.PopulatePhysicalSize(image);
        }
    }

    /// <summary>
    /// Streaming TIFF load: feeds the source directly to Magick.NET via
    /// <see cref="VipsSourceStream"/>, decodes all pages eagerly, and drops
    /// the encoded buffer. TIFFs are typically large (often tens of MB) so
    /// the savings here are meaningful — a 50 MB TIFF that decodes to 200 MB
    /// of pixels keeps just the 200 MB instead of 250 MB.
    ///
    /// Forfeits the laziness of <see cref="LoadAsync"/>; use when memory
    /// matters more than deferring decode (typical CDN-thumbnail workflows
    /// always materialize anyway).
    /// </summary>
    public static async ValueTask<VipsImage?> LoadStreamingAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        if (!await IsTiffAsync(source, cancellationToken)) return null;
        await Task.Yield();

        try
        {
            using var stream = source.AsStream();
            using var pages = new MagickImageCollection(stream);
            if (pages.Count == 0) return null;

            int width = (int)pages[0].Width;
            int pageHeight = (int)pages[0].Height;
            int colorBands = pages[0].ColorSpace == ColorSpace.Gray ? 1 : 3;
            int bands = colorBands + (pages[0].HasAlpha ? 1 : 0);

            bool uniform = true;
            for (int i = 1; i < pages.Count; i++)
            {
                if ((int)pages[i].Width != width || (int)pages[i].Height != pageHeight)
                {
                    uniform = false;
                    break;
                }
            }
            int nPages = uniform ? pages.Count : 1;
            int totalHeight = pageHeight * nPages;
            int stride = width * bands;
            var buf = new byte[stride * totalHeight];

            for (int p = 0; p < nPages; p++)
            {
                var frame = pages[p];
                if (bands == 1) frame.ColorSpace = ColorSpace.Gray;
                else if (bands == 3 && frame.HasAlpha) frame.Alpha(AlphaOption.Off);
                else if (bands == 4 && !frame.HasAlpha) frame.Alpha(AlphaOption.On);

                using var pixels = frame.GetPixels();
                int pageBase = p * pageHeight * stride;
                for (int y = 0; y < pageHeight; y++)
                {
                    var row = pixels.GetArea(0, y, (uint)width, 1)
                        ?? throw new InvalidOperationException($"TIFF streaming: page {p} row {y} returned null");
                    Array.Copy(row, 0, buf, pageBase + y * stride, stride);
                }
            }

            var image = new VipsImage
            {
                Width = width,
                Height = totalHeight,
                Bands = bands,
                BandFormat = VipsBandFormat.UChar,
                Interpretation = bands <= 2 ? VipsInterpretation.BW : VipsInterpretation.RGB,
                Coding = VipsCoding.None,
                XRes = 1.0,
                YRes = 1.0,
                PixelsLazy = new Lazy<byte[]>(() => buf),
            };

            if (nPages > 1)
            {
                image.Metadata["n-pages"] = nPages.ToString();
                image.Metadata["page-height"] = pageHeight.ToString();
            }

            // Profile + orientation metadata from the first page (already
            // decoded; same source object).
            var first = pages[0];
            var exifProfile = first.GetProfile("exif");
            if (exifProfile != null) image.MetadataBlobs["exif"] = exifProfile.ToByteArray();
            var xmpProfile = first.GetProfile("xmp");
            if (xmpProfile != null) image.MetadataBlobs["xmp"] = xmpProfile.ToByteArray();
            var iccProfile = first.GetColorProfile();
            if (iccProfile != null) image.MetadataBlobs["icc"] = iccProfile.ToByteArray();
            int orient = (int)first.Orientation;
            if (orient >= 1 && orient <= 8) image.Metadata["orientation"] = orient.ToString();
            CapturImageDescription(first, image);

            return image;
        }
        catch
        {
            return null;
        }
    }

    private static ushort ReadU16(byte[] buf, int offset, bool le) =>
        le ? (ushort)(buf[offset] | (buf[offset + 1] << 8))
           : (ushort)((buf[offset] << 8) | buf[offset + 1]);

    private static uint ReadU32(byte[] buf, int offset, bool le) =>
        le ? (uint)(buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16) | (buf[offset + 3] << 24))
           : (uint)((buf[offset] << 24) | (buf[offset + 1] << 16) | (buf[offset + 2] << 8) | buf[offset + 3]);
}
