using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using CosmoImage.Core;

namespace CosmoImage.Savers;

/// <summary>
/// NIfTI-1 writer. Emits the single-file <c>.nii</c> form (magic
/// <c>"n+1\0"</c>): 348-byte header + 4 zero pad + raw pixel data
/// starting at offset 352. Native little-endian byte order — every
/// modern reader detects endianness via the <c>dim[0]</c> rule, so
/// matching the host order is fine.
///
/// <para>BITPIX dispatch: UChar → datatype 2 (uint8); Float → datatype 16
/// (float32). 1-band image saves as NIfTI 2D (NDIM = 2); multi-band
/// (3 or 4 bands) saves as 3D with NDIM = 3 and dim[3] = bands, mirroring
/// how the loader treats the third axis. <c>scl_slope</c> = 1 and
/// <c>scl_inter</c> = 0 (no value transform on output).</para>
///
/// Any <c>Metadata["nifti:descrip"]</c> entry rides through into the
/// header's 80-byte description field.
/// </summary>
public static class VipsNiftiSaver
{
    private const int HeaderSize = 348;
    private const int VoxOffset = 352; // 348 header + 4 pad

    /// <summary>
    /// Save the image as a single-file <c>.nii</c>. Header magic is
    /// <c>"n+1\0"</c>; pixel data immediately follows the 4-byte pad.
    /// </summary>
    public static async Task SaveAsync(VipsImage image, PipeWriter writer, CancellationToken cancellationToken = default)
    {
        if (image == null) throw new ArgumentNullException(nameof(image));
        if (image.Bands < 1 || image.Bands > 4)
            throw new NotSupportedException($"NIfTI save needs 1..4 bands; got {image.Bands}");

        // Decide output datatype. UChar passes through; everything else
        // round-trips through Float for simplicity.
        short datatype = image.BandFormat == VipsBandFormat.UChar ? (short)2 : (short)16;
        short bitpix = (short)(datatype == 2 ? 8 : 32);
        bool outFloat = datatype == 16;
        VipsImage src = image.BandFormat == VipsBandFormat.UChar || image.BandFormat == VipsBandFormat.Float
            ? image
            : VipsImageOps.CastFloat(image);

        byte[] pixels;
        if (src.Pixels is { } existing)
        {
            pixels = existing;
        }
        else
        {
            var sink = new MemorySink(src);
            await sink.RunAsync(cancellationToken);
            pixels = sink.Pixels;
        }

        int W = src.Width;
        int H = src.Height;
        int planes = src.Bands;
        int inBytesPerPel = src.SizeOfPel;
        int bytesPerSample = bitpix / 8;
        int ndims = planes == 1 ? 2 : 3;

        var stream = writer.AsStream();
        var header = new byte[VoxOffset];

        // sizeof_hdr
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0, 4), HeaderSize);
        // dim[0] (ndims) at offset 40, dim[1..3] at 42, 44, 46.
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(40, 2), (short)ndims);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(42, 2), (short)W);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(44, 2), (short)H);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(46, 2), (short)planes);
        // dim[4..7] = 1 (NIfTI convention: unused dims set to 1)
        for (int i = 4; i <= 7; i++)
            BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(40 + i * 2, 2), 1);

        // datatype + bitpix
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(70, 2), datatype);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(72, 2), bitpix);

        // pixdim[0] = qfac (sign of qform determinant; default 1)
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(76, 4), 1f);
        // pixdim[1] / [2] from XRes/YRes (libvips px/mm → mm/px = 1/XRes).
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(80, 4),
            src.XRes > 0 ? (float)(1.0 / src.XRes) : 1f);
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(84, 4),
            src.YRes > 0 ? (float)(1.0 / src.YRes) : 1f);
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(88, 4), 1f); // pixdim[3]

        // vox_offset
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(108, 4), VoxOffset);
        // scl_slope = 1, scl_inter = 0 (no value transform)
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(112, 4), 1f);
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(116, 4), 0f);

        // descrip[80] at offset 148.
        if (src.Metadata.TryGetValue("nifti:descrip", out var descrip) && !string.IsNullOrEmpty(descrip))
        {
            var b = System.Text.Encoding.ASCII.GetBytes(descrip);
            int len = Math.Min(b.Length, 80);
            Buffer.BlockCopy(b, 0, header, 148, len);
        }

        // xyzt_units at offset 123: low 3 bits = spatial unit (2 = mm).
        header[123] = 2;

        // qform_code / sform_code (offsets 252, 254). 0 = unknown — viewers
        // fall back to pixdim. Avoids us having to compute a quaternion.
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(252, 2), 0);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(254, 2), 0);

        // Magic at offset 344: "n+1\0" for single-file.
        header[344] = (byte)'n'; header[345] = (byte)'+'; header[346] = (byte)'1'; header[347] = 0;
        // bytes 348..351 are 4 zero pad bytes (already zero from Array.Clear-by-default).

        await stream.WriteAsync(header, cancellationToken);

        // Pixel data: planar (NIfTI's natural layout). Walk plane-major, each
        // plane written as a contiguous W×H block in row-major order. The
        // VipsImage source is interleaved by band so we transpose here.
        var sampleBuf = new byte[bytesPerSample];
        for (int p = 0; p < planes; p++)
        {
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int srcOff = (y * W + x) * inBytesPerPel + p * bytesPerSample;
                    if (outFloat)
                    {
                        // In-memory floats are little-endian; no swap needed
                        // since header advertises native byte order.
                        Buffer.BlockCopy(pixels, srcOff, sampleBuf, 0, 4);
                    }
                    else
                    {
                        sampleBuf[0] = pixels[srcOff];
                    }
                    await stream.WriteAsync(sampleBuf, cancellationToken);
                }
            }
        }

        await writer.FlushAsync(cancellationToken);
        await writer.CompleteAsync();
    }

    /// <summary>
    /// Save the image as a paired-form NIfTI (<c>.hdr</c> + <c>.img</c>).
    /// Header magic is <c>"ni1\0"</c>; pixel data lives entirely in the
    /// second writer at offset 0. Use this when emitting NIfTI for tools
    /// that expect the legacy two-file convention. The two writers can
    /// be backed by anything <see cref="PipeWriter"/> wraps — files,
    /// memory, custom callbacks via <see cref="Core.IVipsTarget"/>.
    /// </summary>
    public static async Task SavePairedAsync(VipsImage image,
        PipeWriter headerWriter, PipeWriter dataWriter,
        CancellationToken cancellationToken = default)
    {
        if (image == null) throw new ArgumentNullException(nameof(image));
        if (image.Bands < 1 || image.Bands > 4)
            throw new NotSupportedException($"NIfTI save needs 1..4 bands; got {image.Bands}");

        short datatype = image.BandFormat == VipsBandFormat.UChar ? (short)2 : (short)16;
        short bitpix = (short)(datatype == 2 ? 8 : 32);
        bool outFloat = datatype == 16;
        VipsImage src = image.BandFormat == VipsBandFormat.UChar || image.BandFormat == VipsBandFormat.Float
            ? image
            : VipsImageOps.CastFloat(image);

        byte[] pixels;
        if (src.Pixels is { } existing) pixels = existing;
        else
        {
            var sink = new MemorySink(src);
            await sink.RunAsync(cancellationToken);
            pixels = sink.Pixels;
        }

        int W = src.Width;
        int H = src.Height;
        int planes = src.Bands;
        int inBytesPerPel = src.SizeOfPel;
        int bytesPerSample = bitpix / 8;
        int ndims = planes == 1 ? 2 : 3;

        // Paired-form header: 348 bytes exactly, no 4-byte pad. Magic
        // "ni1\0", vox_offset = 0 (data starts at byte 0 of the .img).
        var header = new byte[HeaderSize];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0, 4), HeaderSize);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(40, 2), (short)ndims);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(42, 2), (short)W);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(44, 2), (short)H);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(46, 2), (short)planes);
        for (int i = 4; i <= 7; i++)
            BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(40 + i * 2, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(70, 2), datatype);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(72, 2), bitpix);
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(76, 4), 1f);
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(80, 4),
            src.XRes > 0 ? (float)(1.0 / src.XRes) : 1f);
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(84, 4),
            src.YRes > 0 ? (float)(1.0 / src.YRes) : 1f);
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(88, 4), 1f);
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(108, 4), 0f); // vox_offset = 0
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(112, 4), 1f);
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(116, 4), 0f);
        if (src.Metadata.TryGetValue("nifti:descrip", out var descrip) && !string.IsNullOrEmpty(descrip))
        {
            var b = System.Text.Encoding.ASCII.GetBytes(descrip);
            int len = Math.Min(b.Length, 80);
            Buffer.BlockCopy(b, 0, header, 148, len);
        }
        header[123] = 2;
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(252, 2), 0);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(254, 2), 0);
        // Magic "ni1\0" — paired-form indicator.
        header[344] = (byte)'n'; header[345] = (byte)'i'; header[346] = (byte)'1'; header[347] = 0;

        await headerWriter.AsStream().WriteAsync(header, cancellationToken);
        await headerWriter.FlushAsync(cancellationToken);
        await headerWriter.CompleteAsync();

        // Pixel data goes to the second writer in NIfTI's planar layout.
        var dataStream = dataWriter.AsStream();
        var sampleBuf = new byte[bytesPerSample];
        for (int p = 0; p < planes; p++)
        {
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int srcOff = (y * W + x) * inBytesPerPel + p * bytesPerSample;
                    if (outFloat)
                        Buffer.BlockCopy(pixels, srcOff, sampleBuf, 0, 4);
                    else
                        sampleBuf[0] = pixels[srcOff];
                    await dataStream.WriteAsync(sampleBuf, cancellationToken);
                }
            }
        }

        await dataWriter.FlushAsync(cancellationToken);
        await dataWriter.CompleteAsync();
    }
}
