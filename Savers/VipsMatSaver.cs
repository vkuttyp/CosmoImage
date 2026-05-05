using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using CosmoImage.Core;

namespace CosmoImage.Savers;

/// <summary>
/// Matlab v5 MAT-file writer — mirror of <see cref="Loaders.VipsMatLoader"/>.
/// Pure-C# emitter for the v5 tagged-binary format; same constraints
/// as the reader (no native deps, no v7.3 / HDF5 path).
///
/// <para>Wire layout produced:</para>
/// <list type="number">
///   <item>128-byte ASCII descriptor — text header + version <c>0x0100</c>
///         + endian marker <c>"IM"</c> (little-endian).</item>
///   <item>One top-level <c>miMATRIX</c> element wrapping the image:</item>
///   <item>… ArrayFlags (miUINT32, 8 bytes — class + flags).</item>
///   <item>… Dimensions (miINT32, ndim × 4 bytes — H, W, [planes]).</item>
///   <item>… Array Name (miINT8, variable — defaults to <c>"X"</c>).</item>
///   <item>… Real Part (data type per band format — UChar→miUInt8,
///         Float→miSingle, etc.). Column-major per matlab convention.</item>
/// </list>
///
/// <para>UChar 1-band emits 2D <c>mxUInt8Class</c>; UChar 3/4-band emits
/// 3D <c>mxUInt8Class</c> with planes = bands. Float emits the
/// corresponding <c>mxSingleClass</c>. Other formats cast to Float
/// before writing.</para>
///
/// <para>Image orientation is transposed row-major → column-major during
/// write; <see cref="Loaders.VipsMatLoader"/> reverses the same
/// transpose, so a round-trip is pixel-exact.</para>
/// </summary>
public static class VipsMatSaver
{
    private const int HeaderSize = 128;

    private const uint MiInt8 = 1;
    private const uint MiUInt8 = 2;
    private const uint MiInt32 = 5;
    private const uint MiUInt32 = 6;
    private const uint MiSingle = 7;
    private const uint MiMatrix = 14;

    private const byte MxSingleClass = 7;
    private const byte MxUInt8Class = 9;

    public static async Task SaveAsync(VipsImage image, PipeWriter writer,
        string variableName = "X", CancellationToken cancellationToken = default)
    {
        if (image == null) throw new ArgumentNullException(nameof(image));

        // UChar passes through as miUInt8; everything else widens to Float
        // (miSingle) so we don't need a per-format type matrix.
        var src = image.BandFormat == VipsBandFormat.UChar
            ? image
            : (image.BandFormat == VipsBandFormat.Float
                ? image
                : VipsImageOps.CastFloat(image));
        bool isFloat = src.BandFormat == VipsBandFormat.Float;

        byte[] pixels;
        if (src.Pixels is { } existing) pixels = existing;
        else
        {
            var sink = new MemorySink(src);
            await sink.RunAsync(cancellationToken);
            pixels = sink.Pixels;
        }

        int W = src.Width, H = src.Height, bands = src.Bands;
        var stream = writer.AsStream();

        // ---- 128-byte descriptor ----
        var header = new byte[HeaderSize];
        // Bytes 0..123: ASCII description text, padded with spaces.
        var desc = System.Text.Encoding.ASCII.GetBytes(
            $"MATLAB 5.0 MAT-file, Platform: CosmoImage, Created: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        if (desc.Length > 124) desc = desc.AsSpan(0, 124).ToArray();
        Buffer.BlockCopy(desc, 0, header, 0, desc.Length);
        for (int i = desc.Length; i < 124; i++) header[i] = (byte)' ';
        // Bytes 124..125: version 0x0100 (little-endian).
        header[124] = 0x00; header[125] = 0x01;
        // Bytes 126..127: endian marker "IM" (little-endian).
        header[126] = (byte)'I'; header[127] = (byte)'M';
        await stream.WriteAsync(header, cancellationToken);

        // ---- Build miMATRIX body to a buffer, then emit with its size ----
        using var body = new MemoryStream();
        WriteArrayFlags(body, isFloat ? MxSingleClass : MxUInt8Class);

        int planes = bands;
        if (planes == 1) WriteDimensions(body, new[] { H, W });
        else WriteDimensions(body, new[] { H, W, planes });

        WriteArrayName(body, variableName);
        WriteRealPart(body, pixels, W, H, bands, isFloat);

        var bodyBytes = body.ToArray();
        WriteTag(stream, MiMatrix, (uint)bodyBytes.Length);
        await stream.WriteAsync(bodyBytes, cancellationToken);

        await writer.FlushAsync(cancellationToken);
        await writer.CompleteAsync();
    }

    /// <summary>Emit an 8-byte tag (type + byte-count) in long form.</summary>
    private static void WriteTag(Stream s, uint type, uint byteCount)
    {
        Span<byte> tag = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(tag.Slice(0, 4), type);
        BinaryPrimitives.WriteUInt32LittleEndian(tag.Slice(4, 4), byteCount);
        s.Write(tag);
    }

    /// <summary>
    /// Pad the stream up to the next 8-byte boundary. The MAT spec aligns
    /// every element so the next tag starts on a multiple of 8.
    /// </summary>
    private static void PadTo8(Stream s)
    {
        long pos = s.Position;
        int pad = (int)((8 - (pos & 7)) & 7);
        for (int i = 0; i < pad; i++) s.WriteByte(0);
    }

    private static void WriteArrayFlags(Stream s, byte cls)
    {
        WriteTag(s, MiUInt32, 8);
        Span<byte> data = stackalloc byte[8];
        // First word: low byte = class, upper bits = flags (0 = real, no logical).
        BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(0, 4), (uint)cls);
        // Second word: nzmax = 0 for non-sparse.
        BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(4, 4), 0);
        s.Write(data);
    }

    private static void WriteDimensions(Stream s, int[] dims)
    {
        uint byteCount = (uint)(dims.Length * 4);
        WriteTag(s, MiInt32, byteCount);
        Span<byte> buf = stackalloc byte[4];
        for (int i = 0; i < dims.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buf, dims[i]);
            s.Write(buf);
        }
        PadTo8(s);
    }

    private static void WriteArrayName(Stream s, string name)
    {
        var nameBytes = System.Text.Encoding.ASCII.GetBytes(name);
        WriteTag(s, MiInt8, (uint)nameBytes.Length);
        s.Write(nameBytes);
        PadTo8(s);
    }

    /// <summary>
    /// Real-part data block. Transposes row-major VipsImage pixels into
    /// matlab's column-major convention. For 3D arrays, planes interleave
    /// in the source band axis but separate-plane in matlab order.
    /// </summary>
    private static void WriteRealPart(Stream s, byte[] pixels, int W, int H, int bands, bool isFloat)
    {
        int sampleSize = isFloat ? 4 : 1;
        long totalSamples = (long)W * H * bands;
        uint dataBytes = (uint)(totalSamples * sampleSize);
        WriteTag(s, isFloat ? MiSingle : MiUInt8, dataBytes);

        // For each plane k, walk column-by-column then row-by-row, picking
        // the (r, c, k) sample from row-major interleaved pixels.
        Span<byte> sbuf = stackalloc byte[4];
        for (int k = 0; k < bands; k++)
        {
            for (int c = 0; c < W; c++)
            {
                for (int r = 0; r < H; r++)
                {
                    int srcOff = (r * W + c) * bands * sampleSize + k * sampleSize;
                    if (isFloat)
                    {
                        sbuf[0] = pixels[srcOff + 0];
                        sbuf[1] = pixels[srcOff + 1];
                        sbuf[2] = pixels[srcOff + 2];
                        sbuf[3] = pixels[srcOff + 3];
                        s.Write(sbuf);
                    }
                    else
                    {
                        s.WriteByte(pixels[srcOff]);
                    }
                }
            }
        }
        PadTo8(s);
    }
}
