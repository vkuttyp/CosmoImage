using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace CosmoImage.Loaders;

/// <summary>
/// Pure-managed PNG pixel decoder. Handles 8- and 16-bit streams,
/// progressive (Adam7) interlace, and color types 0 (greyscale),
/// 2 (RGB), 3 (palette), 4 (greyscale + alpha), and 6 (RGB + alpha),
/// with <c>tRNS</c> transparency expansion.
///
/// <para>Returns <c>null</c> for malformed input or configurations
/// the pipeline doesn't carry (bit depths outside {8, 16}). Callers
/// fall back to a different decoder in that case.</para>
/// </summary>
internal static class PurePngDecoder
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>
    /// Decode <paramref name="pngBytes"/> into a row-major raw pixel buffer.
    /// On success returns the buffer and sets <paramref name="channels"/>
    /// to 1, 2, 3, or 4 (matching <see cref="VipsImage.Bands"/>). Returns
    /// <c>null</c> for unsupported configurations or malformed input.
    /// </summary>
    public static byte[]? TryDecode(byte[] pngBytes, out int channels)
    {
        channels = 0;
        if (pngBytes == null || pngBytes.Length < 8) return null;
        for (int i = 0; i < 8; i++)
            if (pngBytes[i] != Signature[i]) return null;

        // Parse chunks.
        int width = 0, height = 0;
        byte bitDepth = 0, colorType = 0, interlace = 0;
        var idat = new MemoryStream();
        byte[]? plte = null;
        byte[]? trns = null;

        int p = 8;
        while (p + 8 <= pngBytes.Length)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(pngBytes.AsSpan(p, 4));
            uint type = BinaryPrimitives.ReadUInt32BigEndian(pngBytes.AsSpan(p + 4, 4));
            p += 8;
            if (p + (int)length + 4 > pngBytes.Length) return null;
            switch (type)
            {
                case 0x49484452:  // IHDR
                    if (length != 13) return null;
                    width = BinaryPrimitives.ReadInt32BigEndian(pngBytes.AsSpan(p, 4));
                    height = BinaryPrimitives.ReadInt32BigEndian(pngBytes.AsSpan(p + 4, 4));
                    bitDepth = pngBytes[p + 8];
                    colorType = pngBytes[p + 9];
                    interlace = pngBytes[p + 12];
                    if (bitDepth != 1 && bitDepth != 2 && bitDepth != 4
                        && bitDepth != 8 && bitDepth != 16) return null;
                    if (interlace != 0 && interlace != 1) return null;
                    if (colorType != 0 && colorType != 2 && colorType != 3
                        && colorType != 4 && colorType != 6) return null;
                    // Sub-8-bit is only legal for greyscale (CT 0) and palette (CT 3).
                    if (bitDepth < 8 && colorType != 0 && colorType != 3) return null;
                    // Palette never goes to 16-bit per spec.
                    if (colorType == 3 && bitDepth == 16) return null;
                    break;
                case 0x504C5445:  // PLTE
                    plte = new byte[length];
                    Buffer.BlockCopy(pngBytes, p, plte, 0, (int)length);
                    break;
                case 0x74524E53:  // tRNS
                    trns = new byte[length];
                    Buffer.BlockCopy(pngBytes, p, trns, 0, (int)length);
                    break;
                case 0x49444154:  // IDAT
                    idat.Write(pngBytes, p, (int)length);
                    break;
                case 0x49454E44:  // IEND
                    p = int.MaxValue / 2;  // signal end
                    break;
            }
            p += (int)length + 4;  // data + CRC
        }
        if (width == 0 || height == 0) return null;

        // Decompress IDAT.
        byte[] raw;
        try
        {
            idat.Position = 0;
            using var inflate = new ZLibStream(idat, CompressionMode.Decompress);
            using var output = new MemoryStream();
            inflate.CopyTo(output);
            raw = output.ToArray();
        }
        catch { return null; }

        // Bytes per pixel in the SOURCE color type, accounting for bit depth.
        int channelsPerPixel = colorType switch
        {
            0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0,
        };

        byte[]? unfiltered;
        if (bitDepth < 8)
        {
            // Sub-8-bit (1/2/4): only legal for greyscale (CT 0) and palette
            // (CT 3). PNG packs samples msb-first into bytes, with each row
            // padded up to a byte boundary. Filter bpp is 1 byte per spec.
            // Decode here into one-byte-per-sample, scaling greyscale samples
            // to fill 0..255 (palette indices stay raw — PLTE expansion later).
            unfiltered = DecodeSubByte(raw, width, height, channelsPerPixel, bitDepth, interlace, scaleGreyscale: colorType == 0);
            if (unfiltered == null) return null;
            bitDepth = 8;
        }
        else
        {
            int byteStep = bitDepth / 8;  // 1 or 2
            int srcBpp = channelsPerPixel * byteStep;

            unfiltered = new byte[width * height * srcBpp];
            if (interlace == 0)
            {
                int srcStride = width * srcBpp;
                int expectedRawLen = (srcStride + 1) * height;
                if (raw.Length < expectedRawLen) return null;
                if (!UnfilterRows(raw, 0, unfiltered, 0, width, height, srcBpp, srcStride)) return null;
            }
            else
            {
                // Adam7: 7 sub-passes with reduced resolution; each pass has
                // its own filter context. Decode each into a flat sub-image,
                // then scatter pixels into the final image at the appropriate
                // (x_start + c*x_step, y_start + r*y_step) coordinates.
                int rawOff = 0;
                for (int pass = 0; pass < 7; pass++)
                {
                    int xStart = Adam7XStart[pass];
                    int yStart = Adam7YStart[pass];
                    int xStep = Adam7XStep[pass];
                    int yStep = Adam7YStep[pass];
                    int passW = Math.Max(0, (width - xStart + xStep - 1) / xStep);
                    int passH = Math.Max(0, (height - yStart + yStep - 1) / yStep);
                    if (passW == 0 || passH == 0) continue;
                    int passStride = passW * srcBpp;
                    int passSize = passStride * passH;
                    if (rawOff + (passStride + 1) * passH > raw.Length) return null;
                    var passBuf = new byte[passSize];
                    if (!UnfilterRows(raw, rawOff, passBuf, 0, passW, passH, srcBpp, passStride)) return null;
                    rawOff += (passStride + 1) * passH;
                    // Scatter pass pixels into the final image.
                    for (int r = 0; r < passH; r++)
                    {
                        int dstY = yStart + r * yStep;
                        int srcRow = r * passStride;
                        for (int c = 0; c < passW; c++)
                        {
                            int dstX = xStart + c * xStep;
                            int dstOff = (dstY * width + dstX) * srcBpp;
                            Buffer.BlockCopy(passBuf, srcRow + c * srcBpp, unfiltered, dstOff, srcBpp);
                        }
                    }
                }
            }

            // Byte-swap for 16-bit: PNG stores big-endian uint16; convert to
            // host-endian (assume little-endian — same as the existing UShort
            // convention in VipsImage).
            if (bitDepth == 16) ByteSwap16(unfiltered);
        }

        // Expand to output channels — apply tRNS / PLTE.
        bool hasAlphaInColorType = (colorType & 4) != 0;
        bool transparencyExpansion = trns != null && !hasAlphaInColorType;

        switch (colorType)
        {
            case 0:  // greyscale → maybe + alpha from tRNS
                if (transparencyExpansion)
                {
                    channels = 2;
                    return bitDepth == 16
                        ? ExpandGreyscaleTrns16(unfiltered, width, height, trns!)
                        : ExpandGreyscaleTrns(unfiltered, width, height, trns!);
                }
                channels = 1;
                return unfiltered;
            case 2:  // RGB → maybe + alpha from tRNS
                if (transparencyExpansion)
                {
                    channels = 4;
                    return bitDepth == 16
                        ? ExpandRgbTrns16(unfiltered, width, height, trns!)
                        : ExpandRgbTrns(unfiltered, width, height, trns!);
                }
                channels = 3;
                return unfiltered;
            case 3:  // palette → RGB or RGBA via PLTE / tRNS (always 8-bit)
                if (plte == null) return null;
                if (trns != null) { channels = 4; return ExpandPaletteRgba(unfiltered, plte, trns); }
                channels = 3;
                return ExpandPaletteRgb(unfiltered, plte);
            case 4:  // greyscale + alpha
                channels = 2;
                return unfiltered;
            case 6:  // RGBA
                channels = 4;
                return unfiltered;
        }
        return null;
    }

    /// <summary>
    /// Decode a sub-8-bit IDAT stream (1/2/4-bit greyscale or palette)
    /// into a one-byte-per-sample buffer of size <c>width * height * channels</c>.
    /// Greyscale samples are scaled into 0..255 when <paramref name="scaleGreyscale"/>
    /// is true; palette indices are preserved verbatim for later PLTE lookup.
    /// Filter bpp is 1 byte per PNG spec §6.3 ("for sub-byte samples,
    /// <c>bpp</c> is rounded up to one byte"); row stride is the packed-bit
    /// length rounded up to a byte boundary.
    /// </summary>
    private static byte[]? DecodeSubByte(byte[] raw, int width, int height, int channels,
        int bitDepth, byte interlace, bool scaleGreyscale)
    {
        var dst = new byte[width * height * channels];

        if (interlace == 0)
        {
            int packedStride = (width * channels * bitDepth + 7) / 8;
            int expectedRawLen = (packedStride + 1) * height;
            if (raw.Length < expectedRawLen) return null;
            var packed = new byte[packedStride * height];
            if (!UnfilterRows(raw, 0, packed, 0, packedStride, height, 1, packedStride))
                return null;
            UnpackBitsToBytes(packed, packedStride, dst, 0, width, height, channels, bitDepth, scaleGreyscale);
            return dst;
        }

        // Adam7 with sub-8-bit: per pass, packed stride uses passW × channels × bitDepth.
        int rawOff = 0;
        for (int pass = 0; pass < 7; pass++)
        {
            int xStart = Adam7XStart[pass];
            int yStart = Adam7YStart[pass];
            int xStep = Adam7XStep[pass];
            int yStep = Adam7YStep[pass];
            int passW = Math.Max(0, (width - xStart + xStep - 1) / xStep);
            int passH = Math.Max(0, (height - yStart + yStep - 1) / yStep);
            if (passW == 0 || passH == 0) continue;
            int passPackedStride = (passW * channels * bitDepth + 7) / 8;
            if (rawOff + (passPackedStride + 1) * passH > raw.Length) return null;
            var packedPass = new byte[passPackedStride * passH];
            if (!UnfilterRows(raw, rawOff, packedPass, 0, passPackedStride, passH, 1, passPackedStride))
                return null;
            rawOff += (passPackedStride + 1) * passH;

            var passBytes = new byte[passW * passH * channels];
            UnpackBitsToBytes(packedPass, passPackedStride, passBytes, 0, passW, passH, channels, bitDepth, scaleGreyscale);

            for (int r = 0; r < passH; r++)
            {
                int dstY = yStart + r * yStep;
                int srcRow = r * passW * channels;
                for (int c = 0; c < passW; c++)
                {
                    int dstX = xStart + c * xStep;
                    int dstOff = (dstY * width + dstX) * channels;
                    Buffer.BlockCopy(passBytes, srcRow + c * channels, dst, dstOff, channels);
                }
            }
        }
        return dst;
    }

    /// <summary>Bit-unpack msb-first packed samples into one byte per sample.</summary>
    private static void UnpackBitsToBytes(byte[] packed, int packedStride, byte[] dst, int dstStartOff,
        int width, int height, int channels, int bitDepth, bool scaleGreyscale)
    {
        // 1-bit→×255, 2-bit→×85, 4-bit→×17 stretches into 0..255.
        // Palette indices (scaleGreyscale=false) keep their raw value.
        int scale = !scaleGreyscale ? 1 : (bitDepth == 1 ? 255 : bitDepth == 2 ? 85 : 17);
        int mask = (1 << bitDepth) - 1;
        int samplesPerRow = width * channels;
        int outStride = samplesPerRow;
        for (int y = 0; y < height; y++)
        {
            int srcRow = y * packedStride;
            int dstRow = dstStartOff + y * outStride;
            for (int s = 0; s < samplesPerRow; s++)
            {
                int bitPos = s * bitDepth;
                int bytePos = bitPos >> 3;
                int shift = 8 - bitDepth - (bitPos & 7);
                int sample = (packed[srcRow + bytePos] >> shift) & mask;
                dst[dstRow + s] = (byte)(sample * scale);
            }
        }
    }

    // Adam7 pass-table per PNG spec §8.2.
    private static readonly int[] Adam7XStart = { 0, 4, 0, 2, 0, 1, 0 };
    private static readonly int[] Adam7YStart = { 0, 0, 4, 0, 2, 0, 1 };
    private static readonly int[] Adam7XStep  = { 8, 8, 4, 4, 2, 2, 1 };
    private static readonly int[] Adam7YStep  = { 8, 8, 8, 4, 4, 2, 2 };

    private static void ByteSwap16(byte[] buf)
    {
        for (int i = 0; i + 1 < buf.Length; i += 2)
        {
            (buf[i], buf[i + 1]) = (buf[i + 1], buf[i]);
        }
    }

    /// <summary>
    /// Reverse PNG scanline filters into a flat unfiltered buffer.
    /// Per scanline: first byte = filter type (0..4), then srcStride
    /// bytes of filtered data. Filter algorithm:
    ///   0 None:  byte = filtered
    ///   1 Sub:   byte = filtered + leftBytes(bpp back)
    ///   2 Up:    byte = filtered + above
    ///   3 Avg:   byte = filtered + (left + above) / 2
    ///   4 Paeth: byte = filtered + paeth(left, above, aboveLeft)
    /// </summary>
    private static bool UnfilterRows(byte[] raw, int rawStartOff, byte[] dst, int dstStartOff,
        int width, int height, int bpp, int stride)
    {
        int rawPos = rawStartOff;
        for (int y = 0; y < height; y++)
        {
            if (rawPos >= raw.Length) return false;
            byte filter = raw[rawPos++];
            int rowOff = dstStartOff + y * stride;
            int aboveOff = dstStartOff + (y - 1) * stride;
            if (rawPos + stride > raw.Length) return false;

            switch (filter)
            {
                case 0:  // None
                    Buffer.BlockCopy(raw, rawPos, dst, rowOff, stride);
                    break;
                case 1:  // Sub
                    for (int x = 0; x < stride; x++)
                    {
                        byte left = x < bpp ? (byte)0 : dst[rowOff + x - bpp];
                        dst[rowOff + x] = (byte)(raw[rawPos + x] + left);
                    }
                    break;
                case 2:  // Up
                    for (int x = 0; x < stride; x++)
                    {
                        byte above = y == 0 ? (byte)0 : dst[aboveOff + x];
                        dst[rowOff + x] = (byte)(raw[rawPos + x] + above);
                    }
                    break;
                case 3:  // Avg
                    for (int x = 0; x < stride; x++)
                    {
                        byte left = x < bpp ? (byte)0 : dst[rowOff + x - bpp];
                        byte above = y == 0 ? (byte)0 : dst[aboveOff + x];
                        dst[rowOff + x] = (byte)(raw[rawPos + x] + ((left + above) / 2));
                    }
                    break;
                case 4:  // Paeth
                    for (int x = 0; x < stride; x++)
                    {
                        byte left = x < bpp ? (byte)0 : dst[rowOff + x - bpp];
                        byte above = y == 0 ? (byte)0 : dst[aboveOff + x];
                        byte upLeft = (y == 0 || x < bpp) ? (byte)0 : dst[aboveOff + x - bpp];
                        dst[rowOff + x] = (byte)(raw[rawPos + x] + Paeth(left, above, upLeft));
                    }
                    break;
                default:
                    return false;
            }
            rawPos += stride;
        }
        return true;
    }

    private static byte Paeth(byte a, byte b, byte c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        if (pb <= pc) return b;
        return c;
    }

    private static byte[] ExpandGreyscaleTrns(byte[] src, int w, int h, byte[] trns)
    {
        // tRNS for greyscale 8-bit: 2 bytes (big-endian), the lower
        // byte is the transparent grey value.
        byte transGrey = trns.Length >= 2 ? trns[1] : (byte)0;
        var dst = new byte[w * h * 2];
        for (int i = 0; i < w * h; i++)
        {
            byte v = src[i];
            dst[i * 2 + 0] = v;
            dst[i * 2 + 1] = v == transGrey ? (byte)0 : (byte)255;
        }
        return dst;
    }

    private static byte[] ExpandRgbTrns(byte[] src, int w, int h, byte[] trns)
    {
        byte tR = 0, tG = 0, tB = 0;
        if (trns.Length >= 6) { tR = trns[1]; tG = trns[3]; tB = trns[5]; }
        var dst = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            byte r = src[i * 3 + 0], g = src[i * 3 + 1], b = src[i * 3 + 2];
            dst[i * 4 + 0] = r; dst[i * 4 + 1] = g; dst[i * 4 + 2] = b;
            dst[i * 4 + 3] = (r == tR && g == tG && b == tB) ? (byte)0 : (byte)255;
        }
        return dst;
    }

    /// <summary>
    /// 16-bit greyscale + tRNS → 16-bit greyscale + alpha (4 bytes per
    /// pixel, host-endian). tRNS for type-0 16-bit holds a single
    /// big-endian uint16 transparent value.
    /// </summary>
    private static byte[] ExpandGreyscaleTrns16(byte[] src, int w, int h, byte[] trns)
    {
        ushort transGrey = trns.Length >= 2
            ? (ushort)((trns[0] << 8) | trns[1])
            : (ushort)0;
        var dst = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            byte lo = src[i * 2 + 0], hi = src[i * 2 + 1];
            ushort v = (ushort)(lo | (hi << 8));  // already byte-swapped to host-LE
            dst[i * 4 + 0] = lo;
            dst[i * 4 + 1] = hi;
            ushort alpha = v == transGrey ? (ushort)0 : (ushort)0xFFFF;
            dst[i * 4 + 2] = (byte)(alpha & 0xFF);
            dst[i * 4 + 3] = (byte)(alpha >> 8);
        }
        return dst;
    }

    /// <summary>16-bit RGB + tRNS → 16-bit RGBA. tRNS holds three big-endian uint16s (R, G, B).</summary>
    private static byte[] ExpandRgbTrns16(byte[] src, int w, int h, byte[] trns)
    {
        ushort tR = 0, tG = 0, tB = 0;
        if (trns.Length >= 6)
        {
            tR = (ushort)((trns[0] << 8) | trns[1]);
            tG = (ushort)((trns[2] << 8) | trns[3]);
            tB = (ushort)((trns[4] << 8) | trns[5]);
        }
        var dst = new byte[w * h * 8];
        for (int i = 0; i < w * h; i++)
        {
            int s = i * 6, d = i * 8;
            // src is already host-LE after ByteSwap16.
            ushort r = (ushort)(src[s + 0] | (src[s + 1] << 8));
            ushort g = (ushort)(src[s + 2] | (src[s + 3] << 8));
            ushort b = (ushort)(src[s + 4] | (src[s + 5] << 8));
            dst[d + 0] = src[s + 0]; dst[d + 1] = src[s + 1];
            dst[d + 2] = src[s + 2]; dst[d + 3] = src[s + 3];
            dst[d + 4] = src[s + 4]; dst[d + 5] = src[s + 5];
            ushort alpha = (r == tR && g == tG && b == tB) ? (ushort)0 : (ushort)0xFFFF;
            dst[d + 6] = (byte)(alpha & 0xFF);
            dst[d + 7] = (byte)(alpha >> 8);
        }
        return dst;
    }

    private static byte[] ExpandPaletteRgb(byte[] indices, byte[] plte)
    {
        var dst = new byte[indices.Length * 3];
        for (int i = 0; i < indices.Length; i++)
        {
            int p = indices[i] * 3;
            if (p + 3 > plte.Length) { dst[i * 3 + 0] = 0; dst[i * 3 + 1] = 0; dst[i * 3 + 2] = 0; continue; }
            dst[i * 3 + 0] = plte[p + 0];
            dst[i * 3 + 1] = plte[p + 1];
            dst[i * 3 + 2] = plte[p + 2];
        }
        return dst;
    }

    private static byte[] ExpandPaletteRgba(byte[] indices, byte[] plte, byte[] trns)
    {
        var dst = new byte[indices.Length * 4];
        for (int i = 0; i < indices.Length; i++)
        {
            int idx = indices[i];
            int p = idx * 3;
            if (p + 3 > plte.Length) continue;
            dst[i * 4 + 0] = plte[p + 0];
            dst[i * 4 + 1] = plte[p + 1];
            dst[i * 4 + 2] = plte[p + 2];
            dst[i * 4 + 3] = idx < trns.Length ? trns[idx] : (byte)255;
        }
        return dst;
    }
}
