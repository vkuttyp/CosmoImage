using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace CosmoImage.Loaders;

/// <summary>
/// EXR PIZ (compression=4) decompressor and supporting primitives.
/// Built bottom-up with isolated round-trip tests for each piece:
/// 2D Haar wavelet, canonical Huffman with EXR's run-length code-length
/// transmission, bitmap-driven LUT compaction. The full block decode
/// composes these in the order: Huffman bitstream → LUT → 2D inverse
/// wavelet per channel → demux to chunky output layout.
///
/// <para>Everything is tagged <c>internal</c> so unit tests can
/// exercise the primitives directly.</para>
/// </summary>
internal static class ExrPiz
{
    internal const int BitmapSize = 8192;       // (USHORT_RANGE / 8) bytes
    internal const int UshortRange = 65536;
    /// <summary>Huffman alphabet size — 65536 + 1 reserved RLE-marker symbol.</summary>
    internal const int HufEncSize = UshortRange + 1;
    private const int ShortZeroCodeRun = 59;
    private const int LongZeroCodeRun = 63;
    private const int ShortestLongRun = 2 + LongZeroCodeRun - ShortZeroCodeRun;

    // =========================================================================
    // 2D Haar wavelet — per libimf ImfWav.cpp.
    //
    // The encoder iterates fine-to-coarse (p=1, 2, 4, ...) so each level
    // operates on the LL coefficients produced by the previous level.
    // The decoder iterates coarse-to-fine, reversing each step.
    //
    // Within each scale's quad: encode does horizontal-then-vertical;
    // decode does vertical-then-horizontal (so the in-place writes
    // unwind in the right order).
    //
    // The 14-bit variant (`mx < 16384`) uses signed shift in the average,
    // which keeps values within the signed-short range. The 16-bit
    // variant handles full-range data.
    // =========================================================================

    /// <summary>Forward Haar wavelet pyramid (encoder side, used only for round-trip tests).</summary>
    internal static void Wav2Encode(ushort[] data, int offset, int nx, int ny, int ox, int oy, ushort mx)
    {
        bool w14 = mx < (1 << 14);
        int n = nx > ny ? ny : nx;
        int p = 1;
        int p2 = 2;
        while (p2 <= n)
        {
            EncodeAtScale(data, offset, nx, ny, ox, oy, p, p2, w14);
            p <<= 1;
            p2 <<= 1;
        }
    }

    /// <summary>Inverse Haar wavelet pyramid (decoder side).</summary>
    internal static void Wav2Decode(ushort[] data, int offset, int nx, int ny, int ox, int oy, ushort mx)
    {
        bool w14 = mx < (1 << 14);
        int n = nx > ny ? ny : nx;
        int p = 1;
        int p2 = 2;
        while (p2 <= n) { p <<= 1; p2 <<= 1; }
        // Now p = largest power-of-two ≤ n. Decode iterates from p/2 down to 1.
        while ((p >>= 1) >= 1)
        {
            p2 = p << 1;
            DecodeAtScale(data, offset, nx, ny, ox, oy, p, p2, w14);
        }
    }

    /// <summary>One forward pass at scale (p, p2): horizontal-then-vertical wenc per quad.</summary>
    private static void EncodeAtScale(ushort[] data, int offset, int nx, int ny, int ox, int oy,
        int p, int p2, bool w14)
    {
        int oy1 = oy * p;
        int oy2 = oy * p2;
        int ox1 = ox * p;
        int ox2 = ox * p2;

        int py = 0;
        int ey = (ny - p2) * oy;
        for (; py <= ey; py += oy2)
        {
            int px = py;
            int ex = py + (nx - p2) * ox;
            for (; px <= ex; px += ox2)
            {
                int p01 = px + ox1;
                int p10 = px + oy1;
                int p11 = p10 + ox1;
                Wenc(data, offset + px,  offset + p01, w14);
                Wenc(data, offset + p10, offset + p11, w14);
                Wenc(data, offset + px,  offset + p10, w14);
                Wenc(data, offset + p01, offset + p11, w14);
            }
            if ((nx & p) != 0)
            {
                int p10 = px + oy1;
                Wenc(data, offset + px, offset + p10, w14);
            }
        }
        if ((ny & p) != 0)
        {
            int px = py;
            int ex = py + (nx - p2) * ox;
            for (; px <= ex; px += ox2)
            {
                int p01 = px + ox1;
                Wenc(data, offset + px, offset + p01, w14);
            }
        }
    }

    /// <summary>One inverse pass at scale (p, p2): vertical-then-horizontal wdec per quad.</summary>
    private static void DecodeAtScale(ushort[] data, int offset, int nx, int ny, int ox, int oy,
        int p, int p2, bool w14)
    {
        int oy1 = oy * p;
        int oy2 = oy * p2;
        int ox1 = ox * p;
        int ox2 = ox * p2;

        int py = 0;
        int ey = (ny - p2) * oy;
        for (; py <= ey; py += oy2)
        {
            int px = py;
            int ex = py + (nx - p2) * ox;
            for (; px <= ex; px += ox2)
            {
                int p01 = px + ox1;
                int p10 = px + oy1;
                int p11 = p10 + ox1;
                Wdec(data, offset + px,  offset + p10, w14);
                Wdec(data, offset + p01, offset + p11, w14);
                Wdec(data, offset + px,  offset + p01, w14);
                Wdec(data, offset + p10, offset + p11, w14);
            }
            if ((nx & p) != 0)
            {
                int p10 = px + oy1;
                Wdec(data, offset + px, offset + p10, w14);
            }
        }
        if ((ny & p) != 0)
        {
            int px = py;
            int ex = py + (nx - p2) * ox;
            for (; px <= ex; px += ox2)
            {
                int p01 = px + ox1;
                Wdec(data, offset + px, offset + p01, w14);
            }
        }
    }

    /// <summary>
    /// 16-bit Haar offset: encoder pre-shifts <c>a</c> by this so the
    /// "average" formula stays inside the unsigned-16 range; decoder
    /// un-shifts at the end. Per ImfWav.cpp.
    /// </summary>
    private const int AOffset = 0x4000;

    private static void Wenc(ushort[] data, int ai, int bi, bool w14)
    {
        if (w14)
        {
            short a = unchecked((short)data[ai]);
            short b = unchecked((short)data[bi]);
            short l = (short)((a + b) >> 1);
            short h = (short)(a - b);
            data[ai] = unchecked((ushort)l);
            data[bi] = unchecked((ushort)h);
        }
        else
        {
            int a = (data[ai] + AOffset) & 0xFFFF;
            int b = data[bi];
            int l = (a + b) >> 1;
            int h = (a - b) & 0xFFFF;
            data[ai] = (ushort)l;
            data[bi] = (ushort)h;
        }
    }

    private static void Wdec(ushort[] data, int li, int hi, bool w14)
    {
        if (w14)
        {
            short ls = unchecked((short)data[li]);
            short hs = unchecked((short)data[hi]);
            int hint = hs;
            int aint = ls + (hint & 1) + (hint >> 1);
            short asValue = (short)aint;
            short bsValue = (short)(aint - hint);
            data[li] = unchecked((ushort)asValue);
            data[hi] = unchecked((ushort)bsValue);
        }
        else
        {
            int l = data[li];
            int h = data[hi];
            int b = (l - (h >> 1)) & 0xFFFF;
            int a = (h + b - AOffset) & 0xFFFF;
            data[li] = (ushort)a;
            data[hi] = (ushort)b;
        }
    }

    // =========================================================================
    // Canonical Huffman with EXR's longest-first convention.
    // =========================================================================

    /// <summary>
    /// Build canonical Huffman codes from per-symbol code lengths.
    /// EXR processes lengths longest-to-shortest, then within each
    /// length assigns codes in symbol order. Returns
    /// <c>codeOf[symbol]</c> with length packed in low 6 bits and code
    /// in remaining bits. Symbols with length 0 map to 0.
    /// </summary>
    internal static ulong[] BuildCanonicalCodes(int[] freq, int im, int iM)
    {
        var codes = new ulong[Math.Max(iM + 1, HufEncSize)];
        int maxLen = 0;
        for (int i = im; i <= iM; i++)
            if (freq[i] > maxLen) maxLen = freq[i];
        if (maxLen == 0) return codes;

        var blCount = new int[maxLen + 2];
        for (int i = im; i <= iM; i++)
            if (freq[i] > 0) blCount[freq[i]]++;

        // Compute base code per length, longest-first.
        var nextCode = new int[maxLen + 2];
        int baseCode = 0;
        for (int L = maxLen; L > 0; L--)
        {
            int next = (baseCode + blCount[L]) >> 1;
            nextCode[L] = baseCode;
            baseCode = next;
        }

        for (int sym = im; sym <= iM; sym++)
        {
            int len = freq[sym];
            if (len == 0) continue;
            int code = nextCode[len]++;
            codes[sym] = ((ulong)(uint)len) | ((ulong)(uint)code << 6);
        }
        return codes;
    }

    /// <summary>
    /// Encode a token stream into a packed bitstream using the supplied
    /// canonical Huffman codes. Run-length-encodes runs of length
    /// <c>≥ rleThreshold</c> using the symbol <paramref name="rlc"/>
    /// (always = iM in PIZ). Returns the bitstream + total bits.
    /// </summary>
    internal static (byte[] Bits, int NBits) HuffmanEncode(ulong[] codes, ushort[] tokens, int rlc, int rleThreshold = 4)
    {
        var ms = new System.IO.MemoryStream();
        ulong acc = 0;
        int accBits = 0;
        int totalBits = 0;

        void Emit(int code, int len)
        {
            acc = (acc << len) | (ulong)(uint)code;
            accBits += len;
            totalBits += len;
            while (accBits >= 8)
            {
                accBits -= 8;
                ms.WriteByte((byte)(acc >> accBits));
                acc &= (1UL << accBits) - 1;
            }
        }

        void EmitSymbol(int sym)
        {
            ulong packed = codes[sym];
            int len = (int)(packed & 0x3F);
            int code = (int)(packed >> 6);
            Emit(code, len);
        }

        if (tokens.Length > 0)
        {
            int s = tokens[0];
            int run = 0;
            EmitSymbol(s);
            for (int i = 1; i < tokens.Length; i++)
            {
                if (tokens[i] == s && run < 255)
                {
                    run++;
                }
                else
                {
                    if (run >= rleThreshold)
                    {
                        EmitSymbol(rlc);
                        Emit(run, 8);
                    }
                    else
                    {
                        for (int k = 0; k < run; k++) EmitSymbol(s);
                    }
                    run = 0;
                    s = tokens[i];
                    EmitSymbol(s);
                }
            }
            // Flush final run
            if (run >= rleThreshold)
            {
                EmitSymbol(rlc);
                Emit(run, 8);
            }
            else
            {
                for (int k = 0; k < run; k++) EmitSymbol(s);
            }
        }

        // Flush remaining bits to byte boundary.
        if (accBits > 0)
        {
            ms.WriteByte((byte)(acc << (8 - accBits)));
        }
        return (ms.ToArray(), totalBits);
    }

    /// <summary>
    /// Decode tokens from a packed bitstream using the canonical Huffman
    /// codes derived from <paramref name="freq"/>. Returns the count of
    /// successfully decoded tokens; sets <paramref name="err"/> on
    /// malformed input.
    /// </summary>
    internal static int HuffmanDecode(int[] freq, int im, int iM,
        BitReader br, int nBits, int totalSamples, ushort[] dst, out string err)
    {
        err = "";
        int maxLen = 0;
        for (int i = im; i <= iM; i++)
            if (freq[i] > maxLen) maxLen = freq[i];
        if (maxLen == 0) { err = "maxLen=0"; return 0; }
        if (maxLen > 58) { err = $"maxLen={maxLen}"; return 0; }

        // Build code-to-symbol lookup, bucketed by length.
        var blCount = new int[maxLen + 2];
        for (int i = im; i <= iM; i++)
            if (freq[i] > 0) blCount[freq[i]]++;

        var nextCode = new int[maxLen + 2];
        int baseCode = 0;
        for (int L = maxLen; L > 0; L--)
        {
            int next = (baseCode + blCount[L]) >> 1;
            nextCode[L] = baseCode;
            baseCode = next;
        }

        var byLenLists = new List<(int Code, int Sym)>[maxLen + 2];
        for (int i = 0; i <= maxLen + 1; i++) byLenLists[i] = new();
        for (int sym = im; sym <= iM; sym++)
        {
            int len = freq[sym];
            if (len == 0) continue;
            int code = nextCode[len]++;
            byLenLists[len].Add((code, sym));
        }
        var byLen = new (int Code, int Sym)[maxLen + 2][];
        for (int i = 0; i <= maxLen + 1; i++) byLen[i] = byLenLists[i].ToArray();

        int totalDecoded = 0;
        long startBits = br.BitsConsumed;
        while (totalDecoded < totalSamples && br.BitsConsumed - startBits < nBits)
        {
            int curCode = 0;
            int sym = -1;
            for (int len = 1; len <= maxLen; len++)
            {
                int bit = br.Read(1);
                if (bit < 0) { err = "bitEOF"; return totalDecoded; }
                curCode = (curCode << 1) | bit;
                var bucket = byLen[len];
                for (int k = 0; k < bucket.Length; k++)
                {
                    if (bucket[k].Code == curCode)
                    {
                        sym = bucket[k].Sym;
                        break;
                    }
                }
                if (sym >= 0) break;
            }
            if (sym < 0) { err = $"noMatch curCode={curCode} totalDecoded={totalDecoded}"; return totalDecoded; }

            if (sym == iM)
            {
                if (totalDecoded == 0) { err = "RLE-at-start"; return totalDecoded; }
                int rl = br.Read(8);
                if (rl < 0) { err = "rlEOF"; return totalDecoded; }
                ushort prev = dst[totalDecoded - 1];
                if (totalDecoded + rl > totalSamples) { err = $"rlOverflow rl={rl}"; return totalDecoded; }
                for (int k = 0; k < rl; k++) dst[totalDecoded++] = prev;
            }
            else
            {
                if (totalDecoded >= totalSamples) { err = "samplesOverflow"; return totalDecoded; }
                dst[totalDecoded++] = (ushort)sym;
            }
        }
        if (totalDecoded != totalSamples) err = $"underrun";
        return totalDecoded;
    }

    // =========================================================================
    // Bitmap → reverse LUT.
    // =========================================================================

    /// <summary>Build a per-bit "value used" bitmap from a token stream.</summary>
    internal static void BitmapFromTokens(ushort[] tokens, byte[] bitmap, out int minNonZero, out int maxNonZero)
    {
        Array.Clear(bitmap);
        foreach (ushort v in tokens)
            bitmap[v >> 3] |= (byte)(1 << (v & 7));
        // Scan for min/max non-zero byte.
        minNonZero = BitmapSize;
        maxNonZero = 0;
        for (int i = 0; i < BitmapSize; i++)
            if (bitmap[i] != 0)
            {
                if (i < minNonZero) minNonZero = i;
                if (i > maxNonZero) maxNonZero = i;
            }
    }

    /// <summary>
    /// Build a forward LUT (value → token) and reverse LUT (token →
    /// value) from a bitmap. Returns the number of distinct values
    /// found. Caller-supplied arrays must be sized <see cref="UshortRange"/>.
    /// </summary>
    internal static ushort ForwardAndReverseLut(byte[] bitmap, ushort[] forward, ushort[] reverse)
    {
        int k = 0;
        for (int i = 0; i < UshortRange; i++)
        {
            if ((bitmap[i >> 3] & (1 << (i & 7))) != 0)
            {
                reverse[k] = (ushort)i;
                forward[i] = (ushort)k;
                k++;
            }
        }
        ushort n = (ushort)(k - 1);
        // Pad reverse with zeros for any out-of-range token reads.
        while (k < UshortRange) reverse[k++] = 0;
        return n;
    }

    // =========================================================================
    // BitReader — MSB-first within bytes.
    // =========================================================================

    internal sealed class BitReader
    {
        private readonly byte[] _buf;
        private readonly int _start;
        private readonly int _end;
        private int _pos;
        private int _bits;
        private int _accum;
        public long BitsConsumed { get; private set; }

        public BitReader(byte[] buf, int offset, int length)
        {
            _buf = buf; _start = offset; _end = offset + length; _pos = offset;
        }

        public int Read(int n)
        {
            while (_bits < n)
            {
                if (_pos >= _end) return -1;
                _accum = (_accum << 8) | _buf[_pos++];
                _bits += 8;
            }
            int v = (_accum >> (_bits - n)) & ((1 << n) - 1);
            _bits -= n;
            _accum &= (1 << _bits) - 1;
            BitsConsumed += n;
            return v;
        }
    }

    // =========================================================================
    // Code-length transmission (UnpackEncTable / PackEncTable).
    // =========================================================================

    /// <summary>
    /// Full PIZ block decompress. Orchestrates bitmap parse → reverse
    /// LUT → Huffman header + bitstream decode → LUT apply → 2D inverse
    /// wavelet per channel → demux to per-row-per-channel-per-pixel
    /// output. Returns false on malformed input or unsupported W16 wraps.
    /// </summary>
    public static bool Decompress(byte[] src, int srcOff, int srcLen,
        byte[] dst, int rows, IReadOnlyList<int> channelByteWidths, int width)
    {
        // Each "sample" in the wavelet/Huffman pipeline is one ushort.
        // HALF contributes 1 sample/pixel; FLOAT/UINT contribute 2.
        int samplesPerPixelTotal = 0;
        foreach (int bw in channelByteWidths) samplesPerPixelTotal += bw / 2;
        int totalSamples = samplesPerPixelTotal * rows * width;

        int p = srcOff;
        int end = srcOff + srcLen;
        if (p + 4 > end) return false;

        int minNonZero = BinaryPrimitives.ReadUInt16LittleEndian(src.AsSpan(p, 2)); p += 2;
        int maxNonZero = BinaryPrimitives.ReadUInt16LittleEndian(src.AsSpan(p, 2)); p += 2;
        if (maxNonZero >= BitmapSize) return false;

        var bitmap = new byte[BitmapSize];
        if (minNonZero <= maxNonZero)
        {
            int len = maxNonZero - minNonZero + 1;
            if (p + len > end) return false;
            Buffer.BlockCopy(src, p, bitmap, minNonZero, len);
            p += len;
        }

        var fwdLut = new ushort[UshortRange];
        var revLut = new ushort[UshortRange];
        ushort maxValue = ForwardAndReverseLut(bitmap, fwdLut, revLut);

        // Huffman header: 4-byte length prefix, then 20-byte header.
        if (p + 4 > end) return false;
        int hufLength = BinaryPrimitives.ReadInt32LittleEndian(src.AsSpan(p, 4)); p += 4;
        if (hufLength < 20 || p + hufLength > end) return false;
        if (p + 20 > end) return false;
        int im = BinaryPrimitives.ReadInt32LittleEndian(src.AsSpan(p, 4)); p += 4;
        int iM = BinaryPrimitives.ReadInt32LittleEndian(src.AsSpan(p, 4)); p += 4;
        p += 4;
        int nBits = BinaryPrimitives.ReadInt32LittleEndian(src.AsSpan(p, 4)); p += 4;
        p += 4;

        if (im < 0 || iM < im || iM >= HufEncSize) return false;
        if (nBits < 0) return false;

        var freq = new int[HufEncSize];
        var tableBr = new BitReader(src, p, end - p);
        if (!UnpackEncTable(tableBr, im, iM, freq)) return false;

        int tableBytesConsumed = (int)((tableBr.BitsConsumed + 7) / 8);
        int dataStart = p + tableBytesConsumed;
        if (dataStart > end) return false;
        var br = new BitReader(src, dataStart, end - dataStart);

        var raw = new ushort[totalSamples];
        if (HuffmanDecode(freq, im, iM, br, nBits, totalSamples, raw, out _) != totalSamples)
            return false;

        // Per libimf ImfPizCompressor.cpp the canonical decode order is
        // inverse-wavelet-then-reverse-LUT (the wavelet operates in token
        // space; the reverse LUT maps tokens back to raw 16-bit values).
        // We had this reversed, which produced garbage when integrating
        // with our own encoder primitives.
        int sampleOff = 0;
        foreach (int bw in channelByteWidths)
        {
            int subBands = bw / 2;
            for (int sub = 0; sub < subBands; sub++)
            {
                Wav2Decode(raw, sampleOff, width, rows, 1, width, maxValue);
                sampleOff += width * rows;
            }
        }

        // Apply reverse LUT — token index → original 16-bit value.
        for (int i = 0; i < totalSamples; i++) raw[i] = revLut[raw[i]];

        // Demux into the standard per-row-per-channel-per-pixel layout
        // (matches what DemuxRegionRow in PureExrDecoder expects).
        int dstP = 0;
        for (int row = 0; row < rows; row++)
        {
            int chSampleOff = 0;
            foreach (int bw in channelByteWidths)
            {
                int sw = bw / 2;
                for (int sub = 0; sub < sw; sub++)
                {
                    int srcRowBase = chSampleOff + sub * width * rows + row * width;
                    for (int x = 0; x < width; x++)
                    {
                        ushort v = raw[srcRowBase + x];
                        dst[dstP++] = (byte)v;
                        dst[dstP++] = (byte)(v >> 8);
                    }
                }
                chSampleOff += sw * width * rows;
            }
        }
        return true;
    }

    /// <summary>
    /// Pack a code-length table into the bit-packed form
    /// <see cref="UnpackEncTable"/> expects. 6-bit values per slot;
    /// zero runs collapse via <see cref="ShortZeroCodeRun"/> /
    /// <see cref="LongZeroCodeRun"/>. Caller bears responsibility for
    /// producing a code-length table whose maxLen ≤ 58 (the decoder
    /// enforces this).
    /// </summary>
    internal static byte[] PackEncTable(int[] freq, int im, int iM)
    {
        var ms = new System.IO.MemoryStream();
        ulong acc = 0;
        int accBits = 0;

        void Emit(int value, int len)
        {
            acc = (acc << len) | (ulong)(uint)value;
            accBits += len;
            while (accBits >= 8)
            {
                accBits -= 8;
                ms.WriteByte((byte)(acc >> accBits));
                acc &= (1UL << accBits) - 1;
            }
        }

        int i = im;
        while (i <= iM)
        {
            if (freq[i] != 0)
            {
                Emit(freq[i], 6);
                i++;
                continue;
            }
            // Zero run starting at i.
            int j = i;
            while (j <= iM && freq[j] == 0) j++;
            int runLen = j - i;
            // Encode the run greedily, longest match first.
            while (runLen > 0)
            {
                if (runLen >= ShortestLongRun)
                {
                    int chunk = Math.Min(runLen, 255 + ShortestLongRun);
                    Emit(LongZeroCodeRun, 6);
                    Emit(chunk - ShortestLongRun, 8);
                    runLen -= chunk;
                }
                else if (runLen >= 2)
                {
                    Emit(ShortZeroCodeRun + runLen - 2, 6);
                    runLen = 0;
                }
                else
                {
                    Emit(0, 6);
                    runLen--;
                }
            }
            i = j;
        }

        // Flush remaining bits to byte boundary.
        if (accBits > 0)
            ms.WriteByte((byte)(acc << (8 - accBits)));
        return ms.ToArray();
    }

    /// <summary>
    /// PIZ-encode a block. Inverse of <see cref="Decompress"/>: takes a
    /// raw pixel buffer in the per-row-per-channel-per-pixel layout and
    /// produces the on-disk PIZ bitstream. Used by tests to round-trip
    /// our own primitives end-to-end. Production code only ever decodes.
    /// </summary>
    public static byte[] Compress(byte[] src, int rows,
        IReadOnlyList<int> channelByteWidths, int width)
    {
        int samplesPerPixelTotal = 0;
        foreach (int bw in channelByteWidths) samplesPerPixelTotal += bw / 2;
        int totalSamples = samplesPerPixelTotal * rows * width;

        // 1. Mux from per-row layout into per-channel-sub-band layout
        //    (the layout the wavelet operates on).
        var raw = new ushort[totalSamples];
        int srcP = 0;
        for (int row = 0; row < rows; row++)
        {
            int chSampleOff = 0;
            foreach (int bw in channelByteWidths)
            {
                int sw = bw / 2;
                for (int sub = 0; sub < sw; sub++)
                {
                    int dstRowBase = chSampleOff + sub * width * rows + row * width;
                    for (int x = 0; x < width; x++)
                    {
                        ushort v = (ushort)(src[srcP] | (src[srcP + 1] << 8));
                        raw[dstRowBase + x] = v;
                        srcP += 2;
                    }
                }
                chSampleOff += sw * width * rows;
            }
        }

        // 2. Build bitmap from raw values, derive forward + reverse LUT.
        //    (Bitmap reflects which 16-bit values occur in the data.)
        var bitmap = new byte[BitmapSize];
        BitmapFromTokens(raw, bitmap, out int minNonZero, out int maxNonZero);
        var fwdLut = new ushort[UshortRange];
        var revLut = new ushort[UshortRange];
        ushort maxValue = ForwardAndReverseLut(bitmap, fwdLut, revLut);

        // 3. Apply forward LUT — raw → tokens (compresses the symbol space).
        for (int i = 0; i < totalSamples; i++) raw[i] = fwdLut[raw[i]];

        // 4. Forward wavelet per sub-band, in token space.
        int sampleOff = 0;
        foreach (int bw in channelByteWidths)
        {
            int sw = bw / 2;
            for (int sub = 0; sub < sw; sub++)
            {
                Wav2Encode(raw, sampleOff, width, rows, 1, width, maxValue);
                sampleOff += width * rows;
            }
        }

        // 5. Compute Huffman frequencies on the wavelet output. The RLE
        //    marker is, by libimf convention, the largest symbol in the
        //    alphabet (iM); we reserve the top slot of the
        //    65536-symbol-plus-one alphabet for it so the marker is
        //    always distinguishable from any 16-bit data sample.
        var freq = new int[HufEncSize];
        BuildHuffmanFrequencies(raw, freq);
        int rlc = HufEncSize - 1;
        freq[rlc] = 1;
        FrequenciesToCodeLengths(freq);

        int im = 0; while (im < HufEncSize && freq[im] == 0) im++;
        int iM = rlc;
        if (im > iM) { im = 0; iM = 0; }

        // 6. Build canonical codes + encode bitstream.
        var codes = BuildCanonicalCodes(freq, im, iM);
        var (encodedBits, nBits) = HuffmanEncode(codes, raw, rlc);

        // 7. Pack the code-length table + serialize wire format:
        //    u16 minBits, u16 maxBits, [bitmap[min..max]], u32 hufLen,
        //    u32 im, u32 iM, u32 (unused), u32 nBits, u32 (unused),
        //    [code-length table], [encoded bits].
        var packedTable = PackEncTable(freq, im, iM);
        int hufLen = 20 + packedTable.Length + encodedBits.Length;

        var ms = new System.IO.MemoryStream();
        Span<byte> u16 = stackalloc byte[2];
        Span<byte> u32 = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(u16, (ushort)minNonZero); ms.Write(u16);
        BinaryPrimitives.WriteUInt16LittleEndian(u16, (ushort)maxNonZero); ms.Write(u16);
        if (minNonZero <= maxNonZero)
            ms.Write(bitmap, minNonZero, maxNonZero - minNonZero + 1);
        BinaryPrimitives.WriteInt32LittleEndian(u32, hufLen); ms.Write(u32);
        BinaryPrimitives.WriteInt32LittleEndian(u32, im); ms.Write(u32);
        BinaryPrimitives.WriteInt32LittleEndian(u32, iM); ms.Write(u32);
        BinaryPrimitives.WriteInt32LittleEndian(u32, 0); ms.Write(u32);
        BinaryPrimitives.WriteInt32LittleEndian(u32, nBits); ms.Write(u32);
        BinaryPrimitives.WriteInt32LittleEndian(u32, 0); ms.Write(u32);
        ms.Write(packedTable);
        ms.Write(encodedBits);
        return ms.ToArray();
    }

    private static void BuildHuffmanFrequencies(ushort[] tokens, int[] freq)
    {
        for (int i = 0; i < tokens.Length; i++)
            freq[tokens[i]]++;
    }

    /// <summary>
    /// Convert symbol frequencies into Huffman code lengths in place.
    /// <paramref name="freq"/> goes in as counts and comes out as
    /// <c>codeOf[sym] = bitLength</c>, which is what
    /// <see cref="BuildCanonicalCodes"/> expects. Standard min-heap
    /// Huffman construction; ties broken by symbol order so output is
    /// deterministic. Caller is responsible for ensuring no length
    /// exceeds 58 (the wire format's 6-bit length cap).
    /// </summary>
    internal static void FrequenciesToCodeLengths(int[] freq)
    {
        var leaves = new System.Collections.Generic.List<int>();
        for (int i = 0; i < freq.Length; i++) if (freq[i] > 0) leaves.Add(i);
        if (leaves.Count == 0) return;
        if (leaves.Count == 1)
        {
            // Single-symbol input: the canonical-code builder needs a
            // non-zero length to emit anything, so synthesize length 1.
            int s = leaves[0];
            for (int i = 0; i < freq.Length; i++) freq[i] = 0;
            freq[s] = 1;
            return;
        }

        int leafCount = leaves.Count;
        int maxNodes = 2 * leafCount - 1;
        var nodeCount = new long[maxNodes];
        var nodeParent = new int[maxNodes];
        for (int i = 0; i < leafCount; i++) nodeCount[i] = freq[leaves[i]];

        var pq = new System.Collections.Generic.SortedSet<(long count, int seq)>();
        for (int i = 0; i < leafCount; i++) pq.Add((nodeCount[i], i));

        int next = leafCount;
        while (pq.Count > 1)
        {
            var a = pq.Min; pq.Remove(a);
            var b = pq.Min; pq.Remove(b);
            nodeCount[next] = a.count + b.count;
            nodeParent[a.seq] = next;
            nodeParent[b.seq] = next;
            pq.Add((nodeCount[next], next));
            next++;
        }
        int root = next - 1;

        for (int i = 0; i < freq.Length; i++) freq[i] = 0;
        for (int i = 0; i < leafCount; i++)
        {
            int depth = 0;
            int node = i;
            while (node != root) { node = nodeParent[node]; depth++; }
            freq[leaves[i]] = depth;
        }
    }

    internal static bool UnpackEncTable(BitReader br, int im, int iM, int[] freq)
    {
        for (int i = im; i <= iM; )
        {
            int n = br.Read(6);
            if (n < 0) return false;
            if (n == LongZeroCodeRun)
            {
                int rl = br.Read(8);
                if (rl < 0) return false;
                int run = rl + ShortestLongRun;
                if (i + run > iM + 1) return false;
                for (int k = 0; k < run; k++) freq[i + k] = 0;
                i += run;
            }
            else if (n >= ShortZeroCodeRun)
            {
                int run = n - ShortZeroCodeRun + 2;
                if (i + run > iM + 1) return false;
                for (int k = 0; k < run; k++) freq[i + k] = 0;
                i += run;
            }
            else
            {
                freq[i++] = n;
            }
        }
        return true;
    }
}
