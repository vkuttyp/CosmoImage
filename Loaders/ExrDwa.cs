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

        // [48..56] = RLE_UNCOMPRESSED_SIZE / RLE_RAW_SIZE
        long rleUncompSize = ReadI64(src, srcOff + 48);
        long rleRawSize    = ReadI64(src, srcOff + 56);

        // Bail when the lossy DCT path (AC + DC) is present — that's
        // the next round.
        if (acCompSize > 0 || dcCompSize > 0) return false;

        // Channel-rule table size = remaining payload minus the four
        // sub-stream sizes.
        long sumStreams = unknownCompSize + acCompSize + dcCompSize + rleCompSize;
        long ruleTableSize = srcLen - CounterHeaderBytes - sumStreams;
        if (ruleTableSize < 0) return false;

        // Inflate UNKNOWN and RLE streams into separate planar buffers.
        int unknownStart = srcOff + CounterHeaderBytes + (int)ruleTableSize;
        if (unknownStart + (int)unknownCompSize > srcOff + srcLen) return false;

        // Walk past UNKNOWN and AC/DC (both zero here) to find the RLE
        // start. AC/DC are zero so unknownStart + unknownCompSize ==
        // rleStart.
        int rleStart = unknownStart + (int)unknownCompSize
                       + (int)acCompSize + (int)dcCompSize;
        if (rleStart + (int)rleCompSize > srcOff + srcLen) return false;

        // Build the planar layout from both streams. Per the default
        // classifier, channels named A go to RLE, others (R/G/B/Y/...)
        // would go to LOSSY_DCT (rejected above), and everything else
        // goes to UNKNOWN. We don't have access to channel names here,
        // so we infer scheme assignment from which stream's bytes
        // account for that channel's planar slice — the file's
        // sub-stream sizes have to add up consistently.
        var unknownInflated = unknownUncompSize > 0 ? new byte[unknownUncompSize] : Array.Empty<byte>();
        if (unknownUncompSize > 0
            && !ZlibInflate(src, unknownStart, (int)unknownCompSize, unknownInflated, (int)unknownUncompSize))
            return false;

        // RLE stream pipeline: zlib-inflate → un-RLE → reverse delta
        // predictor → byte de-interleave (same composition as the
        // standalone EXR compression=1 path; DWA reuses it for its
        // RLE-class channels).
        byte[] rleInflated = rleRawSize > 0 ? new byte[rleRawSize] : Array.Empty<byte>();
        if (rleRawSize > 0)
        {
            var stage1 = new byte[rleUncompSize];
            if (!ZlibInflate(src, rleStart, (int)rleCompSize, stage1, (int)rleUncompSize))
                return false;
            var unrled = new byte[rleRawSize];
            if (!RleDecompress(stage1, 0, (int)rleUncompSize, unrled, (int)rleRawSize))
                return false;
            // Byte de-interleave: encoder splits samples into two
            // halves (low bytes of all samples, then high bytes). DWA's
            // RLE path skips the delta predictor that compression=1
            // applies — the encoder pipeline uses zlib's own context
            // for cross-byte redundancy.
            int half = (int)(rleRawSize + 1) / 2;
            int t1 = 0, t2 = half;
            for (int i = 0; i < rleRawSize; )
            {
                rleInflated[i++] = unrled[t1++];
                if (i < rleRawSize) rleInflated[i++] = unrled[t2++];
            }
        }

        // Total planar bytes in this block, matched against the two
        // streams. We can't tell from the channel widths alone which
        // stream owns which channel, but the spec's classifier
        // assigns A → RLE and other-named-channels → UNKNOWN, so we
        // route the LAST channel(s) to RLE if the size accounting
        // matches.
        long totalPlanar = 0;
        foreach (int bw in channelByteWidths) totalPlanar += bw * width * rows;
        if (totalPlanar != unknownUncompSize + rleRawSize) return false;

        // Decide which channels are RLE-class by accumulating from the
        // end: the last K channels whose total bytes equals rleRawSize
        // are the RLE channels. Robust because libimf places A at the
        // beginning alphabetically, but the planar layout we need is
        // per-channel, and BOTH streams' inflated buffers are planar.
        int firstRleChannelIdx = channelByteWidths.Count;
        if (rleRawSize > 0)
        {
            long acc = 0;
            for (int i = channelByteWidths.Count - 1; i >= 0; i--)
            {
                acc += (long)channelByteWidths[i] * width * rows;
                if (acc == rleRawSize) { firstRleChannelIdx = i; break; }
                if (acc > rleRawSize) return false;
            }
            if (firstRleChannelIdx == channelByteWidths.Count) return false;
        }

        // Demux: each channel comes from either the unknown buffer or
        // the RLE buffer based on its index, with separate cursors
        // that walk the channel's planar slice in scanline order.
        long rowStride = 0;
        foreach (int bw in channelByteWidths) rowStride += bw * width;

        int unknownCursor = 0;
        int rleCursor = 0;
        long channelOffInRow = 0;
        for (int chIdx = 0; chIdx < channelByteWidths.Count; chIdx++)
        {
            int bw = channelByteWidths[chIdx];
            byte[] srcBuf = chIdx >= firstRleChannelIdx ? rleInflated : unknownInflated;
            int sp = chIdx >= firstRleChannelIdx ? rleCursor : unknownCursor;

            for (int r = 0; r < rows; r++)
            {
                long dstRowBase = r * rowStride + channelOffInRow;
                int rowBytes = width * bw;
                Buffer.BlockCopy(srcBuf, sp, dst, (int)dstRowBase, rowBytes);
                sp += rowBytes;
            }

            if (chIdx >= firstRleChannelIdx) rleCursor = sp;
            else unknownCursor = sp;
            channelOffInRow += bw * width;
        }
        return true;
    }

    /// <summary>
    /// EXR's run/literal byte RLE — same shape as compression=1. A
    /// signed control byte n in [-127, -1] introduces a run of
    /// (-n + 1) copies of the next byte; n in [0, 127] introduces
    /// (n + 1) literal bytes. n = -128 is a valid run-of-129.
    /// </summary>
    /// <summary>
    /// EXR's run/literal byte RLE per <c>ImfRle.cpp</c>:
    /// positive control byte n in [0, 127] introduces a run of (n + 1)
    /// copies of the next byte; negative n in [-128, -1] introduces
    /// (-n) literal bytes. Note this is INVERTED from PackBits — the
    /// standalone compression=1 path uses the opposite convention,
    /// which is also standard EXR (per OpenEXR source).
    /// </summary>
    private static bool RleDecompress(byte[] src, int srcOff, int srcLen, byte[] dst, int expected)
    {
        int sp = srcOff, sEnd = srcOff + srcLen;
        int dp = 0;
        while (sp < sEnd && dp < expected)
        {
            sbyte n = (sbyte)src[sp++];
            if (n >= 0)
            {
                int run = n + 1;
                if (sp >= sEnd || dp + run > expected) return false;
                byte v = src[sp++];
                for (int i = 0; i < run; i++) dst[dp++] = v;
            }
            else
            {
                int run = -n;
                if (sp + run > sEnd || dp + run > expected) return false;
                Buffer.BlockCopy(src, sp, dst, dp, run);
                sp += run;
                dp += run;
            }
        }
        return dp == expected;
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
