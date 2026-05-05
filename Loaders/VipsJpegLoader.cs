using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using JpegLibrary;

namespace CosmoImage.Loaders;

public static class VipsJpegLoader
{
    // APP1 identifier prefixes that distinguish EXIF and XMP payloads sharing
    // the 0xFFE1 marker. Both include the trailing NUL.
    internal static readonly byte[] ExifIdentifier = { 0x45, 0x78, 0x69, 0x66, 0x00, 0x00 }; // "Exif\0\0"
    internal static readonly byte[] XmpIdentifier  = System.Text.Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
    // APP2 identifier for ICC profile chunks; followed by 1-byte sequence
    // number and 1-byte total chunks per spec ICC.1:1998-09 Annex B.
    internal static readonly byte[] IccIdentifier = System.Text.Encoding.ASCII.GetBytes("ICC_PROFILE\0");

    /// <summary>
    /// Scan a JPEG byte stream for an APP1 marker whose payload starts with
    /// <paramref name="identifier"/> and return the bytes that follow the
    /// identifier (the raw EXIF / XMP / etc. blob). Returns null if no such
    /// segment exists. Stops at SOS (start of scan) — markers after that are
    /// inside the entropy-coded image data and don't carry metadata.
    /// </summary>
    internal static byte[]? ExtractApp1Segment(byte[] jpeg, byte[] identifier)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return null; // not a JPEG
        int i = 2;
        while (i + 3 < jpeg.Length)
        {
            if (jpeg[i] != 0xFF) return null;
            byte marker = jpeg[i + 1];

            // Standalone markers (no length, no payload).
            if (marker == 0xD8 || marker == 0xD9 || marker == 0x01 ||
                (marker >= 0xD0 && marker <= 0xD7))
            {
                i += 2;
                continue;
            }
            if (marker == 0xDA) return null; // SOS — past header

            int length = (jpeg[i + 2] << 8) | jpeg[i + 3]; // big-endian, includes itself
            if (length < 2 || i + 2 + length > jpeg.Length) return null;

            if (marker == 0xE1 && length >= 2 + identifier.Length &&
                AsSpan(jpeg, i + 4, identifier.Length).SequenceEqual(identifier))
            {
                int blobStart = i + 4 + identifier.Length;
                int blobLength = length - 2 - identifier.Length;
                return jpeg.AsSpan(blobStart, blobLength).ToArray();
            }
            i += 2 + length;
        }
        return null;
    }

    private static System.ReadOnlySpan<byte> AsSpan(byte[] arr, int start, int length) =>
        arr.AsSpan(start, length);

    /// <summary>
    /// Walk APP2 markers tagged with "ICC_PROFILE\0", read 1-based sequence and
    /// total counts, concatenate chunks in sequence order. Returns null if no
    /// ICC profile is present or the segments are inconsistent.
    /// </summary>
    internal static byte[]? ExtractIccProfile(byte[] jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return null;

        var chunks = new System.Collections.Generic.SortedDictionary<int, byte[]>();
        int totalChunks = 0;
        int i = 2;

        while (i + 3 < jpeg.Length)
        {
            if (jpeg[i] != 0xFF) return null;
            byte marker = jpeg[i + 1];
            if (marker == 0xD8 || marker == 0xD9 || marker == 0x01 ||
                (marker >= 0xD0 && marker <= 0xD7))
            {
                i += 2;
                continue;
            }
            if (marker == 0xDA) break;

            int length = (jpeg[i + 2] << 8) | jpeg[i + 3];
            if (length < 2 || i + 2 + length > jpeg.Length) return null;

            if (marker == 0xE2 && length >= 2 + IccIdentifier.Length + 2 &&
                jpeg.AsSpan(i + 4, IccIdentifier.Length).SequenceEqual(IccIdentifier))
            {
                int hdr = i + 4 + IccIdentifier.Length;
                int seqNo = jpeg[hdr];
                int total = jpeg[hdr + 1];
                int dataStart = hdr + 2;
                int dataLength = length - 2 - IccIdentifier.Length - 2;
                if (dataLength < 0) return null;

                if (totalChunks == 0) totalChunks = total;
                else if (total != totalChunks) return null; // inconsistent

                chunks[seqNo] = jpeg.AsSpan(dataStart, dataLength).ToArray();
            }
            i += 2 + length;
        }

        if (chunks.Count == 0 || totalChunks == 0) return null;
        if (chunks.Count != totalChunks) return null; // missing chunks

        int totalSize = 0;
        foreach (var c in chunks.Values) totalSize += c.Length;
        var result = new byte[totalSize];
        int offset = 0;
        foreach (var c in chunks.Values)
        {
            Array.Copy(c, 0, result, offset, c.Length);
            offset += c.Length;
        }
        return result;
    }

    public static async ValueTask<bool> IsJpegAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        var sniff = await source.SniffAsync(2, cancellationToken);
        if (sniff.Length < 2) return false;
        
        var span = sniff.Span;
        return span[0] == 0xFF && span[1] == 0xD8;
    }

    public static async ValueTask<VipsImage?> LoadAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        if (!await IsJpegAsync(source, cancellationToken))
            return null;

        var ms = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            ms.Write(buffer, 0, read);
        }

        var jpegBytes = ms.ToArray();
        var decoder = new JpegDecoder();
        decoder.SetInput(jpegBytes);
        decoder.Identify();

        int width = decoder.Width;
        int height = decoder.Height;
        int bands = decoder.NumberOfComponents;

        // Memory-image dtype: pixels live directly on the VipsImage. Decoding
        // is lazy — the first downstream Prepare forces materialization, but
        // header-only callers (LoadHeaderAsync) never trigger it. Downstream
        // ops alias this buffer in their Prepare path with no per-tile copy.
        // Pre-detect color space ONCE (the lazy may run multiple times in
        // some pipelines; the JPEG markers don't change between runs).
        var colorSpace = DetectJpegColorSpace(jpegBytes, bands);

        var pixelsLazy = new Lazy<byte[]>(() =>
        {
            int stride = width * bands;
            var buf = new byte[stride * height];
            var innerDecoder = new JpegDecoder();
            innerDecoder.SetInput(jpegBytes);
            innerDecoder.SetOutputWriter(new SimpleOutputWriter(width, height, bands, buf));
            innerDecoder.Decode();
            // SimpleOutputWriter writes raw component samples interleaved
            // at offsets 0/1/2 of each pixel — for a default JFIF JPEG
            // those are Y, Cb, Cr, which need conversion to RGB before
            // we hand the buffer back labeled as RGB.
            ConvertColorSpace(buf, width, height, bands, colorSpace);
            return buf;
        });

        var image = new VipsImage
        {
            Width = width,
            Height = height,
            Bands = bands,
            BandFormat = VipsBandFormat.UChar,
            Coding = VipsCoding.None,
            Interpretation = bands == 1 ? VipsInterpretation.BW : VipsInterpretation.RGB,
            XRes = 1.0,
            YRes = 1.0,
            PixelsLazy = pixelsLazy
        };

        // Capture raw EXIF/XMP segments for lossless round-trip through save.
        // We scan markers ourselves because we need the untouched payload
        // bytes, not parsed tag values.
        var exifBlob = ExtractApp1Segment(jpegBytes, ExifIdentifier);
        if (exifBlob != null) image.MetadataBlobs["exif"] = exifBlob;
        var xmpBlob = ExtractApp1Segment(jpegBytes, XmpIdentifier);
        if (xmpBlob != null) image.MetadataBlobs["xmp"] = xmpBlob;
        var iccBlob = ExtractIccProfile(jpegBytes);
        if (iccBlob != null) image.MetadataBlobs["icc"] = iccBlob;

        SurfaceExifTags(exifBlob, image);

        return image;
    }

    public static async ValueTask<VipsImage?> LoadHeaderAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        return await LoadAsync(source, cancellationToken);
    }

    /// <summary>
    /// Streaming JPEG load. Reads the source into a single buffer, decodes
    /// pixels eagerly, and drops the encoded buffer immediately. Trades the
    /// laziness of <see cref="LoadAsync"/> for not holding the encoded JPEG
    /// alongside the decoded pixel buffer (a 5 MB JPEG that decodes to 50 MB
    /// of pixels: keeps 50 MB instead of 55 MB).
    ///
    /// JpegLibrary's <c>SetInput</c> takes <c>ReadOnlyMemory&lt;byte&gt;</c>
    /// so a single in-memory buffer is still required during the decode; it
    /// simply doesn't outlive the call.
    /// </summary>
    public static async ValueTask<VipsImage?> LoadStreamingAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        if (!await IsJpegAsync(source, cancellationToken))
            return null;

        var ms = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            ms.Write(buffer, 0, read);
        }
        var jpegBytes = ms.ToArray();

        var decoder = new JpegDecoder();
        decoder.SetInput(jpegBytes);
        decoder.Identify();
        int width = decoder.Width;
        int height = decoder.Height;
        int bands = decoder.NumberOfComponents;

        // Decode now into a fresh buffer so we can drop jpegBytes after.
        var pixels = new byte[width * bands * height];
        decoder.SetOutputWriter(new SimpleOutputWriter(width, height, bands, pixels));
        decoder.Decode();
        ConvertColorSpace(pixels, width, height, bands, DetectJpegColorSpace(jpegBytes, bands));

        var image = new VipsImage
        {
            Width = width,
            Height = height,
            Bands = bands,
            BandFormat = VipsBandFormat.UChar,
            Coding = VipsCoding.None,
            Interpretation = bands == 1 ? VipsInterpretation.BW : VipsInterpretation.RGB,
            XRes = 1.0,
            YRes = 1.0,
            // Pixels are already materialized; PixelsLazy hands them out.
            PixelsLazy = new Lazy<byte[]>(() => pixels),
        };

        // Metadata extraction uses the encoded bytes — do this *before* we
        // let them go out of scope. Same scanning logic as the byte-buffered
        // path; the only difference is jpegBytes won't outlive this method.
        var exifBlob = ExtractApp1Segment(jpegBytes, ExifIdentifier);
        if (exifBlob != null) image.MetadataBlobs["exif"] = exifBlob;
        var xmpBlob = ExtractApp1Segment(jpegBytes, XmpIdentifier);
        if (xmpBlob != null) image.MetadataBlobs["xmp"] = xmpBlob;
        var iccBlob = ExtractIccProfile(jpegBytes);
        if (iccBlob != null) image.MetadataBlobs["icc"] = iccBlob;

        SurfaceExifTags(exifBlob, image);

        return image;
    }

    /// <summary>
    /// Parse the EXIF blob via <see cref="VipsExifProfile"/> and surface
    /// each tag under <c>image.Metadata["exif:{TagName}"]</c>. Also lifts
    /// <see cref="VipsExifTag.Orientation"/> into the canonical
    /// <c>"orientation"</c> key consumed by <c>AutoOrient</c>.
    /// </summary>
    private static void SurfaceExifTags(byte[]? exifBlob, VipsImage image)
    {
        var profile = CosmoImage.Operations.Metadata.VipsExifProfile.TryParse(exifBlob);
        if (profile == null) return;

        foreach (var tag in profile.Tags)
        {
            var raw = profile.GetRaw(tag);
            image.Metadata[$"exif:{tag}"] = FormatExifValue(raw);
        }
        foreach (var gps in profile.GpsTags)
        {
            var raw = profile.GetGpsRaw(gps);
            image.Metadata[$"exif:GPS{gps}"] = FormatExifValue(raw);
        }

        var orientation = profile.GetValue<int>(CosmoImage.Operations.Metadata.VipsExifTag.Orientation);
        if (orientation >= 1 && orientation <= 8)
            image.Metadata["orientation"] = orientation.ToString();
    }

    private static string FormatExifValue(object? value)
    {
        if (value == null) return "";
        if (value is string s) return s;
        if (value is Array arr)
        {
            var parts = new string[arr.Length];
            for (int i = 0; i < arr.Length; i++) parts[i] = arr.GetValue(i)?.ToString() ?? "";
            return string.Join(" ", parts);
        }
        return value.ToString() ?? "";
    }

    /// <summary>
    /// JPEG component color space. JpegLibrary hands us raw component
    /// samples — we need to know what they encode before we can call
    /// the result "RGB".
    /// </summary>
    internal enum JpegColorSpace
    {
        Grayscale,    // 1 component, no conversion
        YCbCr,        // 3 components (default JFIF) — convert to RGB
        Rgb,          // 3 components, Adobe transform=0 — leave as-is
        YCCK,         // 4 components, Adobe transform=2 — convert to CMYK then leave
        Cmyk,         // 4 components, no Adobe marker or transform=0 — leave as-is
    }

    /// <summary>
    /// Inspect APP14 / SOF markers to decide what JPEG component samples
    /// represent. Most JPEGs are JFIF YCbCr; Adobe-tagged streams may
    /// declare RGB / YCCK / CMYK. Falls back to component-count heuristic
    /// when no marker pins it down: 1 = grayscale, 3 = YCbCr, 4 = CMYK.
    /// </summary>
    internal static JpegColorSpace DetectJpegColorSpace(byte[] jpeg, int components)
    {
        if (components == 1) return JpegColorSpace.Grayscale;

        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            return components == 4 ? JpegColorSpace.Cmyk : JpegColorSpace.YCbCr;

        int p = 2;
        while (p + 3 < jpeg.Length)
        {
            if (jpeg[p] != 0xFF) break;
            byte marker = jpeg[p + 1];
            if (marker == 0xD8 || marker == 0xD9 || marker == 0x01 ||
                (marker >= 0xD0 && marker <= 0xD7)) { p += 2; continue; }
            if (marker == 0xDA) break;  // SOS — past header
            int len = (jpeg[p + 2] << 8) | jpeg[p + 3];
            if (len < 2 || p + 2 + len > jpeg.Length) break;

            if (marker == 0xEE && len >= 14)
            {
                // APP14 "Adobe" marker: bytes 4..8 = "Adobe", byte 13 = transform.
                if (p + 2 + 14 <= jpeg.Length &&
                    jpeg[p + 4] == (byte)'A' && jpeg[p + 5] == (byte)'d' &&
                    jpeg[p + 6] == (byte)'o' && jpeg[p + 7] == (byte)'b' &&
                    jpeg[p + 8] == (byte)'e')
                {
                    byte transform = jpeg[p + 13];
                    if (components == 3)
                        return transform == 0 ? JpegColorSpace.Rgb : JpegColorSpace.YCbCr;
                    if (components == 4)
                        return transform == 2 ? JpegColorSpace.YCCK : JpegColorSpace.Cmyk;
                }
            }
            p += 2 + len;
        }
        // No definitive marker — JFIF defaults: 3 → YCbCr, 4 → CMYK.
        return components == 4 ? JpegColorSpace.Cmyk : JpegColorSpace.YCbCr;
    }

    /// <summary>
    /// Convert pixel buffer from the JPEG's native component encoding to
    /// RGB / CMYK / grayscale as appropriate. Operates in place on the
    /// buffer SimpleOutputWriter just filled.
    /// </summary>
    internal static void ConvertColorSpace(byte[] buf, int width, int height, int bands, JpegColorSpace cs)
    {
        if (cs == JpegColorSpace.Grayscale || cs == JpegColorSpace.Rgb || cs == JpegColorSpace.Cmyk)
            return;

        if (cs == JpegColorSpace.YCbCr && bands == 3)
        {
            int n = width * height;
            for (int i = 0; i < n; i++)
            {
                int o = i * 3;
                int Y  = buf[o];
                int Cb = buf[o + 1] - 128;
                int Cr = buf[o + 2] - 128;
                // BT.601 / JFIF YCbCr-to-RGB. Scaled integer math: ×1024
                // multipliers, +512 round, >>10 shift.
                int r = Y + ((1436 * Cr + 512) >> 10);
                int g = Y - ((352 * Cb + 731 * Cr + 512) >> 10);
                int b = Y + ((1815 * Cb + 512) >> 10);
                buf[o]     = (byte)Math.Clamp(r, 0, 255);
                buf[o + 1] = (byte)Math.Clamp(g, 0, 255);
                buf[o + 2] = (byte)Math.Clamp(b, 0, 255);
            }
            return;
        }

        if (cs == JpegColorSpace.YCCK && bands == 4)
        {
            // YCCK = YCbCr for first three components + K passes through.
            int n = width * height;
            for (int i = 0; i < n; i++)
            {
                int o = i * 4;
                int Y  = buf[o];
                int Cb = buf[o + 1] - 128;
                int Cr = buf[o + 2] - 128;
                int r = Y + ((1436 * Cr + 512) >> 10);
                int g = Y - ((352 * Cb + 731 * Cr + 512) >> 10);
                int b = Y + ((1815 * Cb + 512) >> 10);
                buf[o]     = (byte)Math.Clamp(r, 0, 255);
                buf[o + 1] = (byte)Math.Clamp(g, 0, 255);
                buf[o + 2] = (byte)Math.Clamp(b, 0, 255);
                // K stays at offset 3 unchanged.
            }
        }
    }

    /// <summary>
    /// Decode a JPEG bytestream into <paramref name="dst"/> at
    /// <paramref name="dstOff"/>, with rows of <paramref name="dstRowStride"/>
    /// bytes (so callers can blit into a larger image buffer at a non-zero
    /// origin). Output is in the JPEG's native component layout converted
    /// to display-friendly RGB / CMYK / Grayscale.
    /// </summary>
    internal static bool DecodeJpegToBuffer(byte[] jpegBytes,
        byte[] dst, int dstOff, int dstRowStride, int expectedWidth, int expectedHeight,
        out int components, bool forceRgbPassthrough = false)
    {
        components = 0;
        try
        {
            var decoder = new JpegDecoder();
            decoder.SetInput(jpegBytes);
            decoder.Identify();
            int w = decoder.Width;
            int h = decoder.Height;
            components = decoder.NumberOfComponents;
            if (w != expectedWidth || h != expectedHeight) return false;

            // Decode into a tightly-packed scratch buffer first; copy rows
            // into dst at the requested stride afterwards.
            var scratch = new byte[w * h * components];
            decoder.SetOutputWriter(new SimpleOutputWriter(w, h, components, scratch));
            decoder.Decode();
            // forceRgbPassthrough is set by callers (e.g. JPEG-in-TIFF
            // photometric=2) where the JPEG components are already R/G/B
            // even though the JPEG itself carries no Adobe APP14 marker
            // declaring it.
            if (!forceRgbPassthrough)
            {
                var cs = DetectJpegColorSpace(jpegBytes, components);
                ConvertColorSpace(scratch, w, h, components, cs);
            }

            int srcRow = w * components;
            for (int y = 0; y < h; y++)
                Buffer.BlockCopy(scratch, y * srcRow, dst, dstOff + y * dstRowStride, srcRow);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private class SimpleOutputWriter : JpegBlockOutputWriter
    {
        private readonly byte[] _buffer;
        private readonly int _width;
        private readonly int _height;
        private readonly int _components;

        public SimpleOutputWriter(int width, int height, int components, byte[] buffer)
        {
            _width = width;
            _height = height;
            _components = components;
            _buffer = buffer;
        }

        public override void WriteBlock(ref short blockRef, int componentIndex, int x, int y)
        {
            ref short blockStart = ref blockRef;
            for (int row = 0; row < 8; row++)
            {
                int pixelY = y + row;
                if (pixelY >= _height) break;

                for (int col = 0; col < 8; col++)
                {
                    int pixelX = x + col;
                    if (pixelX >= _width) break;

                    int offset = (pixelY * _width + pixelX) * _components + componentIndex;
                    short val = System.Runtime.CompilerServices.Unsafe.Add(ref blockStart, row * 8 + col);
                    // JpegLibrary's IDCT output is already shifted to
                    // unsigned [0, 255]; SimpleOutputWriter just clamps.
                    _buffer[offset] = (byte)Math.Clamp((int)val, 0, 255);
                }
            }
        }
    }

}
