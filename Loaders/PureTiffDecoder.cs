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
        bool bigTiff;
        ulong ifdOffset;
        if (magic == 0x002A)
        {
            // Classic TIFF: 4-byte offset to IFD0 at byte 4.
            bigTiff = false;
            ifdOffset = ReadU32(bytes, 4, le);
        }
        else if (magic == 0x002B)
        {
            // BigTIFF: bytesize-of-offsets (must be 8) + reserved + 8-byte offset.
            bigTiff = true;
            if (bytes.Length < 16) return null;
            ushort offsetSize = ReadU16(bytes, 4, le);
            ushort reserved = ReadU16(bytes, 6, le);
            if (offsetSize != 8 || reserved != 0) return null;
            ifdOffset = ReadU64(bytes, 8, le);
        }
        else
        {
            return null;
        }
        if (ifdOffset == 0) return null;

        var first = DecodeIfd(bytes, le, bigTiff, ifdOffset, out ulong nextIfd);
        if (first == null) return null;
        if (nextIfd == 0) return first;

        // Multi-page chain. Walk it; require uniform dimensions/bands/format
        // since the flat-buffer "stack pages vertically" representation
        // (used by animated GIF/WebP/HEIF too) can't carry heterogeneous
        // pages. Heterogeneous → null → caller falls back to Magick.
        var pages = new List<VipsImage> { first };
        ulong cur = nextIfd;
        while (cur != 0)
        {
            // Cycle / DoS guard.
            if (pages.Count > 4096) return null;
            var page = DecodeIfd(bytes, le, bigTiff, cur, out ulong next);
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
    private static VipsImage? DecodeIfd(byte[] bytes, bool le, bool bigTiff, ulong ifdOffset, out ulong nextIfd)
    {
        nextIfd = 0;
        if (ifdOffset == 0 || ifdOffset > (ulong)bytes.Length) return null;

        // Classic: 2-byte count + 12-byte entries + 4-byte next-IFD pointer.
        // BigTIFF: 8-byte count + 20-byte entries + 8-byte next-IFD pointer.
        int countSize = bigTiff ? 8 : 2;
        int entrySize = bigTiff ? 20 : 12;
        int nextSize = bigTiff ? 8 : 4;

        if (ifdOffset + (ulong)countSize > (ulong)bytes.Length) return null;
        ulong numEntries = bigTiff
            ? ReadU64(bytes, (int)ifdOffset, le)
            : ReadU16(bytes, (int)ifdOffset, le);
        if (numEntries > 65535) return null;  // sanity cap

        int entriesStart = (int)ifdOffset + countSize;
        long afterEntries = (long)entriesStart + (long)numEntries * entrySize;
        if (afterEntries + nextSize > bytes.Length) return null;

        nextIfd = bigTiff
            ? ReadU64(bytes, (int)afterEntries, le)
            : ReadU32(bytes, (int)afterEntries, le);

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
        long tileWidth = -1, tileLength = -1;
        long[] tileOffsets = Array.Empty<long>();
        long[] tileByteCounts = Array.Empty<long>();
        byte[] jpegTables = Array.Empty<byte>();

        for (ulong i = 0; i < numEntries; i++)
        {
            int e = entriesStart + (int)i * entrySize;
            ushort tag = ReadU16(bytes, e, le);
            ushort type = ReadU16(bytes, e + 2, le);
            ulong count = bigTiff
                ? ReadU64(bytes, e + 4, le)
                : ReadU32(bytes, e + 4, le);

            switch (tag)
            {
                case 256: width = ReadOne(bytes, e, type, le, count, bigTiff); break;
                case 257: height = ReadOne(bytes, e, type, le, count, bigTiff); break;
                case 258: bitsPerSample = ReadAll(bytes, e, type, le, count, bigTiff) ?? bitsPerSample; break;
                case 259: compression = ReadOne(bytes, e, type, le, count, bigTiff); break;
                case 262: photometric = ReadOne(bytes, e, type, le, count, bigTiff); break;
                case 266: fillOrder = ReadOne(bytes, e, type, le, count, bigTiff); break;
                case 273: stripOffsets = ReadAll(bytes, e, type, le, count, bigTiff) ?? stripOffsets; break;
                case 277: samplesPerPixel = ReadOne(bytes, e, type, le, count, bigTiff); break;
                case 278: rowsPerStrip = ReadOne(bytes, e, type, le, count, bigTiff); break;
                case 279: stripByteCounts = ReadAll(bytes, e, type, le, count, bigTiff) ?? stripByteCounts; break;
                case 284: planarConfig = ReadOne(bytes, e, type, le, count, bigTiff); break;
                case 317: predictor = ReadOne(bytes, e, type, le, count, bigTiff); break;
                case 320: colorMap = ReadAll(bytes, e, type, le, count, bigTiff) ?? colorMap; break;
                case 322: tileWidth = ReadOne(bytes, e, type, le, count, bigTiff); break;
                case 323: tileLength = ReadOne(bytes, e, type, le, count, bigTiff); break;
                case 324: tileOffsets = ReadAll(bytes, e, type, le, count, bigTiff) ?? tileOffsets; break;
                case 325: tileByteCounts = ReadAll(bytes, e, type, le, count, bigTiff) ?? tileByteCounts; break;
                case 339: sampleFormat = ReadAll(bytes, e, type, le, count, bigTiff) ?? sampleFormat; break;
                case 347: jpegTables = ReadBytes(bytes, e, type, le, count, bigTiff) ?? jpegTables; break;
            }
        }

        bool tiled = tileWidth > 0 || tileLength > 0 || tileOffsets.Length > 0;
        if (width <= 0 || height <= 0) return null;
        if (compression != 1 && compression != 5 && compression != 7 && compression != 8
            && compression != 32946 && compression != 32773)
            return null;
        // JPEG-compressed TIFFs encode entire strips/tiles as JPEG datastreams
        // and predictor doesn't apply; planar=2 + JPEG combo is rare and
        // we don't implement it.
        if (compression == 7 && (predictor != 1 || planarConfig != 1)) return null;
        if (planarConfig != 1 && planarConfig != 2) return null;
        // FillOrder=2 only matters for sub-byte data (bps < 8). Our bps
        // is restricted to {8, 16, 32} below, so FillOrder is effectively
        // a no-op — accept either value.
        if (fillOrder != 1 && fillOrder != 2) return null;
        // Predictor 1 = none, 2 = horizontal differencing (int),
        // 3 = floating-point predictor (byte-shuffled diff, FP only).
        if (predictor != 1 && predictor != 2 && predictor != 3) return null;

        int bps = (int)bitsPerSample[0];
        for (int i = 0; i < bitsPerSample.Length; i++)
            if (bitsPerSample[i] != bps) return null;

        // Validate sample format: 1 = unsigned int, 3 = IEEE float.
        // All channels must agree; the existing code only checks the
        // first when only one entry is present.
        int sampleFmt = sampleFormat.Length == 0 ? 1 : (int)sampleFormat[0];
        for (int i = 0; i < sampleFormat.Length; i++)
            if (sampleFormat[i] != sampleFmt) return null;
        if (sampleFmt != 1 && sampleFmt != 3) return null;
        if (sampleFmt == 1 && bps != 8 && bps != 16) return null;
        if (sampleFmt == 3 && bps != 32) return null;
        // Predictor 2 doesn't apply to float; predictor 3 only applies to float.
        if (sampleFmt == 1 && predictor == 3) return null;
        if (sampleFmt == 3 && predictor == 2) return null;

        int spp = (int)samplesPerPixel;
        if (spp < 1 || spp > 4) return null;

        // Photometric 6 (YCbCr) is allowed only with JPEG compression — we
        // hand the YCbCr→RGB conversion off to the JPEG decoder there.
        bool jpegNeedsYcbcr = photometric == 6 && compression == 7;
        if (photometric != 0 && photometric != 1 && photometric != 2
            && photometric != 3 && photometric != 5 && !jpegNeedsYcbcr) return null;
        // Treat the YCbCr-JPEG case as RGB downstream: the strip decode
        // emits RGB pixels, photometric processing then sees regular RGB.
        if (jpegNeedsYcbcr) photometric = 2;
        // Float samples don't have a meaningful "invert" or palette
        // interpretation — restrict to plain BlackIsZero / RGB.
        if (sampleFmt == 3 && photometric != 1 && photometric != 2) return null;
        if (photometric == 0 || photometric == 1)
        {
            if (spp != 1 && spp != 2) return null;
        }
        else if (photometric == 2)
        {
            if (spp != 3 && spp != 4) return null;
        }
        else if (photometric == 3)  // palette — int-only
        {
            if (spp != 1) return null;
            int expected = 3 * (1 << bps);
            if (colorMap.Length != expected) return null;
        }
        else  // photometric == 5, CMYK
        {
            // 4 inks (CMYK); some prepress files add spot inks via
            // ExtraSamples which we don't currently support — limit to spp=4.
            if (spp != 4) return null;
        }

        int bytesPerSample = bps / 8;
        long inRowStride = width * spp * bytesPerSample;
        long totalIn = checked(height * inRowStride);
        if (totalIn > int.MaxValue) return null;

        var raw = new byte[totalIn];

        if (tiled)
        {
            if (tileWidth <= 0 || tileLength <= 0) return null;
            if (tileOffsets.Length != tileByteCounts.Length || tileOffsets.Length == 0) return null;
            if (planarConfig == 2)
            {
                // Per-channel tiles: SPP × tilesPerImage offsets, in
                // plane-major order (all tiles of plane 0, then plane 1).
                // Decode each plane via the existing tiled path with
                // spp=1, then byte-interleave into the chunky output —
                // mirrors the strip+planar=2 path.
                int tilesAcross = ((int)width + (int)tileWidth - 1) / (int)tileWidth;
                int tilesDown = ((int)height + (int)tileLength - 1) / (int)tileLength;
                int tilesPerPlane = tilesAcross * tilesDown;
                if (tileOffsets.Length != (long)tilesPerPlane * spp) return null;

                int planeSize = (int)((long)width * height * bytesPerSample);
                long planeRowStride = (long)width * bytesPerSample;
                var planes = new byte[spp][];
                for (int c = 0; c < spp; c++)
                {
                    planes[c] = new byte[planeSize];
                    var planeOffsets = new long[tilesPerPlane];
                    var planeCounts = new long[tilesPerPlane];
                    for (int t = 0; t < tilesPerPlane; t++)
                    {
                        planeOffsets[t] = tileOffsets[c * tilesPerPlane + t];
                        planeCounts[t] = tileByteCounts[c * tilesPerPlane + t];
                    }
                    if (!DecodeTiles(bytes, planes[c], (int)width, (int)height,
                        (int)tileWidth, (int)tileLength,
                        spp: 1, bytesPerSample, bps, (int)compression, (int)predictor, le,
                        planeOffsets, planeCounts, jpegTables, jpegNeedsYcbcr))
                        return null;
                }

                for (int y = 0; y < height; y++)
                {
                    int rowBase = y * (int)inRowStride;
                    int planeRowBase = y * (int)planeRowStride;
                    for (int x = 0; x < width; x++)
                    {
                        int chunkPx = rowBase + x * spp * bytesPerSample;
                        int planePx = planeRowBase + x * bytesPerSample;
                        for (int c = 0; c < spp; c++)
                            for (int b = 0; b < bytesPerSample; b++)
                                raw[chunkPx + c * bytesPerSample + b] = planes[c][planePx + b];
                    }
                }
            }
            else if (!DecodeTiles(bytes, raw, (int)width, (int)height, (int)tileWidth, (int)tileLength,
                spp, bytesPerSample, bps, (int)compression, (int)predictor, le,
                tileOffsets, tileByteCounts, jpegTables, jpegNeedsYcbcr))
                return null;
        }
        else if (planarConfig == 2)
        {
            // Per-channel strips: SPP × stripsPerPlane offsets, each
            // strip carries one channel's worth of data. Decode each
            // plane independently into its own buffer, then interleave.
            if (rowsPerStrip < 0) rowsPerStrip = height;
            int stripsPerPlane = (int)((height + rowsPerStrip - 1) / rowsPerStrip);
            if (stripOffsets.Length != stripByteCounts.Length) return null;
            if (stripOffsets.Length != (long)stripsPerPlane * spp) return null;

            int planeSize = (int)((long)width * height * bytesPerSample);
            long planeRowStride = (long)width * bytesPerSample;
            var planes = new byte[spp][];
            for (int c = 0; c < spp; c++)
            {
                planes[c] = new byte[planeSize];
                var planeOffsets = new long[stripsPerPlane];
                var planeCounts = new long[stripsPerPlane];
                for (int s = 0; s < stripsPerPlane; s++)
                {
                    planeOffsets[s] = stripOffsets[c * stripsPerPlane + s];
                    planeCounts[s] = stripByteCounts[c * stripsPerPlane + s];
                }
                if (!DecodeStrips(bytes, planes[c], (int)width, (int)height, (int)rowsPerStrip,
                    spp: 1, bytesPerSample, bps, (int)compression, (int)predictor, le,
                    planeRowStride, planeOffsets, planeCounts, jpegTables, jpegNeedsYcbcr))
                    return null;
            }

            // Interleave planes into chunky raw, byte-by-byte (handles
            // any bytesPerSample without special-casing).
            for (int y = 0; y < height; y++)
            {
                int rowBase = y * (int)inRowStride;
                int planeRowBase = y * (int)planeRowStride;
                for (int x = 0; x < width; x++)
                {
                    int chunkPx = rowBase + x * spp * bytesPerSample;
                    int planePx = planeRowBase + x * bytesPerSample;
                    for (int c = 0; c < spp; c++)
                        for (int b = 0; b < bytesPerSample; b++)
                            raw[chunkPx + c * bytesPerSample + b] = planes[c][planePx + b];
                }
            }
        }
        else
        {
            if (stripOffsets.Length != stripByteCounts.Length || stripOffsets.Length == 0)
                return null;
            if (rowsPerStrip < 0) rowsPerStrip = height;
            if (!DecodeStrips(bytes, raw, (int)width, (int)height, (int)rowsPerStrip,
                spp, bytesPerSample, bps, (int)compression, (int)predictor, le, inRowStride,
                stripOffsets, stripByteCounts, jpegTables, jpegNeedsYcbcr))
                return null;
        }

        // (Byte-swap + predictor are applied inside DecodeStrips / DecodeTiles
        // so 16-bit values are host-LE and predictor=2 is unwound at the
        // appropriate stride before we land here.)

        // Apply photometric transformation.
        byte[] outPixels;
        int outBands;
        VipsBandFormat bandFormat = sampleFmt == 3
            ? VipsBandFormat.Float
            : (bps == 16 ? VipsBandFormat.UShort : VipsBandFormat.UChar);
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
        else if (photometric == 3)  // palette
        {
            outPixels = ExpandPalette(raw, (int)width, (int)height, bps, colorMap);
            outBands = 3;
            interp = VipsInterpretation.RGB;
        }
        else  // photometric == 5, CMYK
        {
            outPixels = raw;
            outBands = spp;
            interp = VipsInterpretation.CMYK;
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

    /// <summary>
    /// Decompress strip-organised TIFF pixels into <paramref name="raw"/>,
    /// then byte-swap 16-bit samples to host LE and unwind predictor=2
    /// across full image rows.
    /// </summary>
    private static bool DecodeStrips(byte[] bytes, byte[] raw,
        int width, int height, int rowsPerStrip,
        int spp, int bytesPerSample, int bps,
        int compression, int predictor, bool le, long inRowStride,
        long[] stripOffsets, long[] stripByteCounts, byte[] jpegTables,
        bool jpegYcbcr = false)
    {
        long y = 0;
        for (int s = 0; s < stripOffsets.Length; s++)
        {
            long off = stripOffsets[s];
            long enc = stripByteCounts[s];
            long stripRows = Math.Min(rowsPerStrip, height - y);
            if (stripRows <= 0) break;
            long expected = stripRows * inRowStride;
            if (off < 0 || enc < 0 || off + enc > bytes.Length) return false;
            int dst = (int)(y * inRowStride);

            if (!Decompress(bytes, (int)off, (int)enc, raw, dst, (int)expected, compression,
                jpegTables: jpegTables, jpegWidth: width, jpegHeight: (int)stripRows,
                jpegRowStride: (int)inRowStride, jpegYcbcr: jpegYcbcr))
                return false;
            y += stripRows;
        }
        if (y != height) return false;

        // Predictor=3 reverse runs FIRST, on stored byte order; then byte-swap;
        // then predictor=2 (which is mutually exclusive with predictor=3).
        if (predictor == 3) ApplyFloatUnpredictor(raw, width, height, spp);
        if (!le)
        {
            if (bps == 16) ByteSwap16InPlace(raw);
            else if (bps == 32) ByteSwap32InPlace(raw);
        }
        if (predictor == 2) ApplyHorizontalUnpredictor(raw, width, height, spp, bps);
        return true;
    }

    /// <summary>
    /// Decode a TIFF tile grid: each tile is decompressed into a fixed-size
    /// scratch buffer (always padded to <c>tileWidth × tileLength</c> per
    /// spec), byte-swapped, predictor-unwound at tile-row stride, then
    /// blitted into the image buffer with edge clamping.
    /// </summary>
    private static bool DecodeTiles(byte[] bytes, byte[] raw,
        int width, int height, int tileWidth, int tileLength,
        int spp, int bytesPerSample, int bps,
        int compression, int predictor, bool le,
        long[] tileOffsets, long[] tileByteCounts, byte[] jpegTables,
        bool jpegYcbcr = false)
    {
        int tilesAcross = (width + tileWidth - 1) / tileWidth;
        int tilesDown = (height + tileLength - 1) / tileLength;
        if ((long)tilesAcross * tilesDown != tileOffsets.Length) return false;

        long tileSize = (long)tileWidth * tileLength * spp * bytesPerSample;
        if (tileSize > int.MaxValue) return false;
        var tile = new byte[tileSize];
        long imageRowStride = (long)width * spp * bytesPerSample;
        long tileRowStride = (long)tileWidth * spp * bytesPerSample;

        for (int ty = 0; ty < tilesDown; ty++)
        {
            for (int tx = 0; tx < tilesAcross; tx++)
            {
                int tileIdx = ty * tilesAcross + tx;
                long off = tileOffsets[tileIdx];
                long enc = tileByteCounts[tileIdx];
                if (off < 0 || enc < 0 || off + enc > bytes.Length) return false;

                if (!Decompress(bytes, (int)off, (int)enc, tile, 0, (int)tileSize, compression,
                    jpegTables: jpegTables, jpegWidth: tileWidth, jpegHeight: tileLength,
                    jpegRowStride: (int)tileRowStride, jpegYcbcr: jpegYcbcr))
                    return false;

                if (predictor == 3) ApplyFloatUnpredictor(tile, tileWidth, tileLength, spp);
                if (!le)
                {
                    if (bps == 16) ByteSwap16InPlace(tile);
                    else if (bps == 32) ByteSwap32InPlace(tile);
                }
                if (predictor == 2) ApplyHorizontalUnpredictor(tile, tileWidth, tileLength, spp, bps);

                int copyWidth = Math.Min(tileWidth, width - tx * tileWidth);
                int copyHeight = Math.Min(tileLength, height - ty * tileLength);
                long copyRowBytes = (long)copyWidth * spp * bytesPerSample;
                for (int y = 0; y < copyHeight; y++)
                {
                    long imgY = (long)ty * tileLength + y;
                    long imgX = (long)tx * tileWidth;
                    long dstOff = imgY * imageRowStride + imgX * spp * bytesPerSample;
                    long srcOff = (long)y * tileRowStride;
                    Buffer.BlockCopy(tile, (int)srcOff, raw, (int)dstOff, (int)copyRowBytes);
                }
            }
        }
        return true;
    }

    /// <summary>Dispatch decompression by TIFF Compression tag.</summary>
    private static bool Decompress(byte[] src, int srcOff, int srcLen,
        byte[] dst, int dstOff, int expected, int compression,
        byte[]? jpegTables = null,
        int jpegWidth = 0, int jpegHeight = 0, int jpegRowStride = 0,
        bool jpegYcbcr = false) => compression switch
    {
        1 => CopyRaw(src, srcOff, srcLen, dst, dstOff, expected),
        5 => DecompressLzw(src, srcOff, srcLen, dst, dstOff, expected),
        7 => DecompressJpeg(src, srcOff, srcLen, dst, dstOff, jpegTables ?? Array.Empty<byte>(),
            jpegWidth, jpegHeight, jpegRowStride, jpegYcbcr),
        32773 => DecompressPackBits(src, srcOff, srcLen, dst, dstOff, expected),
        8 or 32946 => DecompressDeflate(src, srcOff, srcLen, dst, dstOff, expected),
        _ => false,
    };

    /// <summary>
    /// Decompress a JPEG-compressed TIFF strip / tile. If the strip data
    /// already starts with SOI it's treated as a self-contained JPEG;
    /// otherwise <paramref name="jpegTables"/> (tag 347, the shared
    /// markers wrapped in their own SOI/EOI envelope) is spliced in
    /// before the strip data so the combined stream is a complete JPEG.
    /// </summary>
    private static bool DecompressJpeg(byte[] src, int srcOff, int srcLen,
        byte[] dst, int dstOff, byte[] jpegTables,
        int width, int height, int rowStride, bool ycbcr)
    {
        if (width <= 0 || height <= 0 || rowStride <= 0) return false;
        byte[] jpeg = BuildJpegStream(src, srcOff, srcLen, jpegTables);
        // For TIFF photometric=6 (YCbCr) the JPEG components ARE Y/Cb/Cr
        // and need conversion. For photometric=2 (RGB), the JPEG was
        // encoded as direct R/G/B with no internal YCbCr transform; the
        // decoder's heuristic would otherwise mis-detect 3-component
        // JPEGs as YCbCr and corrupt the data. Pass that decision in
        // explicitly via forceRgbPassthrough.
        return VipsJpegLoader.DecodeJpegToBuffer(jpeg, dst, dstOff, rowStride, width, height,
            out _, forceRgbPassthrough: !ycbcr);
    }

    /// <summary>
    /// Combine TIFF JPEGTables (tag 347) with a strip's JPEG data into a
    /// single self-sufficient JPEG bytestream. Strategy: if the strip
    /// already opens with SOI, keep the strip; if not, wrap the strip in
    /// the tables' envelope by inserting tables' interior between the
    /// strip's start and its SOS.
    /// </summary>
    private static byte[] BuildJpegStream(byte[] src, int srcOff, int srcLen, byte[] jpegTables)
    {
        bool stripHasSoi = srcLen >= 2 && src[srcOff] == 0xFF && src[srcOff + 1] == 0xD8;
        bool tablesHaveSoi = jpegTables.Length >= 4
            && jpegTables[0] == 0xFF && jpegTables[1] == 0xD8;
        bool tablesHaveEoi = jpegTables.Length >= 4
            && jpegTables[^2] == 0xFF && jpegTables[^1] == 0xD9;

        if (jpegTables.Length == 0)
        {
            // No shared tables — strip must already be self-contained.
            var copy = new byte[srcLen];
            Buffer.BlockCopy(src, srcOff, copy, 0, srcLen);
            return copy;
        }

        // Tables interior = everything between SOI and EOI.
        int tablesStart = tablesHaveSoi ? 2 : 0;
        int tablesEnd = tablesHaveEoi ? jpegTables.Length - 2 : jpegTables.Length;
        int interiorLen = Math.Max(0, tablesEnd - tablesStart);

        if (stripHasSoi)
        {
            // Splice tables interior right after strip's SOI.
            var combined = new byte[2 + interiorLen + (srcLen - 2)];
            combined[0] = 0xFF; combined[1] = 0xD8;
            Buffer.BlockCopy(jpegTables, tablesStart, combined, 2, interiorLen);
            Buffer.BlockCopy(src, srcOff + 2, combined, 2 + interiorLen, srcLen - 2);
            return combined;
        }

        // Strip lacks SOI — synthesize one, then tables interior, then strip data.
        var combinedNoSoi = new byte[2 + interiorLen + srcLen];
        combinedNoSoi[0] = 0xFF; combinedNoSoi[1] = 0xD8;
        Buffer.BlockCopy(jpegTables, tablesStart, combinedNoSoi, 2, interiorLen);
        Buffer.BlockCopy(src, srcOff, combinedNoSoi, 2 + interiorLen, srcLen);
        return combinedNoSoi;
    }

    /// <summary>
    /// Reverse the TIFF "floating-point predictor" (Predictor=3, Adobe
    /// Technote 3). Per row: horizontal byte accumulate (cancels the
    /// encoder's byte-by-byte differencing), then de-shuffle the
    /// byte-significance grouping. Bytes operate on the stored byte
    /// order — caller byte-swaps to host LE afterwards if needed.
    /// </summary>
    private static void ApplyFloatUnpredictor(byte[] buf, int width, int height, int spp)
    {
        int samplesPerRow = width * spp;
        int rowBytes = samplesPerRow * 4;
        var tmp = new byte[rowBytes];

        for (int y = 0; y < height; y++)
        {
            int rowOff = y * rowBytes;

            // Step 1: horizontal byte accumulator across the entire row,
            // ignoring sample boundaries (the predictor operates on
            // already-shuffled bytes).
            for (int i = 1; i < rowBytes; i++)
                buf[rowOff + i] = (byte)(buf[rowOff + i] + buf[rowOff + i - 1]);

            // Step 2: byte de-shuffle. Stored layout per row is byte-major:
            //   [b0_0, b0_1, ..., b0_{S-1}, b1_0, b1_1, ..., b3_{S-1}]
            // Natural layout is sample-major:
            //   [s0_b0, s0_b1, s0_b2, s0_b3, s1_b0, ...]
            // where S = samplesPerRow.
            Buffer.BlockCopy(buf, rowOff, tmp, 0, rowBytes);
            for (int i = 0; i < samplesPerRow; i++)
            {
                buf[rowOff + i * 4 + 0] = tmp[0 * samplesPerRow + i];
                buf[rowOff + i * 4 + 1] = tmp[1 * samplesPerRow + i];
                buf[rowOff + i * 4 + 2] = tmp[2 * samplesPerRow + i];
                buf[rowOff + i * 4 + 3] = tmp[3 * samplesPerRow + i];
            }
        }
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

    private static void ByteSwap32InPlace(byte[] buf)
    {
        for (int i = 0; i + 3 < buf.Length; i += 4)
        {
            (buf[i], buf[i + 3]) = (buf[i + 3], buf[i]);
            (buf[i + 1], buf[i + 2]) = (buf[i + 2], buf[i + 1]);
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
        1 or 2 or 6 or 7 => 1,        // BYTE / ASCII / SBYTE / UNDEFINED
        3 or 8 => 2,                  // SHORT / SSHORT
        4 or 9 or 11 => 4,            // LONG / SLONG / FLOAT
        5 or 10 or 12 => 8,           // RATIONAL / SRATIONAL / DOUBLE
        16 or 17 or 18 => 8,          // LONG8 / SLONG8 / IFD8 (BigTIFF)
        _ => 0,
    };

    private static long ReadOne(byte[] buf, int entryOffset, ushort type, bool le, ulong count, bool bigTiff)
    {
        if (count != 1) return -1;
        int valOffset = entryOffset + (bigTiff ? 12 : 8);
        return type switch
        {
            1 or 7 => buf[valOffset],
            3 => ReadU16(buf, valOffset, le),
            4 => ReadU32(buf, valOffset, le),
            16 => (long)ReadU64(buf, valOffset, le),
            _ => -1,
        };
    }

    /// <summary>
    /// Read a tag's value as raw bytes (UNDEFINED type — TIFF type 7).
    /// JPEGTables (tag 347) is the canonical use; tag value is a JPEG
    /// markers blob whose count is the byte length.
    /// </summary>
    private static byte[]? ReadBytes(byte[] buf, int entryOffset, ushort type, bool le, ulong count, bool bigTiff)
    {
        if (type != 7 && type != 1) return null;
        int inlineMax = bigTiff ? 8 : 4;
        int dataOffset;
        if ((long)count <= inlineMax)
        {
            dataOffset = entryOffset + (bigTiff ? 12 : 8);
        }
        else
        {
            ulong off = bigTiff
                ? ReadU64(buf, entryOffset + 12, le)
                : ReadU32(buf, entryOffset + 8, le);
            if (off + count > (ulong)buf.Length) return null;
            dataOffset = (int)off;
        }
        var bytes = new byte[count];
        Buffer.BlockCopy(buf, dataOffset, bytes, 0, (int)count);
        return bytes;
    }

    private static long[]? ReadAll(byte[] buf, int entryOffset, ushort type, bool le, ulong count, bool bigTiff)
    {
        int sz = TypeSize(type);
        if (sz == 0) return null;
        long total = checked((long)count * sz);
        int inlineMax = bigTiff ? 8 : 4;
        int dataOffset;
        if (total <= inlineMax)
        {
            dataOffset = entryOffset + (bigTiff ? 12 : 8);
        }
        else
        {
            ulong off = bigTiff
                ? ReadU64(buf, entryOffset + 12, le)
                : ReadU32(buf, entryOffset + 8, le);
            if (off + (ulong)total > (ulong)buf.Length) return null;
            dataOffset = (int)off;
        }
        var values = new long[count];
        for (ulong i = 0; i < count; i++)
        {
            int p = dataOffset + (int)i * sz;
            values[i] = type switch
            {
                1 or 7 => buf[p],
                3 => ReadU16(buf, p, le),
                4 => ReadU32(buf, p, le),
                16 => (long)ReadU64(buf, p, le),
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

    private static ulong ReadU64(byte[] buf, int offset, bool le)
    {
        if (le)
        {
            ulong lo = ReadU32(buf, offset, true);
            ulong hi = ReadU32(buf, offset + 4, true);
            return lo | (hi << 32);
        }
        else
        {
            ulong hi = ReadU32(buf, offset, false);
            ulong lo = ReadU32(buf, offset + 4, false);
            return (hi << 32) | lo;
        }
    }
}
