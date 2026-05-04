using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace CosmoImage.Loaders;

/// <summary>
/// Pure-managed PNG pixel decoder. Handles 8-bit non-interlaced
/// streams of color types 0 (greyscale), 2 (RGB), 3 (palette), 4
/// (greyscale + alpha), and 6 (RGB + alpha), with <c>tRNS</c>
/// transparency expansion.
///
/// <para>Returns <c>null</c> for configurations we don't support
/// (bit depth other than 8, interlaced, malformed input). Callers
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
                    if (bitDepth != 8) return null;       // 1/2/4/16 not supported here
                    if (interlace != 0) return null;       // Adam7 not supported here
                    if (colorType != 0 && colorType != 2 && colorType != 3
                        && colorType != 4 && colorType != 6) return null;
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

        // bytes per pixel in the SOURCE color type (before any expansion).
        int srcBpp = colorType switch
        {
            0 => 1,    // greyscale
            2 => 3,    // RGB
            3 => 1,    // palette index
            4 => 2,    // greyscale + alpha
            6 => 4,    // RGBA
            _ => 0,
        };
        int srcStride = width * srcBpp;
        int expectedRawLen = (srcStride + 1) * height;
        if (raw.Length < expectedRawLen) return null;

        // Unfilter scanlines in place into a flat buffer (no filter bytes).
        var unfiltered = new byte[srcStride * height];
        if (!UnfilterRows(raw, unfiltered, width, height, srcBpp, srcStride)) return null;

        // Expand to output channels — apply tRNS / PLTE.
        bool hasAlphaInColorType = (colorType & 4) != 0;
        bool transparencyExpansion = trns != null && !hasAlphaInColorType;

        switch (colorType)
        {
            case 0:  // greyscale (1) → maybe + alpha from tRNS
                if (transparencyExpansion)
                {
                    channels = 2;
                    return ExpandGreyscaleTrns(unfiltered, width, height, trns!);
                }
                channels = 1;
                return unfiltered;
            case 2:  // RGB (3) → maybe + alpha from tRNS
                if (transparencyExpansion)
                {
                    channels = 4;
                    return ExpandRgbTrns(unfiltered, width, height, trns!);
                }
                channels = 3;
                return unfiltered;
            case 3:  // palette index (1) → RGB (3) or RGBA (4) via PLTE / tRNS
                if (plte == null) return null;
                if (trns != null) { channels = 4; return ExpandPaletteRgba(unfiltered, plte, trns); }
                channels = 3;
                return ExpandPaletteRgb(unfiltered, plte);
            case 4:  // greyscale + alpha (2)
                channels = 2;
                return unfiltered;
            case 6:  // RGBA (4)
                channels = 4;
                return unfiltered;
        }
        return null;
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
    private static bool UnfilterRows(byte[] raw, byte[] dst, int width, int height, int bpp, int stride)
    {
        int rawPos = 0;
        for (int y = 0; y < height; y++)
        {
            if (rawPos >= raw.Length) return false;
            byte filter = raw[rawPos++];
            int rowOff = y * stride;
            int aboveOff = (y - 1) * stride;
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
