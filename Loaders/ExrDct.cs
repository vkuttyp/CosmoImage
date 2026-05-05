using System;
using System.IO;
using System.IO.Compression;

namespace CosmoImage.Loaders;

/// <summary>
/// 8×8 forward and inverse DCT-II (separable, orthonormal) plus the
/// standard JPEG zigzag scan order. Foundational primitives for
/// OpenEXR DWA's LOSSY_DCT channel path; matches libimf's reference
/// floating-point DCT in <c>internal_dwa.c</c>.
///
/// <para>The scaling factor is the orthonormal one: per 1-D pass,
/// each output is a sum-of-cosines weighted by <c>c(n)</c> where
/// <c>c(0) = 1/√2</c> and <c>c(n) = 1</c> for <c>n &gt; 0</c>, with a
/// <c>×½</c> normalization. Two passes (rows then columns for
/// forward, columns then rows for inverse) make it self-inverse.</para>
/// </summary>
internal static class ExrDct
{
    /// <summary>
    /// Forward 8×8 DCT-II in place. Operates on a row-major 64-element
    /// float array; produces frequency-domain coefficients in the
    /// same layout (DC at [0], horizontal frequencies along row 0,
    /// vertical along column 0).
    /// </summary>
    internal static void Forward8x8InPlace(float[] block)
    {
        Span<float> tmp = stackalloc float[64];
        // Rows: pre-pass that lands frequency-domain row coefficients.
        for (int y = 0; y < 8; y++)
        {
            for (int u = 0; u < 8; u++)
            {
                float sum = 0f;
                for (int x = 0; x < 8; x++)
                    sum += block[y * 8 + x] * (float)Math.Cos((2 * x + 1) * u * Math.PI / 16.0);
                float c = (u == 0) ? 0.7071067811865475f : 1f;
                tmp[y * 8 + u] = c * sum * 0.5f;
            }
        }
        // Columns: complete the 2-D DCT.
        for (int u = 0; u < 8; u++)
        {
            for (int v = 0; v < 8; v++)
            {
                float sum = 0f;
                for (int y = 0; y < 8; y++)
                    sum += tmp[y * 8 + u] * (float)Math.Cos((2 * y + 1) * v * Math.PI / 16.0);
                float c = (v == 0) ? 0.7071067811865475f : 1f;
                block[v * 8 + u] = c * sum * 0.5f;
            }
        }
    }

    /// <summary>
    /// Inverse 8×8 DCT-II in place. Reverses
    /// <see cref="Forward8x8InPlace"/>: takes frequency-domain
    /// coefficients and reconstructs spatial pixels.
    /// </summary>
    internal static void Inverse8x8InPlace(float[] block)
    {
        Span<float> tmp = stackalloc float[64];
        // Inverse columns.
        for (int u = 0; u < 8; u++)
        {
            for (int y = 0; y < 8; y++)
            {
                float sum = 0f;
                for (int v = 0; v < 8; v++)
                {
                    float c = (v == 0) ? 0.7071067811865475f : 1f;
                    sum += c * block[v * 8 + u] * (float)Math.Cos((2 * y + 1) * v * Math.PI / 16.0);
                }
                tmp[y * 8 + u] = sum * 0.5f;
            }
        }
        // Inverse rows.
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                float sum = 0f;
                for (int u = 0; u < 8; u++)
                {
                    float c = (u == 0) ? 0.7071067811865475f : 1f;
                    sum += c * tmp[y * 8 + u] * (float)Math.Cos((2 * x + 1) * u * Math.PI / 16.0);
                }
                block[y * 8 + x] = sum * 0.5f;
            }
        }
    }

    /// <summary>
    /// Expand a DWA AC token stream into <paramref name="blockCount"/>
    /// 8×8 frequency blocks (63 AC coefficients per block, DC at
    /// position 0 left untouched). Token convention from
    /// <c>internal_dwa_decoder.h</c>:
    /// <list type="bullet">
    ///   <item><c>0xFFnn</c> with <c>nn != 0</c>: zero-run of
    ///         <c>nn</c> positions in the zigzag stream</item>
    ///   <item><c>0xFF00</c>: end-of-block (advance to next block;
    ///         remaining AC slots stay zero — caller must pre-zero
    ///         the AC positions of <paramref name="blocks"/>).
    ///         Emitted only by VERSION≥1 encoders, harmless when
    ///         absent (VERSION 0 streams never produce it)</item>
    ///   <item>Anything else: literal HALF coefficient</item>
    /// </list>
    /// Block boundary is implicit when 63 AC positions are filled.
    /// </summary>
    /// <returns>True if the token stream filled or terminated all
    /// blocks; false on overrun or partial fill.</returns>
    internal static bool ExpandAcTokens(ushort[] acTokens, int blockCount, ushort[] blocks)
    {
        if (blocks.Length < (long)blockCount * 64) return false;

        int tokIdx = 0;
        int blockIdx = 0;
        int posInBlock = 1;  // skip DC at [0]

        while (blockIdx < blockCount && tokIdx < acTokens.Length)
        {
            ushort tok = acTokens[tokIdx++];
            if ((tok & 0xFF00) == 0xFF00)
            {
                int count = tok & 0xFF;
                if (count == 0)
                {
                    // EOB — advance to next block. Remaining AC
                    // positions of this block stay at their
                    // pre-zeroed default.
                    blockIdx++;
                    posInBlock = 1;
                    continue;
                }
                while (count > 0)
                {
                    if (blockIdx >= blockCount) return false;
                    blocks[blockIdx * 64 + posInBlock] = 0;
                    posInBlock++;
                    count--;
                    if (posInBlock == 64)
                    {
                        blockIdx++;
                        posInBlock = 1;
                    }
                }
            }
            else
            {
                if (blockIdx >= blockCount) return false;
                blocks[blockIdx * 64 + posInBlock] = tok;
                posInBlock++;
                if (posInBlock == 64)
                {
                    blockIdx++;
                    posInBlock = 1;
                }
            }
        }

        // Success: all blocks completed. blockIdx == blockCount can
        // happen either because they all naturally filled (posInBlock
        // wrapped to 1 on the last block) or because EOB was the
        // final token of the last block (also leaves posInBlock at 1).
        return blockIdx == blockCount && posInBlock == 1;
    }

    /// <summary>
    /// Inflate the DC sub-stream and surface it as
    /// <paramref name="count"/> little-endian uint16 values — one
    /// DC coefficient per 8×8 block in scanline-block order. Whether
    /// the encoder applied a per-block delta predictor on top is a
    /// matter for the caller (the integration round figures it out).
    /// Returns null when the zlib decode fails or the byte count is
    /// inconsistent with <paramref name="count"/>.
    /// </summary>
    internal static ushort[]? DecodeDcStream(byte[] src, int srcOff, int srcLen, int count)
    {
        if (count < 0) return null;
        var bytes = new byte[count * 2];
        try
        {
            using var ms = new MemoryStream(src, srcOff, srcLen);
            using var z = new ZLibStream(ms, CompressionMode.Decompress);
            int read = 0;
            while (read < bytes.Length)
            {
                int n = z.Read(bytes, read, bytes.Length - read);
                if (n == 0) return null;
                read += n;
            }
        }
        catch { return null; }

        var result = new ushort[count];
        for (int i = 0; i < count; i++)
            result[i] = (ushort)(bytes[i * 2] | (bytes[i * 2 + 1] << 8));
        return result;
    }

    /// <summary>
    /// Write one 8×8 post-IDCT spatial block into a planar HALF
    /// target at <paramref name="blockX"/>, <paramref name="blockY"/>.
    /// Edge blocks (where blockX + 8 &gt; planeWidth or
    /// blockY + 8 &gt; planeHeight) write only the valid pixels.
    /// When <paramref name="applyToLinear"/> is true the value is
    /// squared in HALF arithmetic — undoes the perceptual sqrt the
    /// DWA encoder applies to non-pLinear channels.
    /// </summary>
    internal static void PlaceBlock(
        float[] block,
        byte[] dst,
        int planeWidth,
        int planeHeight,
        int blockX,
        int blockY,
        bool applyToLinear)
    {
        int maxY = Math.Min(8, planeHeight - blockY);
        int maxX = Math.Min(8, planeWidth - blockX);
        if (maxX <= 0 || maxY <= 0) return;

        for (int y = 0; y < maxY; y++)
        {
            int dstY = blockY + y;
            int rowBase = dstY * planeWidth * 2;
            for (int x = 0; x < maxX; x++)
            {
                int dstX = blockX + x;
                Half h = (Half)block[y * 8 + x];
                if (applyToLinear) h = h * h;
                ushort bits = BitConverter.HalfToUInt16Bits(h);
                int o = rowBase + dstX * 2;
                dst[o]     = (byte)bits;
                dst[o + 1] = (byte)(bits >> 8);
            }
        }
    }

    /// <summary>
    /// Standard JPEG zigzag scan order. <c>ZigzagToRowMajor[k]</c> is
    /// the row-major flat index <c>(row * 8 + col)</c> of the k-th
    /// position in the zigzag scan. Used by encoders to serialize
    /// 8×8 frequency blocks into 1-D coefficient streams that
    /// concentrate energy at the front.
    /// </summary>
    internal static readonly int[] ZigzagToRowMajor = new[]
    {
         0,  1,  8, 16,  9,  2,  3, 10,
        17, 24, 32, 25, 18, 11,  4,  5,
        12, 19, 26, 33, 40, 48, 41, 34,
        27, 20, 13,  6,  7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36,
        29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63,
    };

    /// <summary>Inverse of <see cref="ZigzagToRowMajor"/>: row-major flat index → zigzag position.</summary>
    internal static readonly int[] ZigzagFromRowMajor = BuildInverseZigzag();

    private static int[] BuildInverseZigzag()
    {
        var inv = new int[64];
        for (int k = 0; k < 64; k++) inv[ZigzagToRowMajor[k]] = k;
        return inv;
    }
}
