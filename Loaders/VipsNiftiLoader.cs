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

        // Magic must be n+1 (single-file). ni1 paired form not handled here.
        if (!(bytes[344] == 'n' && bytes[345] == '+' && bytes[346] == '1' && bytes[347] == 0))
            return null;

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
        if (dataOffset < HeaderSize || dataOffset > bytes.Length) return null;

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
            case 2:  // uint8
                outFloat = needsTransform; // promotes to Float when scaling applied
                break;
            case 16: // float32
            case 64: // float64
                outFloat = true;
                break;
            default:
                // signed-int / int16 / int32 etc. unsupported in first cut
                return null;
        }
        int expectedBytesPerSampleByDt = datatype switch
        {
            2 => 1,
            16 => 4,
            64 => 8,
            _ => 0,
        };
        if (bytesPerSample != expectedBytesPerSampleByDt) return null;

        long sampleCount = (long)nx * ny * planes;
        long dataLen = sampleCount * bytesPerSample;
        if (dataOffset + dataLen > bytes.Length) return null;

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
                        2 => bytes[srcOff],
                        16 => ReadFloatAt(bytes, (int)srcOff, littleEndian),
                        64 => ReadDoubleAt(bytes, (int)srcOff, littleEndian),
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
}
