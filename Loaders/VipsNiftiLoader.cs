using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CosmoImage.Loaders;

/// <summary>
/// NIfTI-1 (Neuroimaging Informatics Technology Initiative) loader.
/// Pure-C# implementation; no native deps.
///
/// <para>Layout: 348-byte fixed binary header followed by 4 padding bytes
/// and pixel data starting at <c>vox_offset</c> (typically 352 for the
/// single-file <c>.nii</c> form). The header carries the magic identifier
/// at byte 344: <c>"n+1\0"</c> for single-file or <c>"ni1\0"</c> for the
/// older paired <c>.hdr</c> + <c>.img</c> form. We currently only handle
/// the single-file case — paired files would need a parallel data-file
/// fetch the source-stream API doesn't model cleanly.</para>
///
/// <para>Endianness auto-detect uses the canonical ANALYZE/NIfTI rule:
/// <c>dim[0]</c> at offset 40 is the number of dimensions and must be in
/// 1..7. If a native-endian read produces a value outside that range,
/// the file was written with the opposite byte order and we byte-swap
/// the entire header before continuing.</para>
///
/// <para>Supported: 2D and 3D images. Datatypes 2 (uint8 → UChar), 16
/// (float32 → Float), 64 (float64 → Float, downcast). Linear value
/// transform via <c>scl_slope</c>/<c>scl_inter</c> applied during decode.
/// 4D+ data cubes (fMRI time series) and signed-integer datatypes still
/// need their own work.</para>
/// </summary>
public static class VipsNiftiLoader
{
    private const int HeaderSize = 348;

    public static async ValueTask<bool> IsNiftiAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        // Magic at byte 344: "n+1\0" (single-file) or "ni1\0" (paired).
        // We need to peek at byte 344, so sniff at least 348 bytes.
        var sniff = await source.SniffAsync(HeaderSize, cancellationToken);
        if (sniff.Length < HeaderSize) return false;
        var s = sniff.Span;
        bool nplus1 = s[344] == (byte)'n' && s[345] == (byte)'+' && s[346] == (byte)'1' && s[347] == 0;
        bool ni1 = s[344] == (byte)'n' && s[345] == (byte)'i' && s[346] == (byte)'1' && s[347] == 0;
        return nplus1 || ni1;
    }

    public static async ValueTask<VipsImage?> LoadAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        if (!await IsNiftiAsync(source, cancellationToken)) return null;
        var bytes = await DrainAsync(source, cancellationToken);
        return Decode(bytes);
    }

    /// <summary>
    /// Paired-form NIfTI load: header in <paramref name="headerSource"/>
    /// (typically <c>xxx.hdr</c>) and pixel data in
    /// <paramref name="dataSource"/> (typically <c>xxx.img</c>). The
    /// header file's magic must be <c>"ni1\0"</c> and its
    /// <c>vox_offset</c> field is honoured against the data file.
    /// </summary>
    public static async ValueTask<VipsImage?> LoadPairedAsync(IVipsSource headerSource, IVipsSource dataSource, CancellationToken cancellationToken = default)
    {
        if (headerSource == null) throw new ArgumentNullException(nameof(headerSource));
        if (dataSource == null) throw new ArgumentNullException(nameof(dataSource));

        var headerBytes = await DrainAsync(headerSource, cancellationToken);
        if (headerBytes.Length < HeaderSize) return null;
        // Don't gate on IsNiftiAsync here — that helper accepts both single
        // and paired magic, but the paired path specifically requires ni1.
        // DecodePaired enforces it.
        var pixelBytes = await DrainAsync(dataSource, cancellationToken);
        return DecodePaired(headerBytes, pixelBytes);
    }

    private static async ValueTask<byte[]> DrainAsync(IVipsSource source, CancellationToken cancellationToken)
    {
        var ms = new MemoryStream();
        var rawBuffer = new byte[81920];
        while (true)
        {
            int read = await source.ReadAsync(rawBuffer, cancellationToken);
            if (read == 0) break;
            ms.Write(rawBuffer, 0, read);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Single-file <c>.nii</c> decode. Magic check + delegate to the
    /// shared header-driven decoder using the same byte array as both
    /// header and pixel source.
    /// </summary>
    private static VipsImage? Decode(byte[] bytes)
    {
        if (bytes.Length < HeaderSize) return null;
        // Magic must be n+1 (single-file). ni1 paired form goes through
        // DecodePaired below.
        if (!(bytes[344] == 'n' && bytes[345] == '+' && bytes[346] == '1' && bytes[347] == 0))
            return null;
        return DecodeFromHeader(bytes, pixelData: bytes);
    }

    /// <summary>
    /// Paired-form decode. <paramref name="headerBytes"/> is the
    /// <c>.hdr</c> file (348 bytes); <paramref name="pixelData"/> is the
    /// matching <c>.img</c> file (raw pixels starting at byte
    /// <c>vox_offset</c>, which is typically 0). Magic must be
    /// <c>"ni1\0"</c>.
    /// </summary>
    private static VipsImage? DecodePaired(byte[] headerBytes, byte[] pixelData)
    {
        if (headerBytes.Length < HeaderSize) return null;
        if (!(headerBytes[344] == 'n' && headerBytes[345] == 'i' && headerBytes[346] == '1' && headerBytes[347] == 0))
            return null;
        return DecodeFromHeader(headerBytes, pixelData);
    }

    /// <summary>
    /// Shared decoder: parses the 348-byte header from <paramref name="bytes"/>
    /// and reads the pixel block from <paramref name="pixelData"/> at
    /// <c>vox_offset</c>. The two arguments are the same array for
    /// single-file <c>.nii</c> and different arrays for paired
    /// <c>.hdr/.img</c>. <c>vox_offset</c> is taken verbatim from the
    /// header — 352 (typical) for single-file, 0 (typical) for paired.
    /// </summary>
    private static VipsImage? DecodeFromHeader(byte[] bytes, byte[] pixelData)
    {
        // Auto-detect endianness via dim[0] (int16 at offset 40, must be 1..7).
        bool littleEndian = true;
        short dim0Le = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(40, 2));
        if (dim0Le < 1 || dim0Le > 7)
        {
            short dim0Be = BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(40, 2));
            if (dim0Be < 1 || dim0Be > 7) return null;
            littleEndian = false;
        }

        short ReadShort(int off) => littleEndian
            ? BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(off, 2))
            : BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(off, 2));
        int ReadInt(int off) => littleEndian
            ? BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(off, 4))
            : BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(off, 4));
        float ReadFloat(int off) => littleEndian
            ? BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(off, 4))
            : BinaryPrimitives.ReadSingleBigEndian(bytes.AsSpan(off, 4));

        // sizeof_hdr (offset 0) must be 348.
        if (ReadInt(0) != HeaderSize) return null;

        int ndims = ReadShort(40);
        if (ndims < 2 || ndims > 3) return null; // 2D and 3D only for first cut

        int nx = ReadShort(42);
        int ny = ReadShort(44);
        int nz = ndims == 3 ? ReadShort(46) : 1;
        if (nx <= 0 || ny <= 0 || nz <= 0) return null;

        short datatype = ReadShort(70);
        short bitpix = ReadShort(72);
        // pixdim[0] at offset 76 is the qfac sign; pixdim[1..3] at 80, 84, 88.
        float pixdimX = ReadFloat(80);
        float pixdimY = ReadFloat(84);
        float voxOffset = ReadFloat(108);
        float sclSlope = ReadFloat(112);
        float sclInter = ReadFloat(116);

        // scl_slope = 0 means "no scaling applied" per spec — treat as identity.
        if (sclSlope == 0f) sclSlope = 1f;

        int dataOffset = (int)voxOffset;
        // Paired form: vox_offset is an offset within the .img file (usually 0).
        // Single-file: vox_offset is past the header (usually 352).
        if (dataOffset < 0 || dataOffset > pixelData.Length) return null;

        // Datatype dispatch. Output band-format is UChar for type 2 with no
        // value transform; Float otherwise. The image's "bands" axis is
        // mapped from the 3rd NIfTI dim (z) when present — common in
        // pathology slides and color-merged neuroimaging exports — capped
        // at 4 bands per VipsImage convention.
        int bytesPerSample = bitpix / 8;
        if (bytesPerSample <= 0) return null;

        int planes = nz;
        if (planes > 4) return null; // multi-slice → too many bands; defer

        bool needsTransform = sclSlope != 1f || sclInter != 0f;
        bool outFloat;
        switch (datatype)
        {
            case 2:    // uint8
                outFloat = needsTransform; // promotes to Float when scaling applied
                break;
            case 4:    // int16
            case 8:    // int32
            case 16:   // float32
            case 64:   // float64
            case 256:  // int8
            case 512:  // uint16
            case 768:  // uint32
                // Integer datatypes promote to Float on output: NIfTI int16/int32
                // routinely cover ranges far outside [0, 255] (raw scanner counts,
                // signed offsets) so byte truncation would destroy the signal.
                outFloat = true;
                break;
            default:
                // Other NIfTI types (complex, RGB packed, int64, etc.) deferred.
                return null;
        }
        int expectedBytesPerSampleByDt = datatype switch
        {
            2 => 1,
            4 => 2,
            8 => 4,
            16 => 4,
            64 => 8,
            256 => 1,
            512 => 2,
            768 => 4,
            _ => 0,
        };
        if (bytesPerSample != expectedBytesPerSampleByDt) return null;

        long sampleCount = (long)nx * ny * planes;
        long dataLen = sampleCount * bytesPerSample;
        if (dataOffset + dataLen > pixelData.Length) return null;

        int outBands = planes;
        int outBytesPerSample = outFloat ? 4 : 1;
        var pixels = new byte[nx * ny * outBands * outBytesPerSample];

        // Decode planar (NIfTI stores X fastest, then Y, then Z — i.e. each
        // slice as a contiguous nx*ny block). VipsImage uses interleaved
        // band order, so bands need to be transposed.
        for (int p = 0; p < planes; p++)
        {
            long planeBase = (long)dataOffset + (long)p * nx * ny * bytesPerSample;
            for (int y = 0; y < ny; y++)
            {
                for (int x = 0; x < nx; x++)
                {
                    long srcOff = planeBase + ((long)y * nx + x) * bytesPerSample;
                    double sample = datatype switch
                    {
                        2 => pixelData[srcOff],
                        4 => ReadInt16At(pixelData, (int)srcOff, littleEndian),
                        8 => ReadInt32At(pixelData, (int)srcOff, littleEndian),
                        16 => ReadFloatAt(pixelData, (int)srcOff, littleEndian),
                        64 => ReadDoubleAt(pixelData, (int)srcOff, littleEndian),
                        256 => (sbyte)pixelData[srcOff],
                        512 => ReadUInt16At(pixelData, (int)srcOff, littleEndian),
                        768 => ReadUInt32At(pixelData, (int)srcOff, littleEndian),
                        _ => 0,
                    };
                    if (needsTransform) sample = sample * sclSlope + sclInter;

                    int dstOff = (y * nx + x) * outBands * outBytesPerSample + p * outBytesPerSample;
                    if (outFloat)
                        BinaryPrimitives.WriteSingleLittleEndian(pixels.AsSpan(dstOff, 4), (float)sample);
                    else
                        pixels[dstOff] = (byte)Math.Clamp(sample, 0, 255);
                }
            }
        }

        var image = new VipsImage
        {
            Width = nx,
            Height = ny,
            Bands = outBands,
            BandFormat = outFloat ? VipsBandFormat.Float : VipsBandFormat.UChar,
            Interpretation = outBands == 1 ? VipsInterpretation.BW : VipsInterpretation.RGB,
            Coding = VipsCoding.None,
            // pixdim[1] / pixdim[2] are voxel size in xyzt_units (default mm).
            // libvips XRes/YRes is "pixels per mm" — invert.
            XRes = pixdimX > 0 ? 1.0 / pixdimX : 1.0,
            YRes = pixdimY > 0 ? 1.0 / pixdimY : 1.0,
            PixelsLazy = new Lazy<byte[]>(() => pixels),
        };

        // Surface useful header fields so a load → save round-trip preserves
        // them and downstream tools can inspect without re-parsing the file.
        image.Metadata["nifti:datatype"] = datatype.ToString();
        image.Metadata["nifti:dim"] = ndims.ToString();
        if (sclSlope != 1f || sclInter != 0f)
        {
            image.Metadata["nifti:scl_slope"] = sclSlope.ToString(System.Globalization.CultureInfo.InvariantCulture);
            image.Metadata["nifti:scl_inter"] = sclInter.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        // descrip[80] at offset 148.
        var descrip = System.Text.Encoding.ASCII.GetString(bytes, 148, 80).TrimEnd('\0', ' ');
        if (descrip.Length > 0) image.Metadata["nifti:descrip"] = descrip;

        // qform / sform spatial-orientation fields. Codes ≠ 0 indicate
        // the matrices are present; we surface them as comma-separated
        // float strings so a save→load round-trip preserves them
        // exactly. We don't apply the spatial transform (VipsImage has
        // no world-coordinate model) — these are pass-through metadata.
        short qformCode = ReadShort(252);
        short sformCode = ReadShort(254);
        if (qformCode != 0)
        {
            image.Metadata["nifti:qform_code"] = qformCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
            float qb = ReadFloat(256), qc = ReadFloat(260), qd = ReadFloat(264);
            float qx = ReadFloat(268), qy = ReadFloat(272), qz = ReadFloat(276);
            image.Metadata["nifti:quatern"] = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0},{1},{2}", qb, qc, qd);
            image.Metadata["nifti:qoffset"] = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0},{1},{2}", qx, qy, qz);
        }
        if (sformCode != 0)
        {
            image.Metadata["nifti:sform_code"] = sformCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
            for (int row = 0; row < 3; row++)
            {
                int rowOff = 280 + row * 16;
                float a = ReadFloat(rowOff), b = ReadFloat(rowOff + 4),
                      c = ReadFloat(rowOff + 8), d = ReadFloat(rowOff + 12);
                image.Metadata[$"nifti:srow_{(char)('x' + row)}"] = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0},{1},{2},{3}", a, b, c, d);
            }
        }
        return image;
    }

    private static double ReadFloatAt(byte[] bytes, int offset, bool le)
        => le
            ? BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset, 4))
            : BinaryPrimitives.ReadSingleBigEndian(bytes.AsSpan(offset, 4));

    private static double ReadDoubleAt(byte[] bytes, int offset, bool le)
        => le
            ? BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(offset, 8))
            : BinaryPrimitives.ReadDoubleBigEndian(bytes.AsSpan(offset, 8));

    private static double ReadInt16At(byte[] bytes, int offset, bool le)
        => le
            ? BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(offset, 2))
            : BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(offset, 2));

    private static double ReadInt32At(byte[] bytes, int offset, bool le)
        => le
            ? BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4))
            : BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, 4));

    private static double ReadUInt16At(byte[] bytes, int offset, bool le)
        => le
            ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2))
            : BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));

    private static double ReadUInt32At(byte[] bytes, int offset, bool le)
        => le
            ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4))
            : BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
}
