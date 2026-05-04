using System;
using System.Collections.Generic;

namespace CosmoImage.Loaders;

/// <summary>
/// OpenEXR B44 / B44A compression. Block-based 4×4-pixel DPCM
/// compressor for HALF channels: 14 bytes per 4×4 block (or 3 bytes
/// for uniform blocks under B44A). FLOAT / UINT channels pass through
/// uncompressed within the block.
///
/// <para>Implementation follows OpenEXR's
/// <c>internal_b44.c</c> / <c>ImfB44Compressor.cpp</c>:</para>
/// <list type="bullet">
///   <item>Sign-magnitude → monotonic mapping (s ↔ t)</item>
///   <item>14-byte layout: 2-byte big-endian DC, then 6-bit shift +
///         15×6-bit difference codes packed across bytes 2..13</item>
///   <item>3-byte uniform block (B44A): DC + 0xFC sentinel</item>
///   <item>Optional perceptual LUT: square (decode) / sqrt (encode)
///         applied to channels with pLinear=0; channels with
///         pLinear=1 skip the pass</item>
/// </list>
/// </summary>
internal static class ExrB44
{
    private const int Bias = 0x20;

    /// <summary>
    /// Decompress a B44 (or B44A) block into the per-row-per-channel-per-pixel
    /// layout the EXR demux expects. <paramref name="b44a"/> only
    /// affects encoding; the decoder auto-detects the 3-byte short
    /// form via the byte-2 sentinel and so handles both modes
    /// transparently.
    /// </summary>
    public static bool Decompress(byte[] src, int srcOff, int srcLen,
        byte[] dst, int rows, IReadOnlyList<ExrB44ChannelInfo> channels, int width)
    {
        int p = srcOff;
        int end = srcOff + srcLen;
        int rowStride = 0;
        foreach (var ch in channels) rowStride += width * ch.BytesPerSample;

        int channelOff = 0;
        foreach (var ch in channels)
        {
            int bw = ch.BytesPerSample;
            if (bw == 2)
            {
                if (!DecompressHalfPlane(src, ref p, end, dst, channelOff, rowStride,
                    rows, width, bw, ch.PLinear))
                    return false;
            }
            else
            {
                // FLOAT (4) and UINT (4): raw rows in scanline order.
                int rowBytes = width * bw;
                for (int row = 0; row < rows; row++)
                {
                    if (p + rowBytes > end) return false;
                    Buffer.BlockCopy(src, p, dst, row * rowStride + channelOff, rowBytes);
                    p += rowBytes;
                }
            }
            channelOff += width * bw;
        }
        return true;
    }

    private static bool DecompressHalfPlane(byte[] src, ref int p, int end,
        byte[] dst, int channelOff, int rowStride,
        int rows, int width, int bytesPerSample, bool pLinear)
    {
        int blockRows = (rows + 3) / 4;
        int blockCols = (width + 3) / 4;
        Span<ushort> t = stackalloc ushort[16];
        Span<ushort> sBuf = stackalloc ushort[16];

        for (int by = 0; by < blockRows; by++)
        {
            int y0 = by * 4;
            int yMax = Math.Min(4, rows - y0);
            for (int bx = 0; bx < blockCols; bx++)
            {
                int x0 = bx * 4;
                int xMax = Math.Min(4, width - x0);

                if (p + 3 > end) return false;
                // Short-form sentinel: byte 2's top 6 bits hold the
                // shift in [0, 62]; canonical encoder writes 0xFC for
                // a uniform block, but the decoder accepts any value
                // ≥ 0xD0 = 13<<2 (shift 52..62 isn't legal for B44).
                bool isShort = src[p + 2] >= 0xD0;
                int blockBytes = isShort ? 3 : 14;
                if (p + blockBytes > end) return false;

                if (isShort)
                {
                    ushort dc = (ushort)((src[p] << 8) | src[p + 1]);
                    for (int i = 0; i < 16; i++) t[i] = dc;
                }
                else
                {
                    UnpackFullBlock(src, p, t);
                }
                p += blockBytes;

                // t → s (sign-magnitude reverse).
                for (int i = 0; i < 16; i++) sBuf[i] = TToS(t[i]);
                if (!pLinear) ApplyConvertToLinear(sBuf);

                // Blit the 4×4 block into the per-row dst layout.
                for (int yy = 0; yy < yMax; yy++)
                {
                    for (int xx = 0; xx < xMax; xx++)
                    {
                        int srcIdx = yy * 4 + xx;
                        int dstByte = (y0 + yy) * rowStride + channelOff + (x0 + xx) * bytesPerSample;
                        dst[dstByte]     = (byte)sBuf[srcIdx];
                        dst[dstByte + 1] = (byte)(sBuf[srcIdx] >> 8);
                    }
                }
            }
        }
        return true;
    }

    /// <summary>
    /// Unpack a 14-byte full B44 block into 16 t-values (monotonic
    /// space). Bytes 0..1 are the big-endian DC; byte 2 carries shift
    /// in its top 6 bits and r[0]'s top 2 bits in its bottom 2; bytes
    /// 3..13 hold r[0..14] packed as 15×6-bit values.
    /// </summary>
    private static void UnpackFullBlock(byte[] src, int p, Span<ushort> t)
    {
        ushort dc = (ushort)((src[p] << 8) | src[p + 1]);
        int shift = src[p + 2] >> 2;

        Span<int> r = stackalloc int[15];
        r[0]  = ((src[p + 2] & 0x03) << 4) | (src[p + 3] >> 4);
        r[1]  = ((src[p + 3] & 0x0F) << 2) | (src[p + 4] >> 6);
        r[2]  = src[p + 4] & 0x3F;
        r[3]  = src[p + 5] >> 2;
        r[4]  = ((src[p + 5] & 0x03) << 4) | (src[p + 6] >> 4);
        r[5]  = ((src[p + 6] & 0x0F) << 2) | (src[p + 7] >> 6);
        r[6]  = src[p + 7] & 0x3F;
        r[7]  = src[p + 8] >> 2;
        r[8]  = ((src[p + 8] & 0x03) << 4) | (src[p + 9] >> 4);
        r[9]  = ((src[p + 9] & 0x0F) << 2) | (src[p + 10] >> 6);
        r[10] = src[p + 10] & 0x3F;
        r[11] = src[p + 11] >> 2;
        r[12] = ((src[p + 11] & 0x03) << 4) | (src[p + 12] >> 4);
        r[13] = ((src[p + 12] & 0x0F) << 2) | (src[p + 13] >> 6);
        r[14] = src[p + 13] & 0x3F;

        // DPCM reconstruction. Encoder chain:
        //   r[0]=t[0]-t[4]   r[1]=t[4]-t[8]   r[2]=t[8]-t[12]   (col 0)
        //   r[3]=t[0]-t[1]   r[4]=t[4]-t[5]   r[5]=t[8]-t[9]    r[6]=t[12]-t[13]
        //   r[7]=t[1]-t[2]   r[8]=t[5]-t[6]   r[9]=t[9]-t[10]   r[10]=t[13]-t[14]
        //   r[11]=t[2]-t[3]  r[12]=t[6]-t[7]  r[13]=t[10]-t[11] r[14]=t[14]-t[15]
        // Reverse: t[next] = t[prev] - ((r - bias) << shift), unsigned wrap.
        t[0] = dc;
        t[4]  = (ushort)(t[0]  - ((r[0]  - Bias) << shift));
        t[8]  = (ushort)(t[4]  - ((r[1]  - Bias) << shift));
        t[12] = (ushort)(t[8]  - ((r[2]  - Bias) << shift));
        t[1]  = (ushort)(t[0]  - ((r[3]  - Bias) << shift));
        t[5]  = (ushort)(t[4]  - ((r[4]  - Bias) << shift));
        t[9]  = (ushort)(t[8]  - ((r[5]  - Bias) << shift));
        t[13] = (ushort)(t[12] - ((r[6]  - Bias) << shift));
        t[2]  = (ushort)(t[1]  - ((r[7]  - Bias) << shift));
        t[6]  = (ushort)(t[5]  - ((r[8]  - Bias) << shift));
        t[10] = (ushort)(t[9]  - ((r[9]  - Bias) << shift));
        t[14] = (ushort)(t[13] - ((r[10] - Bias) << shift));
        t[3]  = (ushort)(t[2]  - ((r[11] - Bias) << shift));
        t[7]  = (ushort)(t[6]  - ((r[12] - Bias) << shift));
        t[11] = (ushort)(t[10] - ((r[13] - Bias) << shift));
        t[15] = (ushort)(t[14] - ((r[14] - Bias) << shift));
    }

    /// <summary>
    /// Sign-magnitude HALF (s) → monotonic 16-bit (t) mapping.
    /// Maps the HALF number line to a monotonic uint16 ordering so
    /// arithmetic differences correspond to perceptual differences:
    /// most-negative HALF → 0x0000, ..., -0 → 0x7FFF, +0 → 0x8000,
    /// ..., max-positive HALF → 0xFFFF.
    /// </summary>
    public static ushort SToT(ushort s)
    {
        return (ushort)(((s & 0x8000) != 0) ? (~s & 0xFFFF) : (s | 0x8000));
    }

    public static ushort TToS(ushort t)
    {
        return (ushort)(((t & 0x8000) != 0) ? (t & 0x7FFF) : (~t & 0xFFFF));
    }

    /// <summary>
    /// Perceptual decode pass for non-linear channels: square the
    /// half-precision value (half-precision multiply, not float-then-cast,
    /// so the round-trip is exact in HALF arithmetic).
    /// </summary>
    private static void ApplyConvertToLinear(Span<ushort> s)
    {
        for (int i = 0; i < 16; i++)
        {
            Half h = BitConverter.UInt16BitsToHalf(s[i]);
            h = h * h;
            s[i] = BitConverter.HalfToUInt16Bits(h);
        }
    }

    /// <summary>
    /// Encode a B44 block payload. Used by tests to round-trip through
    /// the decoder. Production code only ever decodes. Returns the
    /// encoded byte array (always 14 bytes; B44A's 3-byte uniform form
    /// is handled by <see cref="Compress"/>).
    /// </summary>
    public static byte[] EncodeFullBlock(ushort[] s, bool pLinear)
    {
        ushort[] sCopy = (ushort[])s.Clone();
        if (!pLinear) ApplyConvertFromLinear(sCopy);
        Span<ushort> t = stackalloc ushort[16];
        for (int i = 0; i < 16; i++) t[i] = SToT(sCopy[i]);

        // Compute the 15 differences in the order the format expects.
        Span<int> d = stackalloc int[15];
        d[0]  = t[0]  - t[4];
        d[1]  = t[4]  - t[8];
        d[2]  = t[8]  - t[12];
        d[3]  = t[0]  - t[1];
        d[4]  = t[4]  - t[5];
        d[5]  = t[8]  - t[9];
        d[6]  = t[12] - t[13];
        d[7]  = t[1]  - t[2];
        d[8]  = t[5]  - t[6];
        d[9]  = t[9]  - t[10];
        d[10] = t[13] - t[14];
        d[11] = t[2]  - t[3];
        d[12] = t[6]  - t[7];
        d[13] = t[10] - t[11];
        d[14] = t[14] - t[15];

        // Pick smallest shift such that all |round(d / 2^shift)| ≤ 31
        // (so biased values fit in [0, 0x3F]).
        int shift = 0;
        Span<int> r = stackalloc int[15];
        while (shift <= 62)
        {
            bool ok = true;
            for (int i = 0; i < 15; i++)
            {
                int rounded = ShiftAndRound(d[i], shift);
                if (rounded < -32 || rounded > 31) { ok = false; break; }
                r[i] = rounded + Bias;
            }
            if (ok) break;
            shift++;
        }
        if (shift > 62) throw new InvalidOperationException("B44 shift overflow");

        var buf = new byte[14];
        buf[0] = (byte)(t[0] >> 8);
        buf[1] = (byte)(t[0] & 0xFF);
        buf[2] = (byte)((shift << 2) | (r[0] >> 4));
        buf[3] = (byte)((r[0] << 4) | (r[1] >> 2));
        buf[4] = (byte)((r[1] << 6) | r[2]);
        buf[5] = (byte)((r[3] << 2) | (r[4] >> 4));
        buf[6] = (byte)((r[4] << 4) | (r[5] >> 2));
        buf[7] = (byte)((r[5] << 6) | r[6]);
        buf[8] = (byte)((r[7] << 2) | (r[8] >> 4));
        buf[9] = (byte)((r[8] << 4) | (r[9] >> 2));
        buf[10] = (byte)((r[9] << 6) | r[10]);
        buf[11] = (byte)((r[11] << 2) | (r[12] >> 4));
        buf[12] = (byte)((r[12] << 4) | (r[13] >> 2));
        buf[13] = (byte)((r[13] << 6) | r[14]);
        return buf;
    }

    public static byte[] EncodeUniformBlock(ushort sValue, bool pLinear)
    {
        ushort s = sValue;
        if (!pLinear)
        {
            Half h = BitConverter.UInt16BitsToHalf(s);
            // Forward perceptual: sqrt for non-linear channels.
            h = (Half)Math.Sqrt((double)(float)h);
            s = BitConverter.HalfToUInt16Bits(h);
        }
        ushort t = SToT(s);
        return new byte[] { (byte)(t >> 8), (byte)(t & 0xFF), 0xFC };
    }

    private static void ApplyConvertFromLinear(ushort[] s)
    {
        for (int i = 0; i < 16; i++)
        {
            Half h = BitConverter.UInt16BitsToHalf(s[i]);
            // sqrt may produce NaN for negatives; cast through float
            // so the half-precision result is exactly representable.
            h = (Half)Math.Sqrt((double)(float)h);
            s[i] = BitConverter.HalfToUInt16Bits(h);
        }
    }

    /// <summary>
    /// Round-half-to-even of <paramref name="d"/> · 2^-shift. Matches
    /// libimf's shiftAndRound; needed so the encoder picks the same
    /// shift libimf would and the decoder's reverse-shift recovers
    /// the same t values bit-exactly when shift = 0.
    /// </summary>
    private static int ShiftAndRound(int d, int shift)
    {
        if (shift == 0) return d;
        int divisor = 1 << shift;
        int q = d / divisor;
        int rem = d - q * divisor;
        int half = divisor >> 1;
        if (d < 0) rem = -rem;
        if (rem > half) q += d > 0 ? 1 : -1;
        else if (rem == half && (q & 1) != 0) q += d > 0 ? 1 : -1;
        return q;
    }

    /// <summary>
    /// Compress raw HALF samples (per-row-per-channel-per-pixel
    /// layout) into a B44 / B44A block. Tests-only path; production
    /// code only ever decodes.
    /// </summary>
    public static byte[] Compress(byte[] src, int rows,
        IReadOnlyList<ExrB44ChannelInfo> channels, int width, bool b44a)
    {
        int rowStride = 0;
        foreach (var ch in channels) rowStride += width * ch.BytesPerSample;

        var ms = new System.IO.MemoryStream();
        int channelOff = 0;
        foreach (var ch in channels)
        {
            int bw = ch.BytesPerSample;
            if (bw == 2)
            {
                int blockRows = (rows + 3) / 4;
                int blockCols = (width + 3) / 4;
                for (int by = 0; by < blockRows; by++)
                {
                    int y0 = by * 4;
                    for (int bx = 0; bx < blockCols; bx++)
                    {
                        int x0 = bx * 4;
                        // Gather 16 samples (replicate edge samples for
                        // partial blocks at right / bottom boundaries).
                        var s = new ushort[16];
                        for (int yy = 0; yy < 4; yy++)
                        {
                            int srcRow = Math.Min(y0 + yy, rows - 1);
                            for (int xx = 0; xx < 4; xx++)
                            {
                                int srcX = Math.Min(x0 + xx, width - 1);
                                int byteOff = srcRow * rowStride + channelOff + srcX * bw;
                                s[yy * 4 + xx] = (ushort)(src[byteOff] | (src[byteOff + 1] << 8));
                            }
                        }
                        bool uniform = true;
                        for (int i = 1; i < 16; i++) if (s[i] != s[0]) { uniform = false; break; }
                        if (uniform && b44a)
                            ms.Write(EncodeUniformBlock(s[0], ch.PLinear));
                        else
                            ms.Write(EncodeFullBlock(s, ch.PLinear));
                    }
                }
            }
            else
            {
                // FLOAT/UINT: raw rows.
                int rowBytes = width * bw;
                for (int row = 0; row < rows; row++)
                    ms.Write(src, row * rowStride + channelOff, rowBytes);
            }
            channelOff += width * bw;
        }
        return ms.ToArray();
    }
}

internal readonly struct ExrB44ChannelInfo
{
    public int BytesPerSample { get; }
    public bool PLinear { get; }
    public ExrB44ChannelInfo(int bytesPerSample, bool pLinear)
    { BytesPerSample = bytesPerSample; PLinear = pLinear; }
}
