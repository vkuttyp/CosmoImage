using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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

        // Native TIFF decode path. Unsupported layouts return null rather than
        // falling back to Magick.NET.
        var pure = PureTiffDecoder.TryDecode(imageBytes);
        if (pure == null) return null;

        AttachTiffMetadata(imageBytes, pure);
        return pure;
    }

    /// <summary>
    /// Best-effort TIFF metadata extraction from the primary IFD.
    /// Reads orientation, ICC/XMP blobs, and ImageDescription without
    /// depending on Magick.NET.
    /// </summary>
    private static void AttachTiffMetadata(byte[] imageBytes, VipsImage image)
    {
        if (!TryReadPrimaryIfdMetadata(imageBytes, out var metadata))
            return;

        if (metadata.IccProfile != null)
            image.MetadataBlobs["icc"] = metadata.IccProfile;
        if (metadata.XmpProfile != null)
            image.MetadataBlobs["xmp"] = metadata.XmpProfile;
        if (metadata.Orientation is int orientation && orientation >= 1 && orientation <= 8)
            image.Metadata["orientation"] = orientation.ToString();
        if (!string.IsNullOrEmpty(metadata.ImageDescription))
            CaptureImageDescription(metadata.ImageDescription, image);
    }

    private static void CaptureImageDescription(string imageDescription, VipsImage image)
    {
        image.Metadata["tiff:image-description"] = imageDescription;

        // OME-TIFF carries OME-XML in this same tag. Surface the XML under a
        // dedicated key and try to populate XRes/YRes from PhysicalSizeX/Y.
        if (CosmoImage.Core.VipsOmeTiff.LooksLikeOmeXml(imageDescription))
        {
            image.Metadata["ome:xml"] = imageDescription;
            CosmoImage.Core.VipsOmeTiff.PopulatePhysicalSize(image);
            CosmoImage.Core.VipsOmeTiff.PopulatePixelsLayout(image);
        }
    }

    /// <summary>
    /// Streaming TIFF load currently reuses the byte-buffered native loader.
    /// This preserves semantics while avoiding a separate Magick-backed path.
    /// </summary>
    public static async ValueTask<VipsImage?> LoadStreamingAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        return await LoadAsync(source, cancellationToken);
    }

    private static bool TryReadPrimaryIfdMetadata(byte[] bytes, out TiffPrimaryMetadata metadata)
    {
        metadata = default;
        if (bytes.Length < 8)
            return false;

        bool le;
        if (bytes[0] == 0x49 && bytes[1] == 0x49) le = true;
        else if (bytes[0] == 0x4D && bytes[1] == 0x4D) le = false;
        else return false;

        ushort magic = ReadU16(bytes, 2, le);
        bool bigTiff;
        ulong ifdOffset;
        if (magic == 0x002A)
        {
            bigTiff = false;
            ifdOffset = ReadU32(bytes, 4, le);
        }
        else if (magic == 0x002B)
        {
            if (bytes.Length < 16) return false;
            ushort offsetSize = ReadU16(bytes, 4, le);
            ushort reserved = ReadU16(bytes, 6, le);
            if (offsetSize != 8 || reserved != 0) return false;
            bigTiff = true;
            ifdOffset = ReadU64(bytes, 8, le);
        }
        else
        {
            return false;
        }

        return TryReadIfdMetadata(bytes, le, bigTiff, ifdOffset, out metadata);
    }

    private static bool TryReadIfdMetadata(byte[] bytes, bool le, bool bigTiff, ulong ifdOffset, out TiffPrimaryMetadata metadata)
    {
        metadata = default;
        if (ifdOffset == 0 || ifdOffset > (ulong)bytes.Length)
            return false;

        int countSize = bigTiff ? 8 : 2;
        int entrySize = bigTiff ? 20 : 12;
        int nextSize = bigTiff ? 8 : 4;
        if (ifdOffset + (ulong)countSize > (ulong)bytes.Length)
            return false;

        ulong numEntries = bigTiff
            ? ReadU64(bytes, (int)ifdOffset, le)
            : ReadU16(bytes, (int)ifdOffset, le);
        if (numEntries > 65535)
            return false;

        int entriesStart = (int)ifdOffset + countSize;
        long afterEntries = (long)entriesStart + (long)numEntries * entrySize;
        if (afterEntries + nextSize > bytes.Length)
            return false;

        for (ulong i = 0; i < numEntries; i++)
        {
            int entryOffset = entriesStart + (int)i * entrySize;
            ushort tag = ReadU16(bytes, entryOffset, le);
            ushort type = ReadU16(bytes, entryOffset + 2, le);
            ulong count = bigTiff
                ? ReadU64(bytes, entryOffset + 4, le)
                : ReadU32(bytes, entryOffset + 4, le);

            switch (tag)
            {
                case 270:
                {
                    var raw = ReadValueBytes(bytes, entryOffset, type, count, le, bigTiff);
                    if (raw != null)
                        metadata.ImageDescription = Encoding.UTF8.GetString(raw).TrimEnd('\0');
                    break;
                }
                case 274:
                {
                    var orientation = ReadUnsignedValue(bytes, entryOffset, type, count, le, bigTiff);
                    if (orientation.HasValue && orientation.Value <= int.MaxValue)
                        metadata.Orientation = (int)orientation.Value;
                    break;
                }
                case 700:
                {
                    metadata.XmpProfile = ReadValueBytes(bytes, entryOffset, type, count, le, bigTiff);
                    break;
                }
                case 34675:
                {
                    metadata.IccProfile = ReadValueBytes(bytes, entryOffset, type, count, le, bigTiff);
                    break;
                }
            }
        }

        return true;
    }

    private static byte[]? ReadValueBytes(byte[] bytes, int entryOffset, ushort type, ulong count, bool le, bool bigTiff)
    {
        int typeSize = GetTypeSize(type);
        if (typeSize == 0) return null;
        if (count > ulong.MaxValue / (ulong)typeSize) return null;

        ulong byteCount = count * (ulong)typeSize;
        int inlineBytes = bigTiff ? 8 : 4;
        int valueOffset = entryOffset + (bigTiff ? 12 : 8);

        if (byteCount == 0) return Array.Empty<byte>();
        if (byteCount <= (ulong)inlineBytes)
        {
            if ((ulong)valueOffset + byteCount > (ulong)bytes.Length) return null;
            var raw = new byte[(int)byteCount];
            Buffer.BlockCopy(bytes, valueOffset, raw, 0, raw.Length);
            return raw;
        }

        ulong dataOffset = bigTiff
            ? ReadU64(bytes, valueOffset, le)
            : ReadU32(bytes, valueOffset, le);
        if (dataOffset + byteCount > (ulong)bytes.Length || byteCount > int.MaxValue)
            return null;

        var blob = new byte[(int)byteCount];
        Buffer.BlockCopy(bytes, (int)dataOffset, blob, 0, blob.Length);
        return blob;
    }

    private static ulong? ReadUnsignedValue(byte[] bytes, int entryOffset, ushort type, ulong count, bool le, bool bigTiff)
    {
        if (count != 1) return null;

        var raw = ReadValueBytes(bytes, entryOffset, type, count, le, bigTiff);
        if (raw == null) return null;

        return type switch
        {
            1 or 6 or 7 when raw.Length >= 1 => raw[0],
            3 or 8 when raw.Length >= 2 => le
                ? (ulong)(raw[0] | (raw[1] << 8))
                : (ulong)((raw[0] << 8) | raw[1]),
            4 or 9 or 11 when raw.Length >= 4 => ReadU32(raw, 0, le),
            16 or 17 or 18 when raw.Length >= 8 => ReadU64(raw, 0, le),
            _ => null
        };
    }

    private static int GetTypeSize(ushort type) => type switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 or 11 => 4,
        5 or 10 or 12 or 16 or 17 or 18 => 8,
        _ => 0,
    };

    private static ushort ReadU16(byte[] buf, int offset, bool le) =>
        le ? (ushort)(buf[offset] | (buf[offset + 1] << 8))
           : (ushort)((buf[offset] << 8) | buf[offset + 1]);

    private static uint ReadU32(byte[] buf, int offset, bool le) =>
        le ? (uint)(buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16) | (buf[offset + 3] << 24))
           : (uint)((buf[offset] << 24) | (buf[offset + 1] << 16) | (buf[offset + 2] << 8) | buf[offset + 3]);

    private static ulong ReadU64(byte[] buf, int offset, bool le)
    {
        uint lo = ReadU32(buf, offset + (le ? 0 : 4), le);
        uint hi = ReadU32(buf, offset + (le ? 4 : 0), le);
        return ((ulong)hi << 32) | lo;
    }

    private struct TiffPrimaryMetadata
    {
        public int? Orientation;
        public string? ImageDescription;
        public byte[]? XmpProfile;
        public byte[]? IccProfile;
    }
}
