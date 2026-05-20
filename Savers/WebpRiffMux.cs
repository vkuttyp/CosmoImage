using System;
using System.Buffers.Binary;
using System.IO;

namespace CosmoImage.Savers;

/// <summary>
/// Pure-managed RIFF chunk muxer for WebP. Wraps a "naked" VP8L (or VP8)
/// payload in a fresh RIFF container with a VP8X header announcing the
/// metadata flags, then emits the metadata chunks in the canonical order
/// required by the WebP container spec:
///
/// <code>
///   RIFF size "WEBP" VP8X ICCP [ALPH] VP8(L) EXIF XMP
/// </code>
///
/// <para>This is the encoder-side counterpart to the RIFF walker in
/// <see cref="Loaders.VipsWebpLoader"/>. Together they round-trip
/// EXIF / XMP / ICCP through a save+load cycle without any native deps.</para>
///
/// <para>Phase-1 scope: single-frame VP8L base; no animation. The mux is
/// idempotent — calling it again with the same metadata produces the same
/// bytes (modulo a fresh VP8X regen).</para>
/// </summary>
internal static class WebpRiffMux
{
    // VP8X feature flag bits per webp/mux_types.h.
    private const byte ANIMATION_FLAG = 0x02;
    private const byte XMP_FLAG       = 0x04;
    private const byte EXIF_FLAG      = 0x08;
    private const byte ALPHA_FLAG     = 0x10;
    private const byte ICCP_FLAG      = 0x20;

    /// <summary>
    /// Wrap <paramref name="baseWebp"/> (a RIFF/WEBP file with a single
    /// VP8L chunk and no metadata) with VP8X + the supplied metadata
    /// chunks. If all metadata args are null, returns the input as-is.
    /// </summary>
    public static byte[] Wrap(
        byte[] baseWebp,
        byte[]? exif = null,
        byte[]? xmp = null,
        byte[]? icc = null)
    {
        if (baseWebp == null) throw new ArgumentNullException(nameof(baseWebp));

        if (exif == null && xmp == null && icc == null)
            return baseWebp;

        // Locate the base file's image chunk (VP8L or VP8) and canvas dims.
        if (!TryParseBase(baseWebp, out var imageChunk, out var imageChunkOffset,
                           out int canvasWidth, out int canvasHeight, out bool hasAlpha))
        {
            throw new InvalidDataException("WebpRiffMux.Wrap: input is not a valid RIFF/WEBP file with a VP8L or VP8 image chunk");
        }

        // Compute VP8X flags.
        byte flags = 0;
        if (icc  != null) flags |= ICCP_FLAG;
        if (exif != null) flags |= EXIF_FLAG;
        if (xmp  != null) flags |= XMP_FLAG;
        if (hasAlpha)     flags |= ALPHA_FLAG;

        // Compute output size:
        //   "RIFF" + 4-byte size + "WEBP"     = 12
        // + VP8X chunk (8 header + 10 payload, even, no pad) = 18
        // + ICCP chunk (8 + len, padded to even)
        // + image chunk (kept verbatim, already padded)
        // + EXIF chunk (8 + len, padded)
        // + XMP  chunk (8 + len, padded)
        int chunksSize = 18; // VP8X
        chunksSize += ChunkBytes(icc);
        chunksSize += imageChunk.Length;       // already even-aligned in source
        if (PadByteIfOdd(imageChunk.Length) == 1) chunksSize++; // defensive
        chunksSize += ChunkBytes(exif);
        chunksSize += ChunkBytes(xmp);

        int totalSize = 4 /* "WEBP" */ + chunksSize;
        var output = new byte[8 + totalSize];

        int w = 0;
        // RIFF header
        output[w++] = (byte)'R'; output[w++] = (byte)'I'; output[w++] = (byte)'F'; output[w++] = (byte)'F';
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(w, 4), (uint)totalSize); w += 4;
        output[w++] = (byte)'W'; output[w++] = (byte)'E'; output[w++] = (byte)'B'; output[w++] = (byte)'P';

        // VP8X chunk
        output[w++] = (byte)'V'; output[w++] = (byte)'P'; output[w++] = (byte)'8'; output[w++] = (byte)'X';
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(w, 4), 10u); w += 4;
        output[w++] = flags;
        output[w++] = 0; output[w++] = 0; output[w++] = 0;          // 3 reserved bytes
        WriteUInt24LittleEndian(output.AsSpan(w, 3), (uint)(canvasWidth - 1));  w += 3;
        WriteUInt24LittleEndian(output.AsSpan(w, 3), (uint)(canvasHeight - 1)); w += 3;

        // Chunks in canonical order: ICCP, image, EXIF, XMP.
        if (icc  != null) w = WriteChunk(output, w, "ICCP", icc);
        Buffer.BlockCopy(imageChunk, 0, output, w, imageChunk.Length); w += imageChunk.Length;
        if (exif != null) w = WriteChunk(output, w, "EXIF", exif);
        if (xmp  != null) w = WriteChunk(output, w, "XMP ", xmp);

        // Sanity: filled exactly the buffer.
        if (w != output.Length)
        {
            // Truncate or grow defensively to keep output well-formed.
            Array.Resize(ref output, w);
            // Patch RIFF size to reflect actual length.
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4, 4), (uint)(w - 8));
        }
        return output;
    }

    private static int ChunkBytes(byte[]? payload)
    {
        if (payload == null) return 0;
        return 8 + payload.Length + PadByteIfOdd(payload.Length);
    }

    private static int PadByteIfOdd(int len) => (len & 1) == 1 ? 1 : 0;

    private static int WriteChunk(byte[] dst, int offset, string fourcc, byte[] payload)
    {
        if (fourcc.Length != 4) throw new ArgumentException("fourcc must be exactly 4 chars", nameof(fourcc));
        dst[offset++] = (byte)fourcc[0];
        dst[offset++] = (byte)fourcc[1];
        dst[offset++] = (byte)fourcc[2];
        dst[offset++] = (byte)fourcc[3];
        BinaryPrimitives.WriteUInt32LittleEndian(dst.AsSpan(offset, 4), (uint)payload.Length);
        offset += 4;
        Buffer.BlockCopy(payload, 0, dst, offset, payload.Length);
        offset += payload.Length;
        if ((payload.Length & 1) == 1)
            dst[offset++] = 0;
        return offset;
    }

    private static void WriteUInt24LittleEndian(Span<byte> dst, uint value)
    {
        dst[0] = (byte)(value & 0xFF);
        dst[1] = (byte)((value >> 8) & 0xFF);
        dst[2] = (byte)((value >> 16) & 0xFF);
    }

    /// <summary>
    /// Locate the VP8L (or VP8) chunk in the base WebP and read its
    /// canvas dimensions + alpha flag. Returns the full chunk including
    /// its 8-byte fourcc+size header so the caller can splice it into a
    /// new container verbatim.
    /// </summary>
    private static bool TryParseBase(
        byte[] bytes,
        out byte[] imageChunk,
        out int imageChunkOffset,
        out int width,
        out int height,
        out bool hasAlpha)
    {
        imageChunk = Array.Empty<byte>();
        imageChunkOffset = -1;
        width = 0; height = 0; hasAlpha = false;

        if (bytes.Length < 12 + 8) return false;
        if (bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F') return false;
        if (bytes[8] != 'W' || bytes[9] != 'E' || bytes[10] != 'B' || bytes[11] != 'P') return false;

        int p = 12;
        while (p + 8 <= bytes.Length)
        {
            uint fourcc = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p, 4));
            int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p + 4, 4));
            int payload = p + 8;
            if (len < 0 || payload + len > bytes.Length) return false;
            int padded = len + (len & 1);
            int chunkSize = 8 + padded;

            if (fourcc == 0x4C385056) // "VP8L"
            {
                if (len < 5 || bytes[payload] != 0x2F) return false;
                uint val = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(payload + 1, 4));
                width    = (int)((val & 0x3FFF) + 1);
                height   = (int)(((val >> 14) & 0x3FFF) + 1);
                hasAlpha = ((val >> 28) & 1) != 0;
                imageChunkOffset = p;
                imageChunk = new byte[chunkSize];
                Buffer.BlockCopy(bytes, p, imageChunk, 0, chunkSize);
                return true;
            }
            if (fourcc == 0x20385056) // "VP8 " (lossy)
            {
                if (len < 10) return false;
                var d = bytes.AsSpan(payload, 10);
                if ((d[0] & 1) != 0 || d[3] != 0x9D || d[4] != 0x01 || d[5] != 0x2A) return false;
                width  = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(6, 2)) & 0x3FFF;
                height = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(8, 2)) & 0x3FFF;
                hasAlpha = false;
                imageChunkOffset = p;
                imageChunk = new byte[chunkSize];
                Buffer.BlockCopy(bytes, p, imageChunk, 0, chunkSize);
                return true;
            }

            p = payload + padded;
        }
        return false;
    }
}
