using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using CosmoImage.Core;

namespace CosmoImage.Loaders;

/// <summary>
/// Pure-managed TIFF pixel decoder. Handles classic TIFF (32-bit
/// offsets), chunky PlanarConfiguration, single-page IFDs, strip-based
/// layout, both byte orders, BitsPerSample 8/16, and
/// PhotometricInterpretation 0 (WhiteIsZero), 1 (BlackIsZero), 2 (RGB),
/// 3 (Palette). Compression schemes supported:
///   1 = None,
///   5 = LZW (libtiff early-change variant),
///   8 / 32946 = Deflate (zlib-wrapped),
///   32773 = PackBits.
///
/// <para>Returns <c>null</c> for everything else (BigTIFF, multi-page,
/// tiled, JPEG-compressed, CCITT, planar=2, FillOrder=2, sample
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
        if (magic != 0x002A) return null;

        uint ifdOffset = ReadU32(bytes, 4, le);
        if (ifdOffset == 0) return null;

        var first = DecodeIfd(bytes, le, ifdOffset, out uint nextIfd);
        if (first == null) return null;
        if (nextIfd == 0) return first;

        // Multi-page chain. Walk it; require uniform dimensions/bands/format
        // since the flat-buffer "stack pages vertically" representation
        // (used by animated GIF/WebP/HEIF too) can't carry heterogeneous
        // pages. Heterogeneous → null → caller falls back to Magick.
        var pages = new List<VipsImage> { first };
        uint cur = nextIfd;
        while (cur != 0)
        {
            // Cycle / DoS guard.
            if (pages.Count > 4096) return null;
            var page = DecodeIfd(bytes, le, cur, out uint next);
            if (page == null) return null;
            if (page.Width != first.Width || page.Height != first.Height
                || page.Bands != first.Bands || page.BandFormat != first.BandFormat
                || page.Interpretation != first.Interpretation)
                return null;
            pages.Add(page);
            cur = next;
        }

        int frameW = first.Width, frameH = first.Height;
        int pelBytes = first.SizeOfPel;
        int frameBytes = frameW * frameH * pelBytes;
        var stacked = new byte[(long)frameBytes * pages.Count];
        for (int i = 0; i < pages.Count; i++)
        {
            var src = pages[i].PixelsLazy!.Value;
            Buffer.BlockCopy(src, 0, stacked, i * frameBytes, frameBytes);
        }

        var stitched = new VipsImage
        {
            Width = frameW,
            Height = frameH * pages.Count,
            Bands = first.Bands,
            BandFormat = first.BandFormat,
            Interpretation = first.Interpretation,
            Coding = VipsCoding.None,
            XRes = 1.0,
            YRes = 1.0,
            PixelsLazy = new Lazy<byte[]>(() => stacked),
        };
        stitched.Metadata["n-pages"] = pages.Count.ToString(CultureInfo.InvariantCulture);
        stitched.Metadata["page-height"] = frameH.ToString(CultureInfo.InvariantCulture);
        return stitched;
    }

    /// <summary>
    /// Decode a single IFD at <paramref name="ifdOffset"/> and report the
    /// <c>nextIfd</c> pointer so the multi-page walker can chain. Returns
    /// <c>null</c> for unsupported configurations (compression, planar,
    /// tiled, exotic SamplesPerPixel, etc.).
    /// </summary>
    private static VipsImage? DecodeIfd(byte[] bytes, bool le, uint ifdOffset, out uint nextIfd)
    {
        nextIfd = 0;
        if (ifdOffset == 0 || ifdOffset + 6 > bytes.Length) return null;

        ushort numEntries = ReadU16(bytes, (int)ifdOffset, le);
        int entriesStart = (int)ifdOffset + 2;
        int afterEntries = entriesStart + numEntries * 12;
        if (afterEntries + 4 > bytes.Length) return null;

        nextIfd = ReadU32(bytes, afterEntries, le);

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
        if (compression != 1 && compression != 5 && compression != 8
            && compression != 32946 && compression != 32773)
            return null;
        if (planarConfig != 1) return null;
        if (fillOrder != 1) return null;
        if (predictor != 1 && predictor != 2) return null;

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

        // Gather strip bytes into a flat raw buffer, decompressing per strip.
        var raw = new byte[totalIn];
        long y = 0;
        for (int s = 0; s < stripOffsets.Length; s++)
        {
            long off = stripOffsets[s];
            long enc = stripByteCounts[s];
            long stripRows = Math.Min(rowsPerStrip, height - y);
            if (stripRows <= 0) break;
            long expected = stripRows * inRowStride;
            if (off < 0 || enc < 0 || off + enc > bytes.Length) return null;
            int dst = (int)(y * inRowStride);

            bool ok = compression switch
            {
                1 => CopyRaw(bytes, (int)off, (int)enc, raw, dst, (int)expected),
                5 => DecompressLzw(bytes, (int)off, (int)enc, raw, dst, (int)expected),
                32773 => DecompressPackBits(bytes, (int)off, (int)enc, raw, dst, (int)expected),
                8 or 32946 => DecompressDeflate(bytes, (int)off, (int)enc, raw, dst, (int)expected),
                _ => false,
            };
            if (!ok) return null;
            y += stripRows;
        }
        if (y != height) return null;

        // Normalize 16-bit samples to host little-endian (matches existing
        // VipsImage convention; PurePngDecoder does the same).
        if (bps == 16 && !le)
            ByteSwap16InPlace(raw);

        // Predictor 2 = horizontal differencing — each sample is stored
        // as the wrap-around difference from the previous sample on the
        // same row. Reverse by left-to-right accumulation. Operates on
        // host-LE values for 16-bit (after the byte-swap above).
        if (predictor == 2)
            ApplyHorizontalUnpredictor(raw, (int)width, (int)height, spp, bps);

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

    private static void ApplyHorizontalUnpredictor(byte[] buf, int width, int height, int spp, int bps)
    {
        if (bps == 8)
        {
            for (int y = 0; y < height; y++)
            {
                int rowBase = y * width * spp;
                for (int x = 1; x < width; x++)
                {
                    int p = rowBase + x * spp;
                    int prev = p - spp;
                    for (int c = 0; c < spp; c++)
                        buf[p + c] = (byte)(buf[p + c] + buf[prev + c]);
                }
            }
        }
        else  // 16-bit, host-LE
        {
            int sampleStride = spp * 2;
            for (int y = 0; y < height; y++)
            {
                int rowBase = y * width * sampleStride;
                for (int x = 1; x < width; x++)
                {
                    int p = rowBase + x * sampleStride;
                    int prev = p - sampleStride;
                    for (int c = 0; c < spp; c++)
                    {
                        int pp = p + c * 2, pv = prev + c * 2;
                        ushort cur = (ushort)(buf[pp] | (buf[pp + 1] << 8));
                        ushort prv = (ushort)(buf[pv] | (buf[pv + 1] << 8));
                        ushort sum = (ushort)(cur + prv);
                        buf[pp] = (byte)sum;
                        buf[pp + 1] = (byte)(sum >> 8);
                    }
                }
            }
        }
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

    /// <summary>
    /// Copy <paramref name="expected"/> uncompressed bytes from
    /// <c>src[srcOff..]</c> into <c>dst[dstOff..]</c>. Encoded length
    /// must be at least the expected length (some encoders pad strips).
    /// </summary>
    private static bool CopyRaw(byte[] src, int srcOff, int srcLen,
        byte[] dst, int dstOff, int expected)
    {
        if (srcLen < expected) return false;
        Buffer.BlockCopy(src, srcOff, dst, dstOff, expected);
        return true;
    }

    /// <summary>
    /// PackBits RLE decompress (TIFF Compression=32773). Each run is
    /// prefixed by a signed control byte n: n in [0,127] copies n+1
    /// literal bytes; n in [-127,-1] replicates the next byte (-n)+1
    /// times; n == -128 is a no-op.
    /// </summary>
    private static bool DecompressPackBits(byte[] src, int srcOff, int srcLen,
        byte[] dst, int dstOff, int expected)
    {
        int sp = srcOff, sEnd = srcOff + srcLen;
        int dp = dstOff, dEnd = dstOff + expected;
        while (sp < sEnd && dp < dEnd)
        {
            sbyte n = (sbyte)src[sp++];
            if (n >= 0)
            {
                int run = n + 1;
                if (sp + run > sEnd || dp + run > dEnd) return false;
                Buffer.BlockCopy(src, sp, dst, dp, run);
                sp += run; dp += run;
            }
            else if (n != -128)
            {
                int run = -n + 1;
                if (sp >= sEnd || dp + run > dEnd) return false;
                byte v = src[sp++];
                for (int i = 0; i < run; i++) dst[dp++] = v;
            }
            // n == -128 is a noop per the PackBits spec.
        }
        return dp == dEnd;
    }

    /// <summary>
    /// zlib-wrapped Deflate (TIFF Compression=8, "Adobe Deflate", or
    /// 32946, the older PKZIP alias — both produce byte-identical
    /// streams). Raw-deflate (no zlib header) is rare but unsupported
    /// here; falls through to Magick.
    /// </summary>
    private static bool DecompressDeflate(byte[] src, int srcOff, int srcLen,
        byte[] dst, int dstOff, int expected)
    {
        try
        {
            using var ms = new MemoryStream(src, srcOff, srcLen);
            using var z = new ZLibStream(ms, CompressionMode.Decompress);
            int read = 0;
            while (read < expected)
            {
                int n = z.Read(dst, dstOff + read, expected - read);
                if (n == 0) return false;
                read += n;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// TIFF LZW decompress (Compression=5). Bit packing is MSB-first
    /// (high bit of each byte is the start of the next code). Code
    /// width starts at 9 and bumps to 10/11/12 using libtiff's
    /// "early change" rule — the variant emitted by every libtiff /
    /// ImageMagick / GDAL / Pillow encoder. Special codes: 256 = clear,
    /// 257 = end-of-information.
    /// </summary>
    private static bool DecompressLzw(byte[] src, int srcOff, int srcLen,
        byte[] dst, int dstOff, int expected)
    {
        const int MaxEntries = 4096;
        const int ClearCode = 256;
        const int EoiCode = 257;

        var prefix = new short[MaxEntries];
        var suffix = new byte[MaxEntries];
        for (int i = 0; i < 256; i++)
        {
            prefix[i] = -1;
            suffix[i] = (byte)i;
        }
        var sbuf = new byte[MaxEntries];

        int sp = srcOff, sEnd = srcOff + srcLen;
        int dp = dstOff, dEnd = dstOff + expected;
        int bitBuf = 0, bitCount = 0;

        int codeWidth = 9;
        int dictSize = 258;
        int prevCode = -1;

        while (true)
        {
            while (bitCount < codeWidth)
            {
                if (sp >= sEnd) return false;
                bitBuf = (bitBuf << 8) | src[sp++];
                bitCount += 8;
            }
            int code = (bitBuf >> (bitCount - codeWidth)) & ((1 << codeWidth) - 1);
            bitCount -= codeWidth;

            if (code == EoiCode) break;
            if (code == ClearCode)
            {
                codeWidth = 9;
                dictSize = 258;
                prevCode = -1;
                continue;
            }

            // Resolve code → walk dict back to a literal, collecting suffix bytes.
            int len = 0;
            int c;
            bool isKwKwK = false;
            if (code < dictSize)
            {
                c = code;
            }
            else if (code == dictSize && prevCode != -1)
            {
                c = prevCode;
                isKwKwK = true;
            }
            else
            {
                return false;
            }

            while (c >= 256)
            {
                sbuf[len++] = suffix[c];
                c = prefix[c];
            }
            sbuf[len++] = (byte)c;
            int firstChar = c;

            int emit = len + (isKwKwK ? 1 : 0);
            if (dp + emit > dEnd) return false;
            // sbuf[0..len-1] holds the string in reverse — emit reversed.
            for (int i = len - 1; i >= 0; i--) dst[dp++] = sbuf[i];
            // KwKwK: append firstChar AFTER the prevCode-string emission
            // so the output order is prevString + firstChar = "ABA"-style.
            if (isKwKwK) dst[dp++] = (byte)firstChar;

            // Add new entry: prevCode + firstChar(string(currentCode))
            if (prevCode != -1 && dictSize < MaxEntries)
            {
                prefix[dictSize] = (short)prevCode;
                suffix[dictSize] = (byte)firstChar;
                dictSize++;
                // libtiff early-change: bump width when next code to be
                // added would have value (1 << codeWidth) - 1 (one less
                // than the natural threshold).
                if (dictSize == (1 << codeWidth) - 1 && codeWidth < 12)
                    codeWidth++;
            }
            prevCode = code;
        }

        return dp == dEnd;
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
