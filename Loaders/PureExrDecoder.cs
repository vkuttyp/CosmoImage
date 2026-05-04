using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using CosmoImage.Core;

namespace CosmoImage.Loaders;

/// <summary>
/// Pure-managed OpenEXR decoder. Reads the file header (magic +
/// version + attribute list), then decodes scanline-organised
/// uncompressed pixel data into a <see cref="VipsImage"/>.
///
/// <para>This first pass covers the foundational subset:</para>
/// <list type="bullet">
///   <item>Single-part files only (multi-part is bit 12 of the version word)</item>
///   <item>Scanline organisation (tiled handled by a later round)</item>
///   <item>NO_COMPRESSION (=0); RLE / ZIP / PIZ / PXR24 / B44 / DWA come later</item>
///   <item>HALF (=1) pixel type — converted to <see cref="float"/> on decode</item>
///   <item>Channels named "R" / "G" / "B" / "A" — other channel sets fall through</item>
/// </list>
///
/// <para>Returns <c>null</c> for unsupported configurations so the
/// caller can fall back. Output is <see cref="VipsBandFormat.Float"/>
/// because EXR's HALF pixels promote naturally and downstream ops
/// handle Float; HALF-format storage in the <see cref="VipsImage"/>
/// is a future optimisation.</para>
/// </summary>
internal static class PureExrDecoder
{
    public const int MagicLE = 0x01312F76;

    public static VipsImage? TryDecode(byte[] bytes)
    {
        if (bytes == null || bytes.Length < 12) return null;
        int magic = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0, 4));
        if (magic != MagicLE) return null;
        int version = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4, 4));
        int formatVersion = version & 0xFF;
        if (formatVersion != 2) return null;

        bool tiled = (version & 0x200) != 0;
        bool longNames = (version & 0x400) != 0;
        bool nonImage = (version & 0x800) != 0;
        bool multiPart = (version & 0x1000) != 0;
        if (nonImage || multiPart) return null;  // future rounds

        int p = 8;
        var header = new ExrHeader();
        if (!ParseAttributes(bytes, ref p, longNames, header)) return null;
        if (!header.IsValid()) return null;

        // Supported compressors: NO_COMPRESSION, RLE, ZIPS, ZIP, PXR24.
        // PIZ primitives exist in ExrPiz and are validated in isolation,
        // but the integration against libimf-encoded bitstreams has
        // residual issues (likely in the demux step or in the W16 wavelet
        // wrap path) — leaving PIZ unwired for now.
        int scanlinesPerBlock = header.Compression switch
        {
            0 or 1 or 2 => 1,
            3 or 5 => 16,
            _ => -1,
        };
        if (scanlinesPerBlock < 0) return null;
        if (header.LineOrder != 0 && header.LineOrder != 1) return null;

        // Determine output bands based on which channels are present.
        // EXR channels are stored alphabetically inside the file; we
        // canonicalise to RGBA on output.
        int[]? channelOrder = ResolveChannelOrder(header.Channels, out int outBands);
        if (channelOrder == null) return null;
        // Selected channels must agree on pixel type (no mixing HALF +
        // FLOAT in the same image; could be relaxed later but is rare).
        // Supported: HALF and FLOAT promote to VipsBandFormat.Float;
        // UINT requires its own band format which we don't yet wire.
        int selectedPixelType = header.Channels[channelOrder[0]].PixelType;
        foreach (int idx in channelOrder)
            if (header.Channels[idx].PixelType != selectedPixelType) return null;
        if (selectedPixelType != PixelTypeHalf && selectedPixelType != PixelTypeFloat) return null;

        int width = header.DataWindow.Width;
        int height = header.DataWindow.Height;
        if (width <= 0 || height <= 0) return null;

        if (tiled)
        {
            if (header.Tiles == null) return null;
            // Only ONE_LEVEL mode for now (mode bits 0-3 == 0).
            if ((header.Tiles.Value.Mode & 0x0F) != 0) return null;
            return DecodeTiled(bytes, p, header, channelOrder, width, height, outBands);
        }

        // After attributes (terminated by a single null byte), the
        // scanline offset table follows: one uint64 per BLOCK.
        int blockCount = (height + scanlinesPerBlock - 1) / scanlinesPerBlock;
        int offsetTableStart = p;
        long need = (long)offsetTableStart + 8L * blockCount;
        if (need > bytes.Length) return null;

        var pixels = new float[(long)width * height * outBands];

        // Number of channels in the FILE's scanline (which includes any
        // unselected channels, alphabetically). Each scanline has all of
        // those channels' bytes laid out per-channel-then-per-pixel.
        int allChannels = header.Channels.Count;
        long fileRowBytes = 0;
        foreach (var c in header.Channels) fileRowBytes += BytesPerChannelSample(c.PixelType) * width;

        // Block-level scratch buffer; max possible size is
        // scanlinesPerBlock × fileRowBytes.
        byte[]? blockScratch = header.Compression != 0
            ? new byte[scanlinesPerBlock * fileRowBytes]
            : null;

        for (int b = 0; b < blockCount; b++)
        {
            long off = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offsetTableStart + b * 8, 8));
            if (off < 0 || off + 8 > bytes.Length) return null;
            int yCoord = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan((int)off, 4));
            int dataSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan((int)off + 4, 4));
            int blockDataOff = (int)off + 8;
            if (blockDataOff + dataSize > bytes.Length) return null;
            if (yCoord < header.DataWindow.YMin || yCoord > header.DataWindow.YMax) return null;
            int outY0 = yCoord - header.DataWindow.YMin;

            int rowsInBlock = Math.Min(scanlinesPerBlock, height - outY0);
            int blockBytes = rowsInBlock * (int)fileRowBytes;

            byte[] blockSource;
            int blockSourceOff;
            if (header.Compression == 0)
            {
                if (dataSize < blockBytes) return null;
                blockSource = bytes;
                blockSourceOff = blockDataOff;
            }
            else
            {
                if (!DecompressBlock(header.Compression, bytes, blockDataOff, dataSize,
                    blockScratch!, blockBytes, rowsInBlock, header.Channels, width))
                    return null;
                blockSource = blockScratch!;
                blockSourceOff = 0;
            }

            for (int row = 0; row < rowsInBlock; row++)
            {
                int outY = outY0 + row;
                int rowOff = blockSourceOff + row * (int)fileRowBytes;
                DemuxScanline(blockSource, rowOff, header.Channels, channelOrder, width, pixels, outY, outBands);
            }
        }

        return new VipsImage
        {
            Width = width,
            Height = height,
            Bands = outBands,
            BandFormat = VipsBandFormat.Float,
            Interpretation = outBands == 1 ? VipsInterpretation.BW : VipsInterpretation.RGB,
            Coding = VipsCoding.None,
            XRes = 1.0,
            YRes = 1.0,
            PixelsLazy = new Lazy<byte[]>(() =>
            {
                var dst = new byte[pixels.Length * 4];
                Buffer.BlockCopy(pixels, 0, dst, 0, dst.Length);
                return dst;
            }),
        };
    }

    /// <summary>
    /// Demux one row of a region (scanline or tile-row) from the file's
    /// per-channel layout into the output buffer's chunky layout.
    /// <paramref name="dstRowBase"/> is the starting index in the output
    /// pixel buffer; <paramref name="srcRowWidth"/> is how many pixels
    /// the source row carries (= image width for scanlines, = tile
    /// width for tile rows).
    /// </summary>
    private static void DemuxRegionRow(byte[] bytes, int srcOff,
        List<ExrChannel> fileChannels, int[] channelOrder,
        int srcRowWidth, float[] dstPixels, long dstRowBase, int outBands)
    {
        int[] channelOffsets = new int[fileChannels.Count];
        int cursor = srcOff;
        for (int i = 0; i < fileChannels.Count; i++)
        {
            channelOffsets[i] = cursor;
            cursor += BytesPerChannelSample(fileChannels[i].PixelType) * srcRowWidth;
        }

        for (int outCh = 0; outCh < outBands; outCh++)
        {
            int srcChIdx = channelOrder[outCh];
            int co = channelOffsets[srcChIdx];
            int pixelType = fileChannels[srcChIdx].PixelType;
            if (pixelType == PixelTypeHalf)
            {
                for (int x = 0; x < srcRowWidth; x++)
                {
                    ushort half = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(co + x * 2, 2));
                    dstPixels[dstRowBase + (long)x * outBands + outCh] = HalfToFloat(half);
                }
            }
            else  // PixelTypeFloat
            {
                for (int x = 0; x < srcRowWidth; x++)
                {
                    int bits = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(co + x * 4, 4));
                    dstPixels[dstRowBase + (long)x * outBands + outCh] = BitConverter.Int32BitsToSingle(bits);
                }
            }
        }
    }

    /// <summary>
    /// Scanline-block convenience: demux a row that spans the full image
    /// width into row <paramref name="outY"/> of the output buffer.
    /// </summary>
    private static void DemuxScanline(byte[] bytes, int srcOff,
        List<ExrChannel> fileChannels, int[] channelOrder,
        int width, float[] dstPixels, int outY, int outBands)
    {
        long dstRowBase = (long)outY * width * outBands;
        DemuxRegionRow(bytes, srcOff, fileChannels, channelOrder, width, dstPixels, dstRowBase, outBands);
    }

    /// <summary>
    /// Decide the output band count + the file-channel index for each
    /// output band. Returns null when no recognised channel set is
    /// found.
    /// </summary>
    private static int[]? ResolveChannelOrder(List<ExrChannel> channels, out int outBands)
    {
        outBands = 0;
        // Channels are stored alphabetically in the file. Look up by name.
        int? r = FindChannel(channels, "R");
        int? g = FindChannel(channels, "G");
        int? b = FindChannel(channels, "B");
        int? a = FindChannel(channels, "A");
        int? y = FindChannel(channels, "Y");
        if (r.HasValue && g.HasValue && b.HasValue)
        {
            if (a.HasValue) { outBands = 4; return new[] { r.Value, g.Value, b.Value, a.Value }; }
            outBands = 3; return new[] { r.Value, g.Value, b.Value };
        }
        if (y.HasValue)
        {
            outBands = 1; return new[] { y.Value };
        }
        return null;
    }

    private static int? FindChannel(List<ExrChannel> channels, string name)
    {
        for (int i = 0; i < channels.Count; i++)
            if (channels[i].Name == name) return i;
        return null;
    }

    private static int BytesPerChannelSample(int pixelType) => pixelType switch
    {
        PixelTypeUint => 4,
        PixelTypeHalf => 2,
        PixelTypeFloat => 4,
        _ => 0,
    };

    public const int PixelTypeUint = 0;
    public const int PixelTypeHalf = 1;
    public const int PixelTypeFloat = 2;

    /// <summary>
    /// Decode tiled EXR (single-level, ONE_LEVEL mode). Each tile is one
    /// block in the existing per-block dispatch, but with rows spanning
    /// only the tile width and a 2D placement in the output image.
    /// </summary>
    private static VipsImage? DecodeTiled(byte[] bytes, int offsetTableStart,
        ExrHeader header, int[] channelOrder, int width, int height, int outBands)
    {
        int tileW = header.Tiles!.Value.XSize;
        int tileH = header.Tiles!.Value.YSize;
        if (tileW <= 0 || tileH <= 0) return null;

        int tilesX = (width + tileW - 1) / tileW;
        int tilesY = (height + tileH - 1) / tileH;
        long numTiles = (long)tilesX * tilesY;
        long need = (long)offsetTableStart + 8L * numTiles;
        if (need > bytes.Length) return null;

        // Tile data is stored exactly like a scanline block but with rows
        // sized to the tile width. fileTileRowBytes mirrors fileRowBytes
        // for the smaller width.
        long fileTileRowBytes = 0;
        foreach (var c in header.Channels)
            fileTileRowBytes += BytesPerChannelSample(c.PixelType) * tileW;
        long maxTileBytes = fileTileRowBytes * tileH;
        byte[]? tileScratch = header.Compression != 0 ? new byte[maxTileBytes] : null;

        var pixels = new float[(long)width * height * outBands];

        for (long t = 0; t < numTiles; t++)
        {
            long off = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offsetTableStart + (int)t * 8, 8));
            if (off < 0 || off + 20 > bytes.Length) return null;
            int tileX = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan((int)off, 4));
            int tileY = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan((int)off + 4, 4));
            int levelX = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan((int)off + 8, 4));
            int levelY = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan((int)off + 12, 4));
            if (levelX != 0 || levelY != 0) return null;  // ONE_LEVEL only
            int dataSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan((int)off + 16, 4));
            int tileDataOff = (int)off + 20;
            if (tileDataOff + dataSize > bytes.Length) return null;
            if (tileX < 0 || tileX >= tilesX || tileY < 0 || tileY >= tilesY) return null;

            int rowsInTile = Math.Min(tileH, height - tileY * tileH);
            int colsInTile = Math.Min(tileW, width - tileX * tileW);
            // Tile data on disk is always stored at full tile dimensions
            // even when the tile straddles the data window. We read at
            // tileW × rowsInTile (last column tiles still get full tileW
            // worth of bytes per row, but unused columns are ignored).
            // Actually per spec, tiles at the right/bottom edges DO carry
            // smaller data — the tile occupies (colsInTile × rowsInTile)
            // pixels.
            long tileBytes = (long)colsInTile * BytesPerPixelAllChannels(header.Channels) * rowsInTile;
            // Each row's source-bytes count uses colsInTile (not tileW):
            long tileRowBytes = (long)colsInTile * BytesPerPixelAllChannels(header.Channels);

            byte[] tileSource;
            int tileSourceOff;
            if (header.Compression == 0)
            {
                if (dataSize < tileBytes) return null;
                tileSource = bytes;
                tileSourceOff = tileDataOff;
            }
            else
            {
                if (!DecompressBlock(header.Compression, bytes, tileDataOff, dataSize,
                    tileScratch!, (int)tileBytes, rowsInTile, header.Channels, colsInTile))
                    return null;
                tileSource = tileScratch!;
                tileSourceOff = 0;
            }

            // Demux each tile row into the right slice of the output buffer.
            int imgX0 = tileX * tileW;
            int imgY0 = tileY * tileH;
            for (int row = 0; row < rowsInTile; row++)
            {
                int outY = imgY0 + row;
                int rowSrcOff = tileSourceOff + row * (int)tileRowBytes;
                long dstRowBase = ((long)outY * width + imgX0) * outBands;
                DemuxRegionRow(tileSource, rowSrcOff, header.Channels, channelOrder,
                    colsInTile, pixels, dstRowBase, outBands);
            }
        }

        return new VipsImage
        {
            Width = width,
            Height = height,
            Bands = outBands,
            BandFormat = VipsBandFormat.Float,
            Interpretation = outBands == 1 ? VipsInterpretation.BW : VipsInterpretation.RGB,
            Coding = VipsCoding.None,
            XRes = 1.0,
            YRes = 1.0,
            PixelsLazy = new Lazy<byte[]>(() =>
            {
                var dst = new byte[pixels.Length * 4];
                Buffer.BlockCopy(pixels, 0, dst, 0, dst.Length);
                return dst;
            }),
        };
    }

    private static int BytesPerPixelAllChannels(List<ExrChannel> channels)
    {
        int total = 0;
        foreach (var c in channels) total += BytesPerChannelSample(c.PixelType);
        return total;
    }

    /// <summary>
    /// Decompress one EXR scanline/tile block into <paramref name="dst"/>.
    /// Per-compression dispatch: RLE/ZIPS/ZIP share zlib-or-RLE + reverse
    /// predictor + de-interleave. PXR24 routes through its own per-pixel
    /// delta path which needs the row geometry to reset the accumulator
    /// at channel boundaries.
    /// </summary>
    private static bool DecompressBlock(int compression, byte[] src, int srcOff, int srcLen,
        byte[] dst, int expected, int rows, List<ExrChannel> channels, int width)
    {
        if (srcLen == expected)
        {
            Buffer.BlockCopy(src, srcOff, dst, 0, expected);
            return true;
        }

        if (compression == 5)
        {
            int rowBytes = expected / Math.Max(1, rows);
            return DecompressPxr24Geom(src, srcOff, srcLen, dst, rows, rowBytes, channels, width);
        }
        // PIZ (compression == 4) goes through ExrPiz.Decompress, but the
        // integration is currently disabled at the dispatcher above —
        // resumed once the bitstream/wavelet integration is fixed.

        var scratch = new byte[expected];
        bool ok = compression switch
        {
            1 => RleDecompress(src, srcOff, srcLen, scratch, expected),
            // ZIPS (=2) and ZIP (=3) share the same per-block algorithm —
            // ZIP just bundles 16 scanlines per block instead of 1.
            2 or 3 => ZlibDecompress(src, srcOff, srcLen, scratch, expected),
            _ => false,
        };
        if (!ok) return false;

        // Reverse delta-predictor: cur += prev - 128 (mod 256).
        for (int i = 1; i < expected; i++)
        {
            int v = scratch[i - 1] + scratch[i] - 128;
            scratch[i] = (byte)v;
        }

        // De-interleave: dst[2i] from first half, dst[2i+1] from second half.
        int half = (expected + 1) / 2;
        int t1 = 0, t2 = half;
        for (int i = 0; i < expected; )
        {
            dst[i++] = scratch[t1++];
            if (i < expected) dst[i++] = scratch[t2++];
        }
        return true;
    }

    /// <summary>
    /// PXR24 (compression=5) decompress. The encoded form is zlib-
    /// compressed per-channel-per-row [high_bytes; low_bytes] streams
    /// with a per-pixel 16-bit delta predictor that resets at channel
    /// boundaries within each row. Caller supplies row count and width
    /// so we can walk the inflated buffer with the right stride.
    ///
    /// HALF channels only here; FLOAT (3-byte truncated) and UINT
    /// (4-byte) come in a future round.
    /// </summary>
    private static bool DecompressPxr24Geom(byte[] src, int srcOff, int srcLen,
        byte[] dst, int rows, int rowBytes, List<ExrChannel> channels, int width)
    {
        int total = rows * rowBytes;
        var inflated = new byte[total];
        if (!ZlibDecompress(src, srcOff, srcLen, inflated, total)) return false;

        // Inflated layout: for each row, for each channel, [high_bytes (W), low_bytes (W)].
        // dst layout: for each row, for each channel, [pixel0_lo, pixel0_hi, pixel1_lo, pixel1_hi, ...]
        //   (HALF stored little-endian: low byte first.)
        int sp = 0;
        int dp = 0;
        for (int r = 0; r < rows; r++)
        {
            foreach (var ch in channels)
            {
                if (ch.PixelType != PixelTypeHalf) return false;  // FLOAT/UINT future
                ushort prev = 0;
                for (int x = 0; x < width; x++)
                {
                    byte hi = inflated[sp + x];
                    byte lo = inflated[sp + width + x];
                    ushort delta = (ushort)((hi << 8) | lo);
                    prev = (ushort)(prev + delta);
                    dst[dp + x * 2]     = (byte)prev;          // low byte (host LE)
                    dst[dp + x * 2 + 1] = (byte)(prev >> 8);   // high byte
                }
                sp += 2 * width;
                dp += 2 * width;
            }
        }
        return true;
    }

    /// <summary>
    /// EXR's RLE: signed control byte n in [-127, -1] → next byte
    /// replicated (-n + 1) times; n in [0, 127] → next n+1 bytes literal.
    /// (Unlike PackBits, n = -128 is a valid run-of-129 here.)
    /// </summary>
    private static bool RleDecompress(byte[] src, int srcOff, int srcLen,
        byte[] dst, int expected)
    {
        int sp = srcOff, sEnd = srcOff + srcLen;
        int dp = 0;
        while (sp < sEnd && dp < expected)
        {
            sbyte n = (sbyte)src[sp++];
            if (n < 0)
            {
                int run = -n + 1;
                if (sp >= sEnd || dp + run > expected) return false;
                byte v = src[sp++];
                for (int i = 0; i < run; i++) dst[dp++] = v;
            }
            else
            {
                int run = n + 1;
                if (sp + run > sEnd || dp + run > expected) return false;
                Buffer.BlockCopy(src, sp, dst, dp, run);
                sp += run;
                dp += run;
            }
        }
        return dp == expected;
    }

    private static bool ZlibDecompress(byte[] src, int srcOff, int srcLen,
        byte[] dst, int expected)
    {
        try
        {
            using var ms = new MemoryStream(src, srcOff, srcLen);
            using var z = new ZLibStream(ms, CompressionMode.Decompress);
            int read = 0;
            while (read < expected)
            {
                int n = z.Read(dst, read, expected - read);
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

    /// <summary>Decode a 16-bit IEEE 754 half-precision float to <see cref="float"/>.</summary>
    private static float HalfToFloat(ushort h)
    {
        // .NET 5+ has Half. Use BitConverter to convert.
        return (float)BitConverter.UInt16BitsToHalf(h);
    }

    // ---- Header parsing ----

    private sealed class ExrHeader
    {
        public List<ExrChannel> Channels { get; } = new();
        public int Compression { get; set; } = -1;
        public Box2i DataWindow { get; set; }
        public Box2i DisplayWindow { get; set; }
        public int LineOrder { get; set; } = -1;
        public float PixelAspectRatio { get; set; } = 1.0f;
        public TileDesc? Tiles { get; set; }
        public bool IsValid() => Channels.Count > 0 && Compression >= 0
                                  && DataWindow.Width > 0 && DataWindow.Height > 0
                                  && LineOrder >= 0;
    }

    private struct TileDesc
    {
        public int XSize, YSize;
        public byte Mode;        // bits 0-3: level mode, bits 4-7: rounding mode
    }

    private sealed class ExrChannel
    {
        public string Name { get; init; } = "";
        public int PixelType { get; init; }
        public byte PLinear { get; init; }
        public int XSampling { get; init; } = 1;
        public int YSampling { get; init; } = 1;
    }

    private struct Box2i
    {
        public int XMin, YMin, XMax, YMax;
        public int Width => XMax - XMin + 1;
        public int Height => YMax - YMin + 1;
    }

    /// <summary>
    /// Parse the attribute list. Each attribute is name + type + size +
    /// payload. The list ends with a single null byte.
    /// </summary>
    private static bool ParseAttributes(byte[] bytes, ref int p, bool longNames, ExrHeader hdr)
    {
        int maxNameLen = longNames ? 255 : 31;
        while (p < bytes.Length)
        {
            if (bytes[p] == 0) { p++; return true; }  // end of attributes

            string? name = ReadNullTerminated(bytes, ref p, maxNameLen);
            if (name == null) return false;
            if (p >= bytes.Length) return false;

            string? type = ReadNullTerminated(bytes, ref p, maxNameLen);
            if (type == null) return false;
            if (p + 4 > bytes.Length) return false;

            int size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p, 4));
            p += 4;
            if (size < 0 || p + size > bytes.Length) return false;
            int valueStart = p;
            int valueEnd = p + size;
            p = valueEnd;

            switch (name)
            {
                case "channels":
                    if (!ParseChannels(bytes, valueStart, valueEnd, hdr.Channels)) return false;
                    break;
                case "compression":
                    if (size < 1) return false;
                    hdr.Compression = bytes[valueStart];
                    break;
                case "dataWindow":
                    if (size < 16) return false;
                    hdr.DataWindow = ReadBox2i(bytes, valueStart);
                    break;
                case "displayWindow":
                    if (size < 16) return false;
                    hdr.DisplayWindow = ReadBox2i(bytes, valueStart);
                    break;
                case "lineOrder":
                    if (size < 1) return false;
                    hdr.LineOrder = bytes[valueStart];
                    break;
                case "pixelAspectRatio":
                    if (size >= 4)
                        hdr.PixelAspectRatio = BitConverter.Int32BitsToSingle(
                            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(valueStart, 4)));
                    break;
                case "tiles":
                    if (size < 9) return false;
                    hdr.Tiles = new TileDesc
                    {
                        XSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(valueStart, 4)),
                        YSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(valueStart + 4, 4)),
                        Mode = bytes[valueStart + 8],
                    };
                    break;
                // Other attributes (screenWindowCenter, screenWindowWidth, etc.)
                // are accepted-and-skipped.
            }
        }
        return false;  // hit EOF without seeing the terminating null
    }

    private static bool ParseChannels(byte[] bytes, int start, int end, List<ExrChannel> channels)
    {
        int p = start;
        while (p < end)
        {
            if (bytes[p] == 0) { p++; return true; }
            string? name = ReadNullTerminated(bytes, ref p, 255);
            if (name == null) return false;
            if (p + 16 > end) return false;
            int pixelType = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p, 4));
            byte pLinear = bytes[p + 4];
            int xSampling = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p + 8, 4));
            int ySampling = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p + 12, 4));
            p += 16;
            channels.Add(new ExrChannel
            {
                Name = name, PixelType = pixelType, PLinear = pLinear,
                XSampling = xSampling, YSampling = ySampling,
            });
        }
        return false;
    }

    private static Box2i ReadBox2i(byte[] bytes, int p) => new()
    {
        XMin = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p, 4)),
        YMin = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p + 4, 4)),
        XMax = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p + 8, 4)),
        YMax = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p + 12, 4)),
    };

    private static string? ReadNullTerminated(byte[] bytes, ref int p, int maxLen)
    {
        int start = p;
        while (p < bytes.Length && p - start < maxLen && bytes[p] != 0) p++;
        if (p >= bytes.Length || bytes[p] != 0) return null;
        string s = Encoding.ASCII.GetString(bytes, start, p - start);
        p++;  // skip null
        return s;
    }
}
