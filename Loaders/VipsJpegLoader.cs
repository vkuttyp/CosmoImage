using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
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
        var pixelsLazy = new Lazy<byte[]>(() =>
        {
            int stride = width * bands;
            var buf = new byte[stride * height];
            var innerDecoder = new JpegDecoder();
            innerDecoder.SetInput(jpegBytes);
            innerDecoder.SetOutputWriter(new SimpleOutputWriter(width, height, bands, buf));
            innerDecoder.Decode();
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
        // We scan markers ourselves rather than going through MetadataExtractor
        // because we need the untouched payload bytes, not parsed tag values.
        var exifBlob = ExtractApp1Segment(jpegBytes, ExifIdentifier);
        if (exifBlob != null) image.MetadataBlobs["exif"] = exifBlob;
        var xmpBlob = ExtractApp1Segment(jpegBytes, XmpIdentifier);
        if (xmpBlob != null) image.MetadataBlobs["xmp"] = xmpBlob;
        var iccBlob = ExtractIccProfile(jpegBytes);
        if (iccBlob != null) image.MetadataBlobs["icc"] = iccBlob;

        try
        {
            var directories = MetadataExtractor.ImageMetadataReader.ReadMetadata(new MemoryStream(jpegBytes));
            foreach (var directory in directories)
            {
                foreach (var tag in directory.Tags)
                {
                    image.Metadata[$"{directory.Name}:{tag.Name}"] = tag.Description ?? "";
                }
            }

            // Extract EXIF orientation as a raw int for AutoOrient. The
            // description-keyed copy above is human-readable text whose wording
            // varies between MetadataExtractor versions, so we surface the int
            // separately under the canonical "orientation" key.
            var ifd0 = directories
                .OfType<MetadataExtractor.Formats.Exif.ExifIfd0Directory>()
                .FirstOrDefault();
            if (ifd0 != null)
            {
                var raw = ifd0.GetObject(MetadataExtractor.Formats.Exif.ExifDirectoryBase.TagOrientation);
                if (raw != null)
                {
                    try
                    {
                        image.Metadata["orientation"] = Convert.ToInt32(raw).ToString();
                    }
                    catch { /* tag exists but isn't numeric */ }
                }
            }
        }
        catch { /* Ignore metadata errors */ }

        return image;
    }

    public static async ValueTask<VipsImage?> LoadHeaderAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        return await LoadAsync(source, cancellationToken);
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
                    _buffer[offset] = (byte)Math.Clamp(val + 128, 0, 255);
                }
            }
        }
    }

}
