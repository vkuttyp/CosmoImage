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
/// version + attribute list), then decodes scanline or tiled pixel
/// data into a <see cref="VipsImage"/>.
///
/// <para>Coverage:</para>
/// <list type="bullet">
///   <item>Single-part and multi-part files (multi-part returns part 0)</item>
///   <item>Scanline and tiled organisations (ONE_LEVEL / MIPMAP /
///         RIPMAP — sub-levels are walked past; we expose level 0)</item>
///   <item>Compressors: NO_COMPRESSION, RLE, ZIPS, ZIP, PIZ, PXR24,
///         B44, B44A. DWAA / DWAB still fall through</item>
///   <item>Pixel types HALF, FLOAT, UINT — output band format is
///         <see cref="VipsBandFormat.Float"/> for HALF/FLOAT and
///         <see cref="VipsBandFormat.UInt"/> for UINT</item>
///   <item>RGB[A] / Y / arbitrary 1–4-channel sets in alphabetical order</item>
/// </list>
///
/// <para>Returns <c>null</c> for unsupported configurations
/// (deep parts, DWA compression, &gt;4 channels) so the caller can
/// fall back to Magick.</para>
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
        if (nonImage) return null;

        int p = 8;
        // Parse the header section: one header for single-part, multiple
        // (separated by null bytes, terminated by an extra null) for
        // multi-part. We keep all headers so we can sum chunkCounts past
        // part 0 later — needed only when we expose more than the first
        // part, but cheap to record now.
        var allHeaders = new List<ExrHeader>();
        while (true)
        {
            var hdr = new ExrHeader();
            if (!ParseAttributes(bytes, ref p, longNames, hdr)) return null;
            if (!hdr.IsValid()) return null;
            allHeaders.Add(hdr);
            if (!multiPart) break;
            if (p >= bytes.Length) return null;
            // Multi-part files terminate the header section with an extra
            // null byte after the last header's own terminator.
            if (bytes[p] == 0) { p++; break; }
        }

        // First-part-only semantics — match the MIPMAP level-0 punt. Rare
        // multi-part files where part 0 is a deep image fall back to
        // null (caller can use Magick).
        var header = allHeaders[0];
        if (multiPart)
        {
            if (header.Type != "scanlineimage" && header.Type != "tiledimage") return null;
            // The version word's tiled bit is single-part-only; for multi-part
            // we trust the part's "type" attribute.
            tiled = header.Type == "tiledimage";
        }

        // Supported compressors: NO_COMPRESSION, RLE, ZIPS, ZIP, PIZ,
        // PXR24, B44, B44A, DWAA, DWAB. PIZ / B44 / B44A / DWAA all
        // use 32 scanlines per block; DWAB extends to 256 lines (only
        // structural difference between DWAA and DWAB).
        int scanlinesPerBlock = header.Compression switch
        {
            0 or 1 or 2 => 1,
            3 or 5 => 16,
            4 or 6 or 7 or 8 => 32,
            9 => 256,
            _ => -1,
        };
        if (scanlinesPerBlock < 0) return null;
        if (header.LineOrder != 0 && header.LineOrder != 1) return null;

        // Determine output bands based on which channels are present.
        // EXR channels are stored alphabetically inside the file; we
        // canonicalise to RGBA on output.
        int[]? channelOrder = ResolveChannelOrder(header.Channels, out int outBands);
        if (channelOrder == null) return null;
        // Selected channels must agree on pixel type (no mixing HALF /
        // FLOAT / UINT in the same image; the EXR spec allows it but
        // it's rare and the API surface for "different formats per
        // band" doesn't exist in VipsImage).
        int selectedPixelType = header.Channels[channelOrder[0]].PixelType;
        foreach (int idx in channelOrder)
            if (header.Channels[idx].PixelType != selectedPixelType) return null;
        if (selectedPixelType != PixelTypeHalf
            && selectedPixelType != PixelTypeFloat
            && selectedPixelType != PixelTypeUint)
            return null;
        VipsBandFormat outBandFormat = selectedPixelType == PixelTypeUint
            ? VipsBandFormat.UInt
            : VipsBandFormat.Float;

        int width = header.DataWindow.Width;
        int height = header.DataWindow.Height;
        if (width <= 0 || height <= 0) return null;

        if (tiled)
        {
            if (header.Tiles == null) return null;
            int levelMode = header.Tiles.Value.Mode & 0x0F;
            int roundingMode = (header.Tiles.Value.Mode >> 4) & 0x0F;
            // ONE_LEVEL (=0), MIPMAP_LEVELS (=1), RIPMAP_LEVELS (=2).
            // For MIPMAP/RIPMAP we expose level 0 (full resolution) only —
            // sub-levels are walked past in the offset table for bounds
            // validation but their tile entries are skipped.
            if (levelMode != 0 && levelMode != 1 && levelMode != 2) return null;
            if (roundingMode != 0 && roundingMode != 1) return null;
            return DecodeTiled(bytes, p, header, channelOrder, width, height, outBands,
                levelMode, roundingMode, outBandFormat, multiPart);
        }

        // Scanline offset table: one uint64 per block. For multi-part the
        // chunkCount attribute is authoritative; for single-part we
        // derive it from dimensions + compression.
        int blockCount = multiPart && header.ChunkCount > 0
            ? header.ChunkCount
            : (height + scanlinesPerBlock - 1) / scanlinesPerBlock;
        int offsetTableStart = p;
        long need = (long)offsetTableStart + 8L * blockCount;
        if (need > bytes.Length) return null;

        // 4 bytes per sample for all supported output formats (Float / UInt).
        var pixels = new byte[(long)width * height * outBands * 4];

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

        // Multi-part chunks carry a 4-byte LE part-number prefix before
        // the regular chunk header; we accept only chunks belonging to
        // part 0 and skip the prefix.
        int chunkHeaderOff = multiPart ? 4 : 0;

        for (int b = 0; b < blockCount; b++)
        {
            long off = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offsetTableStart + b * 8, 8));
            if (off < 0 || off + 8 + chunkHeaderOff > bytes.Length) return null;
            if (multiPart)
            {
                int partNumber = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan((int)off, 4));
                if (partNumber != 0) return null;
            }
            int yCoord = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan((int)off + chunkHeaderOff, 4));
            int dataSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan((int)off + chunkHeaderOff + 4, 4));
            int blockDataOff = (int)off + chunkHeaderOff + 8;
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
            BandFormat = outBandFormat,
            Interpretation = outBands == 1 ? VipsInterpretation.BW : VipsInterpretation.RGB,
            Coding = VipsCoding.None,
            XRes = 1.0,
            YRes = 1.0,
            PixelsLazy = new Lazy<byte[]>(() => pixels),
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
        int srcRowWidth, byte[] dstPixels, long dstRowBaseBytes, int outBands)
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
                // HALF promotes to Float — convert and write 4 bytes.
                for (int x = 0; x < srcRowWidth; x++)
                {
                    ushort half = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(co + x * 2, 2));
                    int bits = BitConverter.SingleToInt32Bits(HalfToFloat(half));
                    long dst = dstRowBaseBytes + ((long)x * outBands + outCh) * 4;
                    BinaryPrimitives.WriteInt32LittleEndian(dstPixels.AsSpan((int)dst, 4), bits);
                }
            }
            else
            {
                // FLOAT and UINT are already 4 bytes / sample, host LE — copy as-is.
                for (int x = 0; x < srcRowWidth; x++)
                {
                    long dst = dstRowBaseBytes + ((long)x * outBands + outCh) * 4;
                    Buffer.BlockCopy(bytes, co + x * 4, dstPixels, (int)dst, 4);
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
        int width, byte[] dstPixels, int outY, int outBands)
    {
        long dstRowBaseBytes = (long)outY * width * outBands * 4;
        DemuxRegionRow(bytes, srcOff, fileChannels, channelOrder, width, dstPixels, dstRowBaseBytes, outBands);
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
        // RGB[A] is canonicalised to band order R,G,B[,A] — even when the
        // file carries extra channels (Z, mask, etc.), pick just the colour
        // channels for the output.
        if (r.HasValue && g.HasValue && b.HasValue)
        {
            if (a.HasValue) { outBands = 4; return new[] { r.Value, g.Value, b.Value, a.Value }; }
            outBands = 3; return new[] { r.Value, g.Value, b.Value };
        }
        if (y.HasValue)
        {
            outBands = 1; return new[] { y.Value };
        }
        // Generic fallback for non-RGB / non-Y channel names — pass them
        // through in the file's alphabetical order. Covers depth (Z),
        // motion vectors (U/V), ID, mask, and arbitrary VFX channel
        // names that previously fell back to Magick. Capped at 4 bands
        // because the rest of the pipeline assumes 1..4 bands.
        if (channels.Count >= 1 && channels.Count <= 4)
        {
            outBands = channels.Count;
            var order = new int[channels.Count];
            for (int i = 0; i < channels.Count; i++) order[i] = i;
            return order;
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
        ExrHeader header, int[] channelOrder, int width, int height, int outBands,
        int levelMode, int roundingMode, VipsBandFormat outBandFormat, bool multiPart)
    {
        int tileW = header.Tiles!.Value.XSize;
        int tileH = header.Tiles!.Value.YSize;
        if (tileW <= 0 || tileH <= 0) return null;

        int tilesX = (width + tileW - 1) / tileW;
        int tilesY = (height + tileH - 1) / tileH;
        long numLevel0Tiles = (long)tilesX * tilesY;

        // For multi-part the chunk count is authoritative (file may have
        // many parts and the compressed offset table is concatenated
        // across them). For single-part, the existing per-level
        // computation tells us how big the offset table is.
        long totalTileEntries = multiPart && header.ChunkCount > 0
            ? header.ChunkCount
            : ComputeOffsetTableEntries(levelMode, roundingMode, width, height, tileW, tileH);
        long need = (long)offsetTableStart + 8L * totalTileEntries;
        if (need > bytes.Length) return null;

        // Multi-part chunks have a 4-byte LE part-number prefix.
        int chunkHeaderOff = multiPart ? 4 : 0;

        // Tile data is stored exactly like a scanline block but with rows
        // sized to the tile width. fileTileRowBytes mirrors fileRowBytes
        // for the smaller width.
        long fileTileRowBytes = 0;
        foreach (var c in header.Channels)
            fileTileRowBytes += BytesPerChannelSample(c.PixelType) * tileW;
        long maxTileBytes = fileTileRowBytes * tileH;
        byte[]? tileScratch = header.Compression != 0 ? new byte[maxTileBytes] : null;

        // 4 bytes per sample for all supported output formats (Float / UInt).
        var pixels = new byte[(long)width * height * outBands * 4];

        for (long t = 0; t < numLevel0Tiles; t++)
        {
            long off = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offsetTableStart + (int)t * 8, 8));
            if (off < 0 || off + 20 + chunkHeaderOff > bytes.Length) return null;
            if (multiPart)
            {
                int partNumber = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan((int)off, 4));
                if (partNumber != 0) return null;
            }
            int chunkOff = (int)off + chunkHeaderOff;
            int tileX = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(chunkOff, 4));
            int tileY = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(chunkOff + 4, 4));
            int levelX = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(chunkOff + 8, 4));
            int levelY = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(chunkOff + 12, 4));
            // The first numLevel0Tiles entries are always level (0,0) in
            // both MIPMAP (level k offsets follow level k-1) and RIPMAP
            // (lx outer? ly outer? — either way (0,0) is first).
            if (levelX != 0 || levelY != 0) return null;
            int dataSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(chunkOff + 16, 4));
            int tileDataOff = chunkOff + 20;
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
                long dstRowBaseBytes = ((long)outY * width + imgX0) * outBands * 4;
                DemuxRegionRow(tileSource, rowSrcOff, header.Channels, channelOrder,
                    colsInTile, pixels, dstRowBaseBytes, outBands);
            }
        }

        return new VipsImage
        {
            Width = width,
            Height = height,
            Bands = outBands,
            BandFormat = outBandFormat,
            Interpretation = outBands == 1 ? VipsInterpretation.BW : VipsInterpretation.RGB,
            Coding = VipsCoding.None,
            XRes = 1.0,
            YRes = 1.0,
            PixelsLazy = new Lazy<byte[]>(() => pixels),
        };
    }

    private static int BytesPerPixelAllChannels(List<ExrChannel> channels)
    {
        int total = 0;
        foreach (var c in channels) total += BytesPerChannelSample(c.PixelType);
        return total;
    }

    /// <summary>
    /// Total entries in a tiled file's offset table for the given level
    /// mode and rounding. ONE_LEVEL = full-image tile count; MIPMAP =
    /// sum across square levels; RIPMAP = sum across (lx, ly) cross
    /// product. Used only for bounds-checking the table — the decoder
    /// itself only consumes level-0 entries.
    /// </summary>
    private static long ComputeOffsetTableEntries(int levelMode, int roundingMode,
        int width, int height, int tileW, int tileH)
    {
        if (levelMode == 0)
        {
            long tx = (width + tileW - 1) / tileW;
            long ty = (height + tileH - 1) / tileH;
            return tx * ty;
        }
        if (levelMode == 1)
        {
            int n = Math.Max(width, height);
            int numLevels = LogN(n, roundingMode) + 1;
            long total = 0;
            for (int l = 0; l < numLevels; l++)
            {
                int wL = LevelSize(width, l, roundingMode);
                int hL = LevelSize(height, l, roundingMode);
                long tx = (wL + tileW - 1) / tileW;
                long ty = (hL + tileH - 1) / tileH;
                total += tx * ty;
            }
            return total;
        }
        // RIPMAP_LEVELS
        int numXLevels = LogN(width, roundingMode) + 1;
        int numYLevels = LogN(height, roundingMode) + 1;
        long totalR = 0;
        for (int ly = 0; ly < numYLevels; ly++)
        {
            int hL = LevelSize(height, ly, roundingMode);
            for (int lx = 0; lx < numXLevels; lx++)
            {
                int wL = LevelSize(width, lx, roundingMode);
                long tx = (wL + tileW - 1) / tileW;
                long ty = (hL + tileH - 1) / tileH;
                totalR += tx * ty;
            }
        }
        return totalR;
    }

    private static int LogN(int x, int roundingMode)
    {
        // ROUND_DOWN = 0 → floor(log2(x)); ROUND_UP = 1 → ceil(log2(x)).
        if (x <= 1) return 0;
        int floor = 0; int v = x;
        while (v > 1) { floor++; v >>= 1; }
        if (roundingMode == 0) return floor;
        return ((1 << floor) == x) ? floor : floor + 1;
    }

    private static int LevelSize(int n, int level, int roundingMode)
    {
        int v = roundingMode == 0
            ? n >> level
            : (n + (1 << level) - 1) >> level;
        return Math.Max(v, 1);
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
            return DecompressPxr24Geom(src, srcOff, srcLen, dst, rows, channels, width);
        }
        if (compression == 4)
        {
            // ExrPiz.Decompress handles PIZ end-to-end: bitmap LUT +
            // canonical Huffman + 2D inverse wavelet + per-channel demux.
            // Channel byte widths are per-pixel sample sizes the
            // wavelet works in (each ushort is one wavelet sample).
            var channelByteWidths = new int[channels.Count];
            for (int i = 0; i < channels.Count; i++)
                channelByteWidths[i] = BytesPerChannelSample(channels[i].PixelType);
            return ExrPiz.Decompress(src, srcOff, srcLen, dst, rows, channelByteWidths, width);
        }
        if (compression == 6 || compression == 7)
        {
            // B44 / B44A: 4×4-block DPCM compressor for HALF channels.
            // FLOAT / UINT channels pass through raw within the block.
            // Both compressor codes share the same decode path; only
            // the encoder differs (B44A emits a 3-byte short form for
            // uniform blocks, which the decoder auto-detects).
            var b44Channels = new ExrB44ChannelInfo[channels.Count];
            for (int i = 0; i < channels.Count; i++)
                b44Channels[i] = new ExrB44ChannelInfo(
                    BytesPerChannelSample(channels[i].PixelType),
                    channels[i].PLinear != 0);
            return ExrB44.Decompress(src, srcOff, srcLen, dst, rows, b44Channels, width);
        }
        if (compression == 8 || compression == 9)
        {
            // DWAA / DWAB: handles UNKNOWN, RLE, and single-channel
            // LOSSY_DCT paths. Multi-channel CSC and complex routing
            // need the channel-rule table parsing (a future round).
            var dwaWidths = new int[channels.Count];
            var dwaPLinear = new bool[channels.Count];
            for (int i = 0; i < channels.Count; i++)
            {
                dwaWidths[i] = BytesPerChannelSample(channels[i].PixelType);
                dwaPLinear[i] = channels[i].PLinear != 0;
            }
            return ExrDwa.Decompress(src, srcOff, srcLen, dst, rows, dwaWidths, width, dwaPLinear);
        }

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
    /// PXR24 (compression=5) decompress. The encoded form is zlib over
    /// per-channel-per-row byte planes (high-to-low byte order within
    /// each pixel) with a per-pixel delta predictor. Plane count
    /// depends on pixel type: HALF = 2, FLOAT = 3 (top 24 bits of the
    /// float; bottom mantissa byte is dropped — that's where PXR24
    /// gets its lossy "24-bit float"), UINT = 4. Output layout is
    /// always 4 bytes per UINT/FLOAT sample and 2 bytes per HALF
    /// sample, host little-endian.
    /// </summary>
    private static bool DecompressPxr24Geom(byte[] src, int srcOff, int srcLen,
        byte[] dst, int rows, List<ExrChannel> channels, int width)
    {
        // Inflated size: per row, sum over channels of bytesPerPlane*width
        // where bytesPerPlane is 2/3/4 for HALF/FLOAT/UINT.
        int rowInflated = 0;
        foreach (var ch in channels)
        {
            int bp = ch.PixelType switch
            {
                PixelTypeHalf => 2,
                PixelTypeFloat => 3,
                PixelTypeUint => 4,
                _ => 0,
            };
            if (bp == 0) return false;
            rowInflated += bp * width;
        }
        int totalInflated = rows * rowInflated;
        var inflated = new byte[totalInflated];
        if (!ZlibDecompress(src, srcOff, srcLen, inflated, totalInflated)) return false;

        int sp = 0;
        int dp = 0;
        for (int r = 0; r < rows; r++)
        {
            foreach (var ch in channels)
            {
                switch (ch.PixelType)
                {
                    case PixelTypeHalf:
                    {
                        ushort prev = 0;
                        for (int x = 0; x < width; x++)
                        {
                            byte hi = inflated[sp + x];
                            byte lo = inflated[sp + width + x];
                            ushort delta = (ushort)((hi << 8) | lo);
                            prev = (ushort)(prev + delta);
                            dst[dp + x * 2]     = (byte)prev;
                            dst[dp + x * 2 + 1] = (byte)(prev >> 8);
                        }
                        sp += 2 * width;
                        dp += 2 * width;
                        break;
                    }
                    case PixelTypeFloat:
                    {
                        // 3 byte-planes carry the top 24 bits of each
                        // float (bytes 3, 2, 1 in big-endian order — the
                        // bottom mantissa byte is dropped). Predictor
                        // accumulates over the assembled 24-bit value
                        // with the dropped byte forced to 0.
                        uint prev = 0;
                        for (int x = 0; x < width; x++)
                        {
                            byte b0 = inflated[sp + x];                  // bits 24..31
                            byte b1 = inflated[sp + width + x];          // bits 16..23
                            byte b2 = inflated[sp + 2 * width + x];      // bits 8..15
                            uint delta = ((uint)b0 << 24) | ((uint)b1 << 16) | ((uint)b2 << 8);
                            prev = prev + delta;
                            // Output 4 bytes little-endian (low byte = 0).
                            dst[dp + x * 4]     = 0;
                            dst[dp + x * 4 + 1] = (byte)(prev >> 8);
                            dst[dp + x * 4 + 2] = (byte)(prev >> 16);
                            dst[dp + x * 4 + 3] = (byte)(prev >> 24);
                        }
                        sp += 3 * width;
                        dp += 4 * width;
                        break;
                    }
                    case PixelTypeUint:
                    {
                        uint prev = 0;
                        for (int x = 0; x < width; x++)
                        {
                            byte b0 = inflated[sp + x];                  // bits 24..31
                            byte b1 = inflated[sp + width + x];          // bits 16..23
                            byte b2 = inflated[sp + 2 * width + x];      // bits 8..15
                            byte b3 = inflated[sp + 3 * width + x];      // bits 0..7
                            uint delta = ((uint)b0 << 24) | ((uint)b1 << 16) | ((uint)b2 << 8) | b3;
                            prev = prev + delta;
                            dst[dp + x * 4]     = (byte)prev;
                            dst[dp + x * 4 + 1] = (byte)(prev >> 8);
                            dst[dp + x * 4 + 2] = (byte)(prev >> 16);
                            dst[dp + x * 4 + 3] = (byte)(prev >> 24);
                        }
                        sp += 4 * width;
                        dp += 4 * width;
                        break;
                    }
                }
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
        // Multi-part-only attributes. "type" identifies the part's
        // organisation (scanlineimage / tiledimage / deepscanline /
        // deeptile); "chunkCount" tells how many chunks the part has
        // (so we can size offset tables without re-deriving them).
        public string Type { get; set; } = "";
        public int ChunkCount { get; set; } = -1;
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
                case "type":
                    // String values are length-prefixed by the attribute size.
                    hdr.Type = System.Text.Encoding.ASCII.GetString(bytes, valueStart, size);
                    break;
                case "chunkCount":
                    if (size >= 4)
                        hdr.ChunkCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(valueStart, 4));
                    break;
                // Other attributes (screenWindowCenter, screenWindowWidth,
                // name, etc.) are accepted-and-skipped.
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
