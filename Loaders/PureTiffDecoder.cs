using System;
using CosmoImage.Core;

namespace CosmoImage.Loaders;

/// <summary>
/// Pure-managed TIFF pixel decoder for the baseline uncompressed case.
/// Handles classic TIFF (32-bit offsets), Compression=None, chunky
/// PlanarConfiguration, single-page IFDs, strip-based layout, both
/// byte orders, BitsPerSample 8/16, and PhotometricInterpretation
/// 0 (WhiteIsZero), 1 (BlackIsZero), 2 (RGB), 3 (Palette).
///
/// <para>Returns <c>null</c> for everything else (BigTIFF, multi-page,
/// tiled, compressed, planar=2, FillOrder=2, predictor != 1, sample
/// formats other than unsigned int, exotic SamplesPerPixel). Callers
/// fall back to the Magick.NET path.</para>
/// </summary>
internal static class PureTiffDecoder
{
    public static VipsImage? TryDecode(byte[] bytes)
    {
        if (bytes == null || bytes.Length < 8) return null;
        bool le;
        if (bytes[0] == 0x49 && bytes[1] == 0x49) le = true;
        else if (bytes[0] == 0x4D && bytes[1] == 0x4D) le = false;
        else return null;

        ushort magic = ReadU16(bytes, 2, le);
        if (magic != 0x002A) return null;  // BigTIFF or invalid

        uint ifdOffset = ReadU32(bytes, 4, le);
        if (ifdOffset == 0 || ifdOffset + 6 > bytes.Length) return null;

        ushort numEntries = ReadU16(bytes, (int)ifdOffset, le);
        int entriesStart = (int)ifdOffset + 2;
        int afterEntries = entriesStart + numEntries * 12;
        if (afterEntries + 4 > bytes.Length) return null;

        // Multi-page → punt to Magick.
        uint nextIfd = ReadU32(bytes, afterEntries, le);
        if (nextIfd != 0) return null;

        long width = -1, height = -1;
        long compression = 1;
        long photometric = -1;
        long samplesPerPixel = 1;
        long planarConfig = 1;
        long rowsPerStrip = -1;
        long fillOrder = 1;
        long predictor = 1;
        long[] bitsPerSample = { 8L };
        long[] sampleFormat = { 1L };
        long[] stripOffsets = Array.Empty<long>();
        long[] stripByteCounts = Array.Empty<long>();
        long[] colorMap = Array.Empty<long>();
        bool tiled = false;

        for (int i = 0; i < numEntries; i++)
        {
            int e = entriesStart + i * 12;
            ushort tag = ReadU16(bytes, e, le);
            ushort type = ReadU16(bytes, e + 2, le);
            uint count = ReadU32(bytes, e + 4, le);

            // Tile-related tags → tiled layout, unsupported.
            if (tag == 322 || tag == 323 || tag == 324 || tag == 325)
            {
                tiled = true;
                continue;
            }

            switch (tag)
            {
                case 256: width = ReadOne(bytes, e, type, le, count); break;
                case 257: height = ReadOne(bytes, e, type, le, count); break;
                case 258: bitsPerSample = ReadAll(bytes, e, type, le, count) ?? bitsPerSample; break;
                case 259: compression = ReadOne(bytes, e, type, le, count); break;
                case 262: photometric = ReadOne(bytes, e, type, le, count); break;
                case 266: fillOrder = ReadOne(bytes, e, type, le, count); break;
                case 273: stripOffsets = ReadAll(bytes, e, type, le, count) ?? stripOffsets; break;
                case 277: samplesPerPixel = ReadOne(bytes, e, type, le, count); break;
                case 278: rowsPerStrip = ReadOne(bytes, e, type, le, count); break;
                case 279: stripByteCounts = ReadAll(bytes, e, type, le, count) ?? stripByteCounts; break;
                case 284: planarConfig = ReadOne(bytes, e, type, le, count); break;
                case 317: predictor = ReadOne(bytes, e, type, le, count); break;
                case 320: colorMap = ReadAll(bytes, e, type, le, count) ?? colorMap; break;
                case 339: sampleFormat = ReadAll(bytes, e, type, le, count) ?? sampleFormat; break;
            }
        }

        if (tiled) return null;
        if (width <= 0 || height <= 0) return null;
        if (compression != 1) return null;
        if (planarConfig != 1) return null;
        if (fillOrder != 1) return null;
        if (predictor != 1) return null;

        int bps = (int)bitsPerSample[0];
        if (bps != 8 && bps != 16) return null;
        for (int i = 0; i < bitsPerSample.Length; i++)
            if (bitsPerSample[i] != bps) return null;

        for (int i = 0; i < sampleFormat.Length; i++)
            if (sampleFormat[i] != 1) return null;

        int spp = (int)samplesPerPixel;
        if (spp < 1 || spp > 4) return null;

        if (photometric != 0 && photometric != 1 && photometric != 2 && photometric != 3) return null;
        if (photometric == 0 || photometric == 1)
        {
            if (spp != 1 && spp != 2) return null;
        }
        else if (photometric == 2)
        {
            if (spp != 3 && spp != 4) return null;
        }
        else  // palette
        {
            if (spp != 1) return null;
            int expected = 3 * (1 << bps);
            if (colorMap.Length != expected) return null;
        }

        if (stripOffsets.Length != stripByteCounts.Length || stripOffsets.Length == 0)
            return null;

        if (rowsPerStrip < 0) rowsPerStrip = height;

        int bytesPerSample = bps / 8;
        long inRowStride = width * spp * bytesPerSample;
        long totalIn = checked(height * inRowStride);
        if (totalIn > int.MaxValue) return null;

        // Gather strip bytes into a flat raw buffer.
        var raw = new byte[totalIn];
        long y = 0;
        for (int s = 0; s < stripOffsets.Length; s++)
        {
            long off = stripOffsets[s];
            long stripRows = Math.Min(rowsPerStrip, height - y);
            if (stripRows <= 0) break;
            long expected = stripRows * inRowStride;
            if (off < 0 || off + expected > bytes.Length) return null;
            Buffer.BlockCopy(bytes, (int)off, raw, (int)(y * inRowStride), (int)expected);
            y += stripRows;
        }
        if (y != height) return null;

        // Normalize 16-bit samples to host little-endian (matches existing
        // VipsImage convention; PurePngDecoder does the same).
        if (bps == 16 && !le)
            ByteSwap16InPlace(raw);

        // Apply photometric transformation.
        byte[] outPixels;
        int outBands;
        VipsBandFormat bandFormat = bps == 16 ? VipsBandFormat.UShort : VipsBandFormat.UChar;
        VipsInterpretation interp;

        if (photometric == 0)
        {
            // WhiteIsZero — invert color samples, leave alpha alone.
            outPixels = new byte[totalIn];
            Buffer.BlockCopy(raw, 0, outPixels, 0, (int)totalIn);
            InvertSamples(outPixels, (int)width, (int)height, spp, bps, alphaAtSample: spp == 2 ? 1 : -1);
            outBands = spp;
            interp = VipsInterpretation.BW;
        }
        else if (photometric == 1)
        {
            outPixels = raw;
            outBands = spp;
            interp = VipsInterpretation.BW;
        }
        else if (photometric == 2)
        {
            outPixels = raw;
            outBands = spp;
            interp = VipsInterpretation.RGB;
        }
        else  // palette
        {
            outPixels = ExpandPalette(raw, (int)width, (int)height, bps, colorMap);
            outBands = 3;
            interp = VipsInterpretation.RGB;
        }

        return new VipsImage
        {
            Width = (int)width,
            Height = (int)height,
            Bands = outBands,
            BandFormat = bandFormat,
            Interpretation = interp,
            Coding = VipsCoding.None,
            XRes = 1.0,
            YRes = 1.0,
            PixelsLazy = new Lazy<byte[]>(() => outPixels),
        };
    }

    private static void InvertSamples(byte[] buf, int width, int height, int spp, int bps, int alphaAtSample)
    {
        int pixels = width * height;
        if (bps == 8)
        {
            for (int p = 0; p < pixels; p++)
            {
                int basePos = p * spp;
                for (int s = 0; s < spp; s++)
                {
                    if (s == alphaAtSample) continue;
                    buf[basePos + s] = (byte)(0xFF - buf[basePos + s]);
                }
            }
        }
        else  // 16-bit, host-LE
        {
            for (int p = 0; p < pixels; p++)
            {
                int basePos = p * spp * 2;
                for (int s = 0; s < spp; s++)
                {
                    if (s == alphaAtSample) continue;
                    int pos = basePos + s * 2;
                    ushort v = (ushort)(buf[pos] | (buf[pos + 1] << 8));
                    ushort inv = (ushort)(0xFFFF - v);
                    buf[pos] = (byte)inv;
                    buf[pos + 1] = (byte)(inv >> 8);
                }
            }
        }
    }

    private static byte[] ExpandPalette(byte[] indices, int width, int height, int bps, long[] colorMap)
    {
        // ColorMap is laid out as N reds, then N greens, then N blues — each
        // entry a 16-bit unsigned. We scale 8-bit palette outputs to 8-bit
        // RGB (>>8) and keep 16-bit palette outputs as 16-bit RGB.
        int pixels = width * height;
        if (bps == 8)
        {
            int n = 256;
            var dst = new byte[pixels * 3];
            for (int i = 0; i < pixels; i++)
            {
                int idx = indices[i];
                dst[i * 3] = (byte)(((ushort)colorMap[idx]) >> 8);
                dst[i * 3 + 1] = (byte)(((ushort)colorMap[n + idx]) >> 8);
                dst[i * 3 + 2] = (byte)(((ushort)colorMap[2 * n + idx]) >> 8);
            }
            return dst;
        }
        else
        {
            int n = 1 << 16;
            var dst = new byte[pixels * 3 * 2];
            for (int i = 0; i < pixels; i++)
            {
                int srcPos = i * 2;
                ushort idx = (ushort)(indices[srcPos] | (indices[srcPos + 1] << 8));
                ushort r = (ushort)colorMap[idx];
                ushort g = (ushort)colorMap[n + idx];
                ushort b = (ushort)colorMap[2 * n + idx];
                int dp = i * 6;
                dst[dp] = (byte)r; dst[dp + 1] = (byte)(r >> 8);
                dst[dp + 2] = (byte)g; dst[dp + 3] = (byte)(g >> 8);
                dst[dp + 4] = (byte)b; dst[dp + 5] = (byte)(b >> 8);
            }
            return dst;
        }
    }

    private static void ByteSwap16InPlace(byte[] buf)
    {
        for (int i = 0; i + 1 < buf.Length; i += 2)
        {
            (buf[i], buf[i + 1]) = (buf[i + 1], buf[i]);
        }
    }

    private static int TypeSize(ushort type) => type switch
    {
        1 or 2 or 6 or 7 => 1,   // BYTE / ASCII / SBYTE / UNDEFINED
        3 or 8 => 2,             // SHORT / SSHORT
        4 or 9 or 11 => 4,       // LONG / SLONG / FLOAT
        5 or 10 or 12 => 8,      // RATIONAL / SRATIONAL / DOUBLE
        _ => 0,
    };

    private static long ReadOne(byte[] buf, int entryOffset, ushort type, bool le, uint count)
    {
        if (count != 1) return -1;
        int valOffset = entryOffset + 8;
        return type switch
        {
            1 or 7 => buf[valOffset],
            3 => ReadU16(buf, valOffset, le),
            4 => ReadU32(buf, valOffset, le),
            _ => -1,
        };
    }

    private static long[]? ReadAll(byte[] buf, int entryOffset, ushort type, bool le, uint count)
    {
        int sz = TypeSize(type);
        if (sz == 0) return null;
        long total = (long)count * sz;
        int dataOffset;
        if (total <= 4)
        {
            dataOffset = entryOffset + 8;
        }
        else
        {
            uint off = ReadU32(buf, entryOffset + 8, le);
            if (off + total > buf.Length) return null;
            dataOffset = (int)off;
        }
        var values = new long[count];
        for (int i = 0; i < count; i++)
        {
            int p = dataOffset + i * sz;
            values[i] = type switch
            {
                1 or 7 => buf[p],
                3 => ReadU16(buf, p, le),
                4 => ReadU32(buf, p, le),
                _ => 0,
            };
        }
        return values;
    }

    private static ushort ReadU16(byte[] buf, int offset, bool le) =>
        le ? (ushort)(buf[offset] | (buf[offset + 1] << 8))
           : (ushort)((buf[offset] << 8) | buf[offset + 1]);

    private static uint ReadU32(byte[] buf, int offset, bool le) =>
        le ? (uint)(buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16) | (buf[offset + 3] << 24))
           : (uint)((buf[offset] << 24) | (buf[offset + 1] << 16) | (buf[offset + 2] << 8) | buf[offset + 3]);
}
