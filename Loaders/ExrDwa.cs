using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace CosmoImage.Loaders;

/// <summary>
/// OpenEXR DWAA (compression=8, 32 lines/block) and DWAB
/// (compression=9, 256 lines/block) — DreamWorks Animation Lossy
/// Wavelet, the still-image-DCT compressor used in production VFX
/// pipelines. Both modes share the same on-disk layout; only the
/// chunk height differs.
///
/// <para>This pass implements the foundational subset:</para>
/// <list type="bullet">
///   <item>The 88-byte counter header (11 × uint64 LE).</item>
///   <item>The optional VERSION≥2 channel-rule table — read past it
///         using the chunk-size accounting (table size = chunk
///         payload - 88 - sum of stream sizes); no per-rule
///         interpretation is needed for UNKNOWN-only files.</item>
///   <item>The UNKNOWN sub-stream (raw zlib of XDR-planar samples)
///         for any channel name that doesn't match RGB / Y / BY /
///         RY / A. Covers depth, mask, ID, and arbitrary VFX
///         channels — common DWA payloads in practice.</item>
/// </list>
///
/// <para>The lossy DCT path (RGB) and the RLE path (alpha) are
/// follow-up rounds; this method returns false when AC / DC / RLE
/// counts are non-zero so the caller falls back.</para>
/// </summary>
internal static class ExrDwa
{
    private const int CounterHeaderBytes = 88;

    /// <summary>
    /// Decompress one DWA scanline block into the per-row-per-channel-
    /// per-pixel layout the EXR demux expects. Returns false (so the
    /// caller falls back) when the block uses any compression scheme
    /// not yet implemented (LOSSY_DCT or RLE).
    /// </summary>
    public static bool Decompress(byte[] src, int srcOff, int srcLen,
        byte[] dst, int rows, IReadOnlyList<int> channelByteWidths, int width)
    {
        if (srcLen < CounterHeaderBytes) return false;

        // 11 uint64 LE counters per `enum DataSizesSingle`.
        long version            = ReadI64(src, srcOff +  0);
        long unknownUncompSize  = ReadI64(src, srcOff +  8);
        long unknownCompSize    = ReadI64(src, srcOff + 16);
        long acCompSize         = ReadI64(src, srcOff + 24);
        long dcCompSize         = ReadI64(src, srcOff + 32);
        long rleCompSize        = ReadI64(src, srcOff + 40);
        // [48, 56, 64, 72] = RLE_UNCOMPRESSED_SIZE / RLE_RAW_SIZE /
        // AC_UNCOMPRESSED_COUNT / DC_UNCOMPRESSED_COUNT — only used by
        // the lossy DCT and RLE paths, not yet implemented here.
        // [80] = AC_COMPRESSION (0 = static Huffman, 1 = deflate).
        if (version < 0 || version > 2) return false;

        // Bail when any non-UNKNOWN sub-stream is present.
        if (acCompSize > 0 || dcCompSize > 0 || rleCompSize > 0) return false;

        // Channel-rule table size = remaining payload minus the four
        // sub-stream sizes. For UNKNOWN-only data only one of those is
        // non-zero (the unknown stream); others are 0.
        long sumStreams = unknownCompSize + acCompSize + dcCompSize + rleCompSize;
        long ruleTableSize = srcLen - CounterHeaderBytes - sumStreams;
        if (ruleTableSize < 0) return false;

        int unknownStart = srcOff + CounterHeaderBytes + (int)ruleTableSize;
        if (unknownStart + (int)unknownCompSize > srcOff + srcLen) return false;

        // Inflate the unknown stream into XDR planar layout: [all of
        // channel 0 in scanline order][all of channel 1][...]. Each
        // sample is bytesPerSample bytes, big-endian.
        int totalChannels = channelByteWidths.Count;
        int totalPlanarBytes = 0;
        foreach (int bw in channelByteWidths) totalPlanarBytes += bw * width * rows;
        if (totalPlanarBytes != unknownUncompSize) return false;

        var inflated = new byte[totalPlanarBytes];
        if (!ZlibInflate(src, unknownStart, (int)unknownCompSize, inflated, totalPlanarBytes))
            return false;

        // Planar→chunky demux. Inflated layout is [all of channel 0
        // samples in scanline order][all of channel 1][...]. dst layout
        // is per-row-per-channel-per-pixel. EXR file byte order is LE
        // (matches host on x86), so per-sample bytes copy as-is.
        long rowStride = 0;
        foreach (int bw in channelByteWidths) rowStride += bw * width;

        int sp = 0;
        long channelOffInRow = 0;
        foreach (int bw in channelByteWidths)
        {
            for (int r = 0; r < rows; r++)
            {
                long dstRowBase = r * rowStride + channelOffInRow;
                int rowBytes = width * bw;
                Buffer.BlockCopy(inflated, sp, dst, (int)dstRowBase, rowBytes);
                sp += rowBytes;
            }
            channelOffInRow += bw * width;
        }
        return true;
    }

    private static long ReadI64(byte[] src, int off)
        => BinaryPrimitives.ReadInt64LittleEndian(src.AsSpan(off, 8));

    private static bool ZlibInflate(byte[] src, int srcOff, int srcLen, byte[] dst, int expected)
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
        catch { return false; }
    }
}
