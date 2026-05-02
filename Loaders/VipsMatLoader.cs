using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace CosmoImage.Loaders;

/// <summary>
/// Matlab Level 5 MAT-file (<c>.mat</c>) loader for numeric arrays.
/// Pure-C# implementation of the v5 tagged-binary format; no native deps.
///
/// <para>Layout: 128-byte ASCII descriptor + binary elements. Each element
/// has an 8-byte tag (4-byte type, 4-byte byte-count) followed by data,
/// padded to 8-byte alignment. Type 15 (<c>miCOMPRESSED</c>) wraps a
/// zlib-deflated nested element; we inflate and recurse. The container
/// type for arrays is type 14 (<c>miMATRIX</c>) which has sub-elements
/// for Array Flags, Dimensions, Name, and Real Part.</para>
///
/// <para>Loaded as a <see cref="VipsImage"/>: the first top-level numeric
/// matrix in the file (matlab convention is one image per variable).
/// Pixel data in matlab is column-major (Fortran order), so the loader
/// transposes to the row-major storage <see cref="VipsImage"/> uses.
/// 3D arrays with the third dim ∈ {1, 3, 4} map to multi-band images.</para>
///
/// <para>Out of scope: cell arrays, structs, sparse, char, object classes;
/// imaginary parts of complex arrays; arrays of rank ≥ 4; the v7.3 format
/// (HDF5-based, completely different layout that would require an HDF5
/// dependency).</para>
/// </summary>
public static class VipsMatLoader
{
    private const int HeaderSize = 128;

    // mi* data types
    private const uint MiInt8 = 1;
    private const uint MiUInt8 = 2;
    private const uint MiInt16 = 3;
    private const uint MiUInt16 = 4;
    private const uint MiInt32 = 5;
    private const uint MiUInt32 = 6;
    private const uint MiSingle = 7;
    private const uint MiDouble = 9;
    private const uint MiInt64 = 12;
    private const uint MiUInt64 = 13;
    private const uint MiMatrix = 14;
    private const uint MiCompressed = 15;
    private const uint MiUtf8 = 16;

    // mxCLASS enum (in the array flags element)
    private const byte MxCellClass = 1;
    private const byte MxStructClass = 2;
    private const byte MxObjectClass = 3;
    private const byte MxCharClass = 4;
    private const byte MxSparseClass = 5;
    private const byte MxDoubleClass = 6;
    private const byte MxSingleClass = 7;
    private const byte MxInt8Class = 8;
    private const byte MxUInt8Class = 9;
    private const byte MxInt16Class = 10;
    private const byte MxUInt16Class = 11;
    private const byte MxInt32Class = 12;
    private const byte MxUInt32Class = 13;
    private const byte MxInt64Class = 14;
    private const byte MxUInt64Class = 15;

    public static async ValueTask<bool> IsMatAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        // Check the 128-byte header for a v5 signature: descriptive ASCII at
        // the start (any printable text), version 0x0100/0x0001 at bytes
        // 124..125, "MI" or "IM" endian marker at 126..127.
        var sniff = await source.SniffAsync(HeaderSize, cancellationToken);
        if (sniff.Length < HeaderSize) return false;
        var s = sniff.Span;

        byte e0 = s[126], e1 = s[127];
        bool littleEndian;
        if (e0 == (byte)'I' && e1 == (byte)'M') littleEndian = true;
        else if (e0 == (byte)'M' && e1 == (byte)'I') littleEndian = false;
        else return false;

        ushort ver = littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(124, 2))
            : BinaryPrimitives.ReadUInt16BigEndian(s.Slice(124, 2));
        // v5 uses 0x0100; v7.3 uses 0x0200 but is HDF5-based and out of scope.
        return ver == 0x0100;
    }

    public static async ValueTask<VipsImage?> LoadAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        if (!await IsMatAsync(source, cancellationToken)) return null;

        var ms = new MemoryStream();
        var rawBuffer = new byte[81920];
        while (true)
        {
            int read = await source.ReadAsync(rawBuffer, cancellationToken);
            if (read == 0) break;
            ms.Write(rawBuffer, 0, read);
        }
        var bytes = ms.ToArray();
        return Decode(bytes);
    }

    private static VipsImage? Decode(byte[] bytes)
    {
        if (bytes.Length < HeaderSize) return null;
        bool le = bytes[126] == (byte)'I' && bytes[127] == (byte)'M';
        if (!le && !(bytes[126] == (byte)'M' && bytes[127] == (byte)'I')) return null;

        // Walk top-level elements until we find a numeric miMATRIX.
        int pos = HeaderSize;
        while (pos + 8 <= bytes.Length)
        {
            if (!ReadTag(bytes, pos, le, out uint type, out uint dataLen, out int tagSize, out byte[]? smallData)) return null;
            int elemDataStart = pos + tagSize;

            if (type == MiCompressed)
            {
                // Inflate the zlib stream into a fresh buffer; that buffer is
                // itself a sequence of elements (typically just one miMATRIX).
                using var compressed = new MemoryStream(bytes, elemDataStart, (int)dataLen);
                using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
                using var inflated = new MemoryStream();
                zlib.CopyTo(inflated);
                var inner = inflated.ToArray();
                var img = TryReadMatrixFromBytes(inner, 0, le);
                if (img != null) return img;
            }
            else if (type == MiMatrix)
            {
                var img = ReadMatrix(bytes, elemDataStart, (int)dataLen, le);
                if (img != null) return img;
            }
            // Other top-level element types are unusual; skip them.

            // Advance past this element, padded to 8-byte boundary.
            int totalLen = tagSize + (smallData != null ? 0 : (int)dataLen);
            int padded = (totalLen + 7) & ~7;
            pos += padded;
        }
        return null;
    }

    /// <summary>
    /// Read a v5 element tag at <paramref name="pos"/>. Returns the data
    /// type, payload length, and the actual tag size in bytes (4 for the
    /// "small data element" form where data ≤ 4 bytes is packed into the
    /// tag; 8 otherwise). When the small form is used, <paramref name="smallData"/>
    /// holds the inline payload.
    /// </summary>
    private static bool ReadTag(byte[] bytes, int pos, bool le, out uint type, out uint dataLen, out int tagSize, out byte[]? smallData)
    {
        type = 0; dataLen = 0; tagSize = 0; smallData = null;
        if (pos + 8 > bytes.Length) return false;

        // Small data element form: high 16 bits of "type" field are the byte
        // count when non-zero, low 16 bits are the type. The 4 data bytes
        // immediately follow.
        uint first4 = le
            ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos, 4))
            : BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(pos, 4));
        ushort hi = (ushort)(first4 >> 16);
        ushort lo = (ushort)(first4 & 0xFFFF);
        if (hi != 0)
        {
            // Small form: lo = type, hi = byte count (≤ 4).
            type = lo;
            dataLen = hi;
            smallData = new byte[4];
            Buffer.BlockCopy(bytes, pos + 4, smallData, 0, 4);
            tagSize = 8; // small form is always 8 bytes total (4 tag + 4 data)
            return true;
        }

        // Regular form: 4-byte type + 4-byte byte count.
        type = first4;
        dataLen = le
            ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos + 4, 4))
            : BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(pos + 4, 4));
        tagSize = 8;
        return true;
    }

    private static VipsImage? TryReadMatrixFromBytes(byte[] bytes, int pos, bool le)
    {
        if (pos + 8 > bytes.Length) return null;
        if (!ReadTag(bytes, pos, le, out uint type, out uint dataLen, out int tagSize, out _)) return null;
        if (type != MiMatrix) return null;
        return ReadMatrix(bytes, pos + tagSize, (int)dataLen, le);
    }

    /// <summary>
    /// Decode the body of a miMATRIX element. Sub-elements come in fixed
    /// order: (1) Array Flags, (2) Dimensions, (3) Array Name, (4) Real
    /// Part. We honour that order rather than scanning for tags.
    /// </summary>
    private static VipsImage? ReadMatrix(byte[] bytes, int start, int length, bool le)
    {
        int end = start + length;
        if (end > bytes.Length) return null;
        int p = start;

        // ---- Array Flags (miUINT32, 8 bytes) ----
        if (!ReadTag(bytes, p, le, out uint t1, out uint l1, out int ts1, out byte[]? sd1)) return null;
        if (t1 != MiUInt32 || l1 != 8) return null;
        byte[] flagsBuf;
        if (sd1 != null) { flagsBuf = sd1; }
        else { flagsBuf = new byte[8]; Buffer.BlockCopy(bytes, p + ts1, flagsBuf, 0, 8); }
        // First 4 bytes: flags + class. Class is the low byte.
        uint flagsWord = le
            ? BinaryPrimitives.ReadUInt32LittleEndian(flagsBuf.AsSpan(0, 4))
            : BinaryPrimitives.ReadUInt32BigEndian(flagsBuf.AsSpan(0, 4));
        byte cls = (byte)(flagsWord & 0xFF);
        bool complex = (flagsWord & 0x0800) != 0;
        if (complex) return null; // imaginary parts not handled
        p = AdvancePadded(p, ts1, sd1, (int)l1);

        // ---- Dimensions Array (miINT32, ndim × 4 bytes) ----
        if (!ReadTag(bytes, p, le, out uint t2, out uint l2, out int ts2, out byte[]? sd2)) return null;
        if (t2 != MiInt32) return null;
        int ndim = (int)l2 / 4;
        if (ndim < 2 || ndim > 3) return null; // 2D / 3D only
        var dims = new int[ndim];
        byte[] dimSrc = sd2 ?? bytes;
        int dimOff = sd2 != null ? 0 : p + ts2;
        for (int i = 0; i < ndim; i++)
        {
            dims[i] = le
                ? BinaryPrimitives.ReadInt32LittleEndian(dimSrc.AsSpan(dimOff + i * 4, 4))
                : BinaryPrimitives.ReadInt32BigEndian(dimSrc.AsSpan(dimOff + i * 4, 4));
        }
        p = AdvancePadded(p, ts2, sd2, (int)l2);

        // ---- Array Name (miINT8, variable) ---- (skip; we don't surface it)
        if (!ReadTag(bytes, p, le, out uint t3, out uint l3, out int ts3, out byte[]? sd3)) return null;
        if (t3 != MiInt8 && t3 != MiUtf8) return null;
        p = AdvancePadded(p, ts3, sd3, (int)l3);

        // ---- Real Part ----
        if (!ReadTag(bytes, p, le, out uint dataType, out uint dataBytes, out int ts4, out byte[]? sd4)) return null;

        int H = dims[0]; // matlab rows
        int W = dims[1]; // matlab cols
        int planes = ndim == 3 ? dims[2] : 1;
        if (H <= 0 || W <= 0 || planes <= 0 || planes > 4) return null;

        long expectedSamples = (long)H * W * planes;
        int bytesPerSample = SizeOf(dataType);
        if (bytesPerSample == 0) return null;
        if ((long)dataBytes != expectedSamples * bytesPerSample) return null;

        // Map matlab class → output VipsImage band format. UInt8 stays
        // UChar; everything else widens to Float for downstream consistency.
        bool outFloat = cls != MxUInt8Class;
        int outBytesPerSample = outFloat ? 4 : 1;
        var pixels = new byte[W * H * planes * outBytesPerSample];

        byte[] dataSrc = sd4 ?? bytes;
        int dataOff = sd4 != null ? 0 : p + ts4;

        // Matlab is column-major: source[k*H*W + c*H + r] is sample at
        // (row r, col c, plane k). Transpose into row-major interleaved.
        for (int k = 0; k < planes; k++)
        {
            for (int r = 0; r < H; r++)
            {
                for (int c = 0; c < W; c++)
                {
                    long srcSampleIdx = (long)k * H * W + (long)c * H + r;
                    int srcOff = dataOff + (int)(srcSampleIdx * bytesPerSample);
                    double v = ReadSample(dataSrc, srcOff, dataType, le);

                    int dstOff = (r * W + c) * planes * outBytesPerSample + k * outBytesPerSample;
                    if (outFloat)
                        BinaryPrimitives.WriteSingleLittleEndian(pixels.AsSpan(dstOff, 4), (float)v);
                    else
                        pixels[dstOff] = (byte)Math.Clamp(v, 0, 255);
                }
            }
        }

        return new VipsImage
        {
            Width = W,
            Height = H,
            Bands = planes,
            BandFormat = outFloat ? VipsBandFormat.Float : VipsBandFormat.UChar,
            Interpretation = planes == 1 ? VipsInterpretation.BW : VipsInterpretation.RGB,
            Coding = VipsCoding.None,
            XRes = 1.0,
            YRes = 1.0,
            PixelsLazy = new Lazy<byte[]>(() => pixels),
        };
    }

    private static int AdvancePadded(int pos, int tagSize, byte[]? smallData, int dataLen)
    {
        if (smallData != null) return pos + 8; // small form is always 8 bytes
        int total = tagSize + dataLen;
        return pos + ((total + 7) & ~7);
    }

    private static int SizeOf(uint miType) => miType switch
    {
        MiInt8 or MiUInt8 => 1,
        MiInt16 or MiUInt16 => 2,
        MiInt32 or MiUInt32 or MiSingle => 4,
        MiDouble or MiInt64 or MiUInt64 => 8,
        _ => 0,
    };

    private static double ReadSample(byte[] src, int off, uint miType, bool le) => miType switch
    {
        MiUInt8 => src[off],
        MiInt8 => (sbyte)src[off],
        MiUInt16 => le ? BinaryPrimitives.ReadUInt16LittleEndian(src.AsSpan(off, 2))
                       : BinaryPrimitives.ReadUInt16BigEndian(src.AsSpan(off, 2)),
        MiInt16 => le ? BinaryPrimitives.ReadInt16LittleEndian(src.AsSpan(off, 2))
                       : BinaryPrimitives.ReadInt16BigEndian(src.AsSpan(off, 2)),
        MiUInt32 => le ? BinaryPrimitives.ReadUInt32LittleEndian(src.AsSpan(off, 4))
                       : BinaryPrimitives.ReadUInt32BigEndian(src.AsSpan(off, 4)),
        MiInt32 => le ? BinaryPrimitives.ReadInt32LittleEndian(src.AsSpan(off, 4))
                       : BinaryPrimitives.ReadInt32BigEndian(src.AsSpan(off, 4)),
        MiSingle => le ? BinaryPrimitives.ReadSingleLittleEndian(src.AsSpan(off, 4))
                        : BinaryPrimitives.ReadSingleBigEndian(src.AsSpan(off, 4)),
        MiDouble => le ? BinaryPrimitives.ReadDoubleLittleEndian(src.AsSpan(off, 8))
                        : BinaryPrimitives.ReadDoubleBigEndian(src.AsSpan(off, 8)),
        _ => 0,
    };
}
