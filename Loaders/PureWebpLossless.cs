using System;
using System.Collections.Generic;
using CosmoImage.Core;

namespace CosmoImage.Loaders;

/// <summary>
/// Pure-managed WebP VP8L (lossless) decoder. Parses the RIFF
/// container, locates the <c>VP8L</c> chunk, and decodes per the
/// WebP Lossless Bitstream Specification:
///   - LSB-first bit reader
///   - Up to four nested transforms (predictor / cross-color /
///     subtract-green / color-indexing) read in order, applied in
///     reverse on the pixel buffer
///   - Five prefix-code groups (G+length+cache / R / B / A / D)
///   - Optional color cache (0..11 bits)
///   - LZ77 backreferences with the WebP "near-neighbor" distance
///     map for the first 120 distance codes
///
/// <para>Returns <c>null</c> for VP8 (lossy), VP8X (extended /
/// animated), or any malformed stream — callers fall back to the
/// Magick.NET path.</para>
/// </summary>
internal static class PureWebpLossless
{
    public static VipsImage? TryDecode(byte[] bytes)
    {
        if (bytes == null || bytes.Length < 12 + 8) return null;
        // RIFF + 4-byte length + "WEBP"
        if (bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F') return null;
        if (bytes[8] != 'W' || bytes[9] != 'E' || bytes[10] != 'B' || bytes[11] != 'P') return null;

        // Walk chunks looking for VP8L. Reject if we hit VP8 or VP8X first
        // (we only handle plain VP8L here; animated / extended is Magick).
        int p = 12;
        int vp8lOff = -1, vp8lLen = 0;
        while (p + 8 <= bytes.Length)
        {
            uint fourcc = BitConverter.ToUInt32(bytes, p);
            int len = BitConverter.ToInt32(bytes, p + 4);
            int payload = p + 8;
            if (len < 0 || payload + len > bytes.Length) return null;

            // 'VP8L' little-endian = 0x4C385056
            if (fourcc == 0x4C385056) { vp8lOff = payload; vp8lLen = len; break; }
            // 'VP8 ' or 'VP8X' or anything else → not a plain lossless WebP.
            if (fourcc == 0x20385056 || fourcc == 0x58385056) return null;
            // Skip ALPH/ICCP/EXIF/XMP and similar; pad to even.
            p = payload + len + (len & 1);
        }
        if (vp8lOff < 0) return null;

        var br = new BitReader(bytes, vp8lOff, vp8lLen);
        if (br.ReadBits(8) != 0x2F) return null;  // VP8L signature
        int width = br.ReadBits(14) + 1;
        int height = br.ReadBits(14) + 1;
        br.ReadBits(1);                            // alpha_is_used (advisory)
        if (br.ReadBits(3) != 0) return null;      // version

        // Read up to 4 transforms. Each transform may consume sub-image
        // params from the bitstream; the actual main-image dimensions
        // mutate when ColorIndexing applies bit-packing.
        var transforms = new List<Transform>(4);
        var seen = new bool[4];
        int xSize = width;
        while (br.ReadBits(1) == 1)
        {
            if (transforms.Count == 4 || br.Failed) return null;
            int type = br.ReadBits(2);
            if (seen[type]) return null;
            seen[type] = true;
            var t = Transform.Read(type, ref xSize, height, br);
            if (t == null) return null;
            transforms.Add(t);
        }

        // Decode the spatially-coded pixel grid (xSize × height ARGB).
        var pixels = SpatialDecoder.Decode(br, xSize, height, topLevel: true);
        if (pixels == null || br.Failed) return null;

        // Reverse-apply transforms.
        for (int i = transforms.Count - 1; i >= 0; i--)
            pixels = transforms[i].Apply(pixels, ref xSize, height);

        if (xSize != width) return null;

        // VP8L stores ARGB in B G R A order in memory (little-endian uint32 layout
        // ARGB -> bytes [B, G, R, A]). The internal decoder produces uint32 with
        // (A << 24) | (R << 16) | (G << 8) | B; expand to [R,G,B,A] for VipsImage.
        return BuildImage(pixels, width, height);
    }

    private static VipsImage BuildImage(uint[] pixels, int width, int height)
    {
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            uint p = pixels[i];
            rgba[i * 4 + 0] = (byte)(p >> 16);   // R
            rgba[i * 4 + 1] = (byte)(p >> 8);    // G
            rgba[i * 4 + 2] = (byte)p;           // B
            rgba[i * 4 + 3] = (byte)(p >> 24);   // A
        }
        return new VipsImage
        {
            Width = width,
            Height = height,
            Bands = 4,
            BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            Coding = VipsCoding.None,
            XRes = 1.0,
            YRes = 1.0,
            PixelsLazy = new Lazy<byte[]>(() => rgba),
        };
    }

    // =========================================================================
    // BitReader — LSB-first within bytes, with cheap prefetch.
    // =========================================================================

    internal sealed class BitReader
    {
        private readonly byte[] _buf;
        private readonly int _start;
        private readonly int _end;
        private int _pos;
        private ulong _accum;
        private int _bits;
        public bool Failed;

        public BitReader(byte[] buf, int offset, int length)
        {
            _buf = buf;
            _start = offset;
            _end = offset + length;
            _pos = offset;
        }

        /// <summary>Read up to 32 bits LSB-first; returns 0 + sets Failed on underflow.</summary>
        public int ReadBits(int n)
        {
            while (_bits < n)
            {
                if (_pos >= _end) { Failed = true; return 0; }
                _accum |= (ulong)_buf[_pos++] << _bits;
                _bits += 8;
            }
            int v = (int)(_accum & ((1UL << n) - 1));
            _accum >>= n;
            _bits -= n;
            return v;
        }
    }

    // =========================================================================
    // Huffman tree — canonical-form decoder.
    // =========================================================================

    /// <summary>
    /// Canonical Huffman tree. Built from a list of code lengths
    /// (DEFLATE-style); decoding walks bit-by-bit through a binary
    /// tree stored in two flat arrays.
    /// </summary>
    internal sealed class HuffmanTree
    {
        // Tree nodes: -1 = leaf-with-symbol-in `_symbol`; else index of left child (right = +1).
        // We pack as {symbol-or-jump}. Negative = leaf with -value-1 = symbol.
        private readonly int[] _tree;
        private readonly int _nodes;
        public int NumSymbols { get; }
        public int Single { get; }            // single-symbol tree shortcut, -1 otherwise

        private HuffmanTree(int[] tree, int nodes, int numSymbols, int single)
        {
            _tree = tree;
            _nodes = nodes;
            NumSymbols = numSymbols;
            Single = single;
        }

        /// <summary>
        /// Build an explicit 2-symbol tree where bit '0' resolves to
        /// <paramref name="sym0"/> and bit '1' resolves to <paramref name="sym1"/>.
        /// Used by the spec's simple-code Huffman mode, which transmits
        /// symbols in their decode order (not numeric order).
        /// </summary>
        public static HuffmanTree TwoSymbol(int sym0, int sym1, int alphabetSize)
        {
            var tree = new int[4];
            tree[0] = -(sym0 + 1);
            tree[1] = -(sym1 + 1);
            return new HuffmanTree(tree, 1, alphabetSize, -1);
        }

        /// <summary>
        /// Build a canonical Huffman tree from per-symbol code lengths.
        /// Returns null on malformed input (over-/under-subscribed code).
        /// </summary>
        public static HuffmanTree? FromLengths(int[] lengths)
        {
            int maxLen = 0, nonZero = 0, lastSym = -1;
            for (int i = 0; i < lengths.Length; i++)
            {
                if (lengths[i] > 15) return null;
                if (lengths[i] > maxLen) maxLen = lengths[i];
                if (lengths[i] != 0) { nonZero++; lastSym = i; }
            }
            if (nonZero == 0) return null;
            if (nonZero == 1) return new HuffmanTree(Array.Empty<int>(), 0, lengths.Length, lastSym);

            // Canonical Huffman: count by length, assign first codes per length.
            var blCount = new int[maxLen + 1];
            for (int i = 0; i < lengths.Length; i++) blCount[lengths[i]]++;
            blCount[0] = 0;

            var nextCode = new int[maxLen + 2];
            int code = 0;
            for (int b = 1; b <= maxLen; b++)
            {
                code = (code + blCount[b - 1]) << 1;
                nextCode[b] = code;
            }
            // Validate the tree is full: sum of 2^(maxLen - len) == 2^maxLen.
            long total = 0;
            for (int b = 1; b <= maxLen; b++) total += (long)blCount[b] << (maxLen - b);
            if (total != (1L << maxLen)) return null;

            // Tree storage: enough internal nodes for any binary tree with N leaves = N - 1.
            // We allocate generously to avoid bound checks.
            int cap = 2 * nonZero + 1;
            var tree = new int[cap * 2];     // each "node" occupies 2 slots: left, right; leaf encoded as -(symbol+1)
            for (int i = 0; i < tree.Length; i++) tree[i] = 0;
            int nodes = 1;                   // node 0 = root, slots 0..1

            for (int sym = 0; sym < lengths.Length; sym++)
            {
                int len = lengths[sym];
                if (len == 0) continue;
                int c = nextCode[len]++;
                int n = 0;                   // start at root
                for (int b = len - 1; b >= 0; b--)
                {
                    int bit = (c >> b) & 1;
                    int slot = n * 2 + bit;
                    if (b == 0)
                    {
                        if (tree[slot] != 0) return null;
                        tree[slot] = -(sym + 1);   // leaf
                    }
                    else
                    {
                        if (tree[slot] == 0)
                        {
                            tree[slot] = nodes;
                            if (nodes >= cap) return null;
                            nodes++;
                        }
                        else if (tree[slot] < 0)
                        {
                            return null;            // would overwrite a leaf
                        }
                        n = tree[slot];
                    }
                }
            }

            return new HuffmanTree(tree, nodes, lengths.Length, -1);
        }

        public int ReadSymbol(BitReader br)
        {
            if (Single >= 0) return Single;
            int n = 0;
            while (true)
            {
                int b = br.ReadBits(1);
                if (br.Failed) return 0;
                int slot = n * 2 + b;
                int v = _tree[slot];
                if (v < 0) return -v - 1;
                if (v == 0) { br.Failed = true; return 0; }
                n = v;
            }
        }
    }

    // =========================================================================
    // Code-length transmission per VP8L spec.
    //
    // Two modes: "simple" (1 or 2 symbols hand-coded) and "normal"
    // (transmit code lengths via a fixed-permutation alphabet of 19,
    // DEFLATE-style).
    // =========================================================================

    private static readonly int[] CodeLengthCodeOrder = {
        17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
    };

    internal static HuffmanTree? ReadHuffmanTree(BitReader br, int alphabetSize)
    {
        int simple = br.ReadBits(1);
        if (simple == 1)
        {
            int numSymbols = br.ReadBits(1) + 1;
            int firstSymbolLenCode = br.ReadBits(1);
            int sym0 = br.ReadBits(firstSymbolLenCode == 0 ? 1 : 8);
            if (sym0 >= alphabetSize) { br.Failed = true; return null; }
            if (numSymbols == 1)
            {
                var lens = new int[alphabetSize];
                lens[sym0] = 1;          // single-leaf — Single shortcut applies
                return HuffmanTree.FromLengths(lens);
            }
            int sym1 = br.ReadBits(8);
            if (sym1 >= alphabetSize) { br.Failed = true; return null; }
            // Spec assigns bit '0' to sym0 and bit '1' to sym1 regardless
            // of numeric order — explicit two-symbol tree preserves that.
            return HuffmanTree.TwoSymbol(sym0, sym1, alphabetSize);
        }

        // Normal mode.
        int numCodeLenCodes = 4 + br.ReadBits(4);
        if (numCodeLenCodes > 19) { br.Failed = true; return null; }
        var clLens = new int[19];
        for (int i = 0; i < numCodeLenCodes; i++)
            clLens[CodeLengthCodeOrder[i]] = br.ReadBits(3);
        var clTree = HuffmanTree.FromLengths(clLens);
        if (clTree == null) return null;

        int useLength = br.ReadBits(1);
        int maxSym;
        if (useLength == 1)
        {
            int lenNbits = 2 + 2 * br.ReadBits(3);
            maxSym = 2 + br.ReadBits(lenNbits);
            if (maxSym > alphabetSize) { br.Failed = true; return null; }
        }
        else
        {
            maxSym = alphabetSize;
        }

        var symLens = new int[alphabetSize];
        int sym = 0, prevLen = 8;
        while (sym < alphabetSize)
        {
            if (useLength == 1 && sym >= maxSym) break;
            int code = clTree.ReadSymbol(br);
            if (br.Failed) return null;
            if (code < 16)
            {
                symLens[sym++] = code;
                if (code != 0) prevLen = code;
            }
            else
            {
                int repeat;
                int repeatVal;
                if (code == 16)        { repeat = 3 + br.ReadBits(2);  repeatVal = prevLen; }
                else if (code == 17)   { repeat = 3 + br.ReadBits(3);  repeatVal = 0; }
                else /* 18 */          { repeat = 11 + br.ReadBits(7); repeatVal = 0; }
                for (int k = 0; k < repeat && sym < alphabetSize; k++)
                    symLens[sym++] = repeatVal;
            }
        }

        return HuffmanTree.FromLengths(symLens);
    }

    // =========================================================================
    // Spatial decoder — main image / sub-image decode loop.
    // =========================================================================

    private static readonly (sbyte X, sbyte Y)[] DistanceMap = BuildDistanceMap();
    private static (sbyte, sbyte)[] BuildDistanceMap()
    {
        // VP8L's 120-entry near-neighbor distance map (spec, table 5).
        int[] xy = {
            24, 7,  23, 7,  25, 7,  22, 7,  26, 7,  21, 7,  27, 7,  20, 7,  28, 7,  19, 7,
            29, 7,  18, 7,  30, 7,  17, 7,  31, 7,  16, 7,  32, 7,  15, 7,  33, 7,  14, 7,
            34, 7,  13, 7,  35, 7,  12, 7,  36, 7,  11, 7,  37, 7,  10, 7,  38, 7,   9, 7,
            39, 7,   8, 7,  40, 7,   7, 7,  41, 7,   6, 7,  42, 7,   5, 7,  43, 7,   4, 7,
            44, 7,   3, 7,  45, 7,   2, 7,  46, 7,   1, 7,  47, 7,
            // Trailing entries derived from the algorithmic table; for compactness
            // we generate the rest below.
        };
        // The spec's table is not actually a simple polynomial — it's hand-tuned.
        // Pull from the reference ordering used by libwebp's kCodeToPlane[120].
        // For correctness, use the libwebp-known table directly:
        sbyte[][] table = new sbyte[][] {
            new sbyte[]{0,1},  new sbyte[]{1,0},  new sbyte[]{1,1},  new sbyte[]{-1,1}, new sbyte[]{0,2},
            new sbyte[]{2,0},  new sbyte[]{1,2},  new sbyte[]{-1,2}, new sbyte[]{2,1},  new sbyte[]{-2,1},
            new sbyte[]{2,2},  new sbyte[]{-2,2}, new sbyte[]{0,3},  new sbyte[]{3,0},  new sbyte[]{1,3},
            new sbyte[]{-1,3}, new sbyte[]{3,1},  new sbyte[]{-3,1}, new sbyte[]{2,3},  new sbyte[]{-2,3},
            new sbyte[]{3,2},  new sbyte[]{-3,2}, new sbyte[]{0,4},  new sbyte[]{4,0},  new sbyte[]{1,4},
            new sbyte[]{-1,4}, new sbyte[]{4,1},  new sbyte[]{-4,1}, new sbyte[]{3,3},  new sbyte[]{-3,3},
            new sbyte[]{2,4},  new sbyte[]{-2,4}, new sbyte[]{4,2},  new sbyte[]{-4,2}, new sbyte[]{0,5},
            new sbyte[]{3,4},  new sbyte[]{-3,4}, new sbyte[]{4,3},  new sbyte[]{-4,3}, new sbyte[]{5,0},
            new sbyte[]{1,5},  new sbyte[]{-1,5}, new sbyte[]{5,1},  new sbyte[]{-5,1}, new sbyte[]{2,5},
            new sbyte[]{-2,5}, new sbyte[]{5,2},  new sbyte[]{-5,2}, new sbyte[]{4,4},  new sbyte[]{-4,4},
            new sbyte[]{3,5},  new sbyte[]{-3,5}, new sbyte[]{5,3},  new sbyte[]{-5,3}, new sbyte[]{0,6},
            new sbyte[]{6,0},  new sbyte[]{1,6},  new sbyte[]{-1,6}, new sbyte[]{6,1},  new sbyte[]{-6,1},
            new sbyte[]{2,6},  new sbyte[]{-2,6}, new sbyte[]{6,2},  new sbyte[]{-6,2}, new sbyte[]{4,5},
            new sbyte[]{-4,5}, new sbyte[]{5,4},  new sbyte[]{-5,4}, new sbyte[]{3,6},  new sbyte[]{-3,6},
            new sbyte[]{6,3},  new sbyte[]{-6,3}, new sbyte[]{0,7},  new sbyte[]{7,0},  new sbyte[]{1,7},
            new sbyte[]{-1,7}, new sbyte[]{5,5},  new sbyte[]{-5,5}, new sbyte[]{7,1},  new sbyte[]{-7,1},
            new sbyte[]{4,6},  new sbyte[]{-4,6}, new sbyte[]{6,4},  new sbyte[]{-6,4}, new sbyte[]{2,7},
            new sbyte[]{-2,7}, new sbyte[]{7,2},  new sbyte[]{-7,2}, new sbyte[]{3,7},  new sbyte[]{-3,7},
            new sbyte[]{7,3},  new sbyte[]{-7,3}, new sbyte[]{5,6},  new sbyte[]{-5,6}, new sbyte[]{6,5},
            new sbyte[]{-6,5}, new sbyte[]{8,0},  new sbyte[]{4,7},  new sbyte[]{-4,7}, new sbyte[]{7,4},
            new sbyte[]{-7,4}, new sbyte[]{8,1},  new sbyte[]{8,2},  new sbyte[]{6,6},  new sbyte[]{-6,6},
            new sbyte[]{8,3},  new sbyte[]{5,7},  new sbyte[]{-5,7}, new sbyte[]{7,5},  new sbyte[]{-7,5},
            new sbyte[]{8,4},  new sbyte[]{6,7},  new sbyte[]{-6,7}, new sbyte[]{7,6},  new sbyte[]{-7,6},
            new sbyte[]{8,5},  new sbyte[]{7,7},  new sbyte[]{-7,7}, new sbyte[]{8,6},  new sbyte[]{8,7},
        };
        var arr = new (sbyte X, sbyte Y)[120];
        for (int i = 0; i < 120; i++) arr[i] = (table[i][0], table[i][1]);
        return arr;
    }

    /// <summary>Decode a length/distance prefix code into an actual count using the WebP variable-bits scheme.</summary>
    internal static int PrefixToValue(int prefixCode, BitReader br)
    {
        if (prefixCode < 4) return prefixCode + 1;
        int extraBits = (prefixCode - 2) >> 1;
        int offset = (2 + (prefixCode & 1)) << extraBits;
        int extra = br.ReadBits(extraBits);
        return offset + extra + 1;
    }

    internal static class SpatialDecoder
    {
        public static uint[]? Decode(BitReader br, int width, int height, bool topLevel = false)
        {
            int colorCacheBits = 0;
            uint[]? colorCache = null;
            int colorCacheSize = 0;
            if (br.ReadBits(1) == 1)
            {
                colorCacheBits = br.ReadBits(4);
                if (colorCacheBits < 1 || colorCacheBits > 11) { br.Failed = true; return null; }
                colorCacheSize = 1 << colorCacheBits;
                colorCache = new uint[colorCacheSize];
            }

            // Meta-Huffman selector: when set, the image is partitioned into
            // tile blocks each indexing a separate prefix-code group. We
            // currently support this via a fully populated meta-image read.
            int numHuffmanGroups = 1;
            int[]? huffmanXIdx = null;
            int huffmanBits = 0;
            int huffmanWidth = 0;
            // Meta-Huffman is only signaled at top level — sub-image decodes
            // (transforms, palette, meta-image itself) skip this flag entirely.
            if (topLevel && br.ReadBits(1) == 1)
            {
                huffmanBits = 2 + br.ReadBits(3);
                huffmanWidth = (width + (1 << huffmanBits) - 1) >> huffmanBits;
                int huffmanHeight = (height + (1 << huffmanBits) - 1) >> huffmanBits;
                var meta = Decode(br, huffmanWidth, huffmanHeight);
                if (meta == null) return null;
                // Top byte (red) is unused; group index = (R << 8) | G.
                huffmanXIdx = new int[meta.Length];
                int max = 0;
                for (int i = 0; i < meta.Length; i++)
                {
                    int g = (int)((meta[i] >> 16) & 0xFF) << 8 | (int)((meta[i] >> 8) & 0xFF);
                    huffmanXIdx[i] = g;
                    if (g > max) max = g;
                }
                numHuffmanGroups = max + 1;
            }

            // Read all prefix-code groups.
            var groups = new HuffmanTree[numHuffmanGroups][];
            for (int g = 0; g < numHuffmanGroups; g++)
            {
                groups[g] = new HuffmanTree[5];
                groups[g][0] = ReadHuffmanTree(br, 256 + 24 + colorCacheSize)!;
                groups[g][1] = ReadHuffmanTree(br, 256)!;
                groups[g][2] = ReadHuffmanTree(br, 256)!;
                groups[g][3] = ReadHuffmanTree(br, 256)!;
                groups[g][4] = ReadHuffmanTree(br, 40)!;
                for (int i = 0; i < 5; i++)
                    if (groups[g][i] == null || br.Failed) return null;
            }

            var pixels = new uint[width * height];
            int x = 0, y = 0;
            int total = pixels.Length;
            int idx = 0;
            int huffmanMask = (1 << huffmanBits) - 1;

            while (idx < total)
            {
                // Resolve current Huffman group for (x, y).
                HuffmanTree[] g;
                if (huffmanXIdx != null)
                {
                    int hx = x >> huffmanBits;
                    int hy = y >> huffmanBits;
                    int gi = huffmanXIdx[hy * huffmanWidth + hx];
                    if (gi < 0 || gi >= numHuffmanGroups) return null;
                    g = groups[gi];
                }
                else g = groups[0];

                int sym = g[0].ReadSymbol(br);
                if (br.Failed) return null;

                if (sym < 256)
                {
                    // Literal pixel: G then R, B, A.
                    int green = sym;
                    int red = g[1].ReadSymbol(br);
                    int blue = g[2].ReadSymbol(br);
                    int alpha = g[3].ReadSymbol(br);
                    if (br.Failed) return null;
                    uint argb = ((uint)alpha << 24) | ((uint)red << 16) | ((uint)green << 8) | (uint)blue;
                    pixels[idx++] = argb;
                    if (colorCache != null) StoreCache(colorCache, colorCacheBits, argb);
                    if (++x == width) { x = 0; y++; }
                }
                else if (sym < 256 + 24)
                {
                    // LZ77: length code, then distance.
                    int lengthCode = sym - 256;
                    int length = PrefixToValue(lengthCode, br);
                    int distSym = g[4].ReadSymbol(br);
                    if (br.Failed) return null;
                    int distCode = PrefixToValue(distSym, br);
                    int distance;
                    if (distCode > 120) distance = distCode - 120;
                    else
                    {
                        var (dx, dy) = DistanceMap[distCode - 1];
                        distance = dx + dy * width;
                        if (distance < 1) distance = 1;
                    }

                    if (idx + length > total) return null;
                    int srcIdx = idx - distance;
                    if (srcIdx < 0) return null;
                    for (int k = 0; k < length; k++)
                    {
                        uint v = pixels[srcIdx + k];
                        pixels[idx++] = v;
                        if (colorCache != null) StoreCache(colorCache, colorCacheBits, v);
                    }
                    x += length;
                    while (x >= width) { x -= width; y++; }
                }
                else
                {
                    // Color cache lookup.
                    int cacheIdx = sym - (256 + 24);
                    if (colorCache == null || cacheIdx >= colorCacheSize) return null;
                    uint argb = colorCache[cacheIdx];
                    pixels[idx++] = argb;
                    if (++x == width) { x = 0; y++; }
                }
            }

            return pixels;
        }

        private static void StoreCache(uint[] cache, int bits, uint argb)
        {
            uint key = unchecked(argb * 0x1E35A7BDu) >> (32 - bits);
            cache[key] = argb;
        }
    }

    // =========================================================================
    // Transforms — read params from bitstream, apply inverse to pixel buffer.
    // =========================================================================

    internal abstract class Transform
    {
        public static Transform? Read(int type, ref int xSize, int height, BitReader br)
        {
            return type switch
            {
                0 => PredictorTransform.ReadIt(xSize, height, br),
                1 => ColorTransform.ReadIt(xSize, height, br),
                2 => new SubtractGreenTransform(),
                3 => ColorIndexingTransform.ReadIt(ref xSize, br),
                _ => null,
            };
        }
        public abstract uint[] Apply(uint[] pixels, ref int xSize, int height);
    }

    internal sealed class SubtractGreenTransform : Transform
    {
        public override uint[] Apply(uint[] pixels, ref int xSize, int height)
        {
            for (int i = 0; i < pixels.Length; i++)
            {
                uint p = pixels[i];
                int g = (int)((p >> 8) & 0xFF);
                int r = ((int)((p >> 16) & 0xFF) + g) & 0xFF;
                int b = ((int)(p & 0xFF) + g) & 0xFF;
                pixels[i] = (p & 0xFF00FF00u) | ((uint)r << 16) | (uint)b;
            }
            return pixels;
        }
    }

    internal sealed class ColorIndexingTransform : Transform
    {
        private readonly uint[] _palette;
        private readonly int _origWidth;
        private readonly int _bundleBits;
        private ColorIndexingTransform(uint[] palette, int origWidth, int bundleBits)
        {
            _palette = palette; _origWidth = origWidth; _bundleBits = bundleBits;
        }

        public static ColorIndexingTransform? ReadIt(ref int xSize, BitReader br)
        {
            int colorTableSize = br.ReadBits(8) + 1;
            // Palette is itself a stored sub-image: 1 row × colorTableSize pixels.
            var palette = SpatialDecoder.Decode(br, colorTableSize, 1);
            if (palette == null) return null;
            // Cumulative-XOR un-delta the palette per spec.
            for (int i = 1; i < colorTableSize; i++)
            {
                uint prev = palette[i - 1];
                uint cur = palette[i];
                palette[i] = ((((cur >> 24) + (prev >> 24)) & 0xFF) << 24)
                           | ((((cur >> 16) + (prev >> 16)) & 0xFF) << 16)
                           | ((((cur >> 8) + (prev >> 8)) & 0xFF) << 8)
                           | (((cur + prev) & 0xFF));
            }

            int bundleBits = colorTableSize <= 2 ? 3
                          : colorTableSize <= 4 ? 2
                          : colorTableSize <= 16 ? 1
                          : 0;
            int origWidth = xSize;
            int newWidth = (xSize + (1 << bundleBits) - 1) >> bundleBits;
            xSize = newWidth;
            return new ColorIndexingTransform(palette, origWidth, bundleBits);
        }

        public override uint[] Apply(uint[] pixels, ref int xSize, int height)
        {
            int packed = xSize;
            xSize = _origWidth;
            int width = _origWidth;
            int paletteSize = _palette.Length;
            int bundleBits = _bundleBits;
            int pixelsPerByte = 1 << bundleBits;
            int bitsPerPixel = 8 >> bundleBits;
            int mask = paletteSize - 1;
            // Spec: palette indices may be any 8-bit value, only when
            // pixelsPerByte > 1 do we bit-pack; mask is calculated from
            // pixelsPerByte not paletteSize.
            if (pixelsPerByte > 1) mask = (1 << bitsPerPixel) - 1;

            var dst = new uint[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int xb = 0; xb < packed; xb++)
                {
                    uint v = pixels[y * packed + xb];
                    int idx = (int)((v >> 8) & 0xFF);
                    for (int k = 0; k < pixelsPerByte; k++)
                    {
                        int x = xb * pixelsPerByte + k;
                        if (x >= width) break;
                        int paletteIdx = pixelsPerByte == 1 ? idx : ((idx >> (k * bitsPerPixel)) & mask);
                        if (paletteIdx >= paletteSize) paletteIdx = 0;
                        dst[y * width + x] = _palette[paletteIdx];
                    }
                }
            }
            return dst;
        }
    }

    internal sealed class ColorTransform : Transform
    {
        private readonly uint[] _data;
        private readonly int _bits;
        private readonly int _blockWidth;
        private readonly int _blockHeight;

        private ColorTransform(uint[] data, int bits, int blockWidth, int blockHeight)
        {
            _data = data; _bits = bits; _blockWidth = blockWidth; _blockHeight = blockHeight;
        }

        public static ColorTransform? ReadIt(int xSize, int height, BitReader br)
        {
            int bits = 2 + br.ReadBits(3);
            int blockWidth = (xSize + (1 << bits) - 1) >> bits;
            int blockHeight = (height + (1 << bits) - 1) >> bits;
            var data = SpatialDecoder.Decode(br, blockWidth, blockHeight);
            if (data == null) return null;
            return new ColorTransform(data, bits, blockWidth, blockHeight);
        }

        public override uint[] Apply(uint[] pixels, ref int xSize, int height)
        {
            int width = xSize;
            for (int y = 0; y < height; y++)
            {
                int blockY = y >> _bits;
                for (int x = 0; x < width; x++)
                {
                    int blockX = x >> _bits;
                    uint cte = _data[blockY * _blockWidth + blockX];
                    int g2r = (sbyte)(cte & 0xFF);
                    int g2b = (sbyte)((cte >> 8) & 0xFF);
                    int r2b = (sbyte)((cte >> 16) & 0xFF);

                    uint p = pixels[y * width + x];
                    int g = (int)((p >> 8) & 0xFF);
                    int r = (int)((p >> 16) & 0xFF);
                    int b = (int)(p & 0xFF);
                    int rNew = (r + ColorTransformDelta(g2r, (sbyte)g)) & 0xFF;
                    int bNew = (b + ColorTransformDelta(g2b, (sbyte)g) + ColorTransformDelta(r2b, (sbyte)rNew)) & 0xFF;
                    pixels[y * width + x] = (p & 0xFF00FF00u) | ((uint)rNew << 16) | (uint)bNew;
                }
            }
            return pixels;
        }

        private static int ColorTransformDelta(int t, int c) => (t * c) >> 5;
    }

    internal sealed class PredictorTransform : Transform
    {
        private readonly uint[] _data;
        private readonly int _bits;
        private readonly int _blockWidth;

        private PredictorTransform(uint[] data, int bits, int blockWidth)
        {
            _data = data; _bits = bits; _blockWidth = blockWidth;
        }

        public static PredictorTransform? ReadIt(int xSize, int height, BitReader br)
        {
            int bits = 2 + br.ReadBits(3);
            int blockWidth = (xSize + (1 << bits) - 1) >> bits;
            int blockHeight = (height + (1 << bits) - 1) >> bits;
            var data = SpatialDecoder.Decode(br, blockWidth, blockHeight);
            if (data == null) return null;
            return new PredictorTransform(data, bits, blockWidth);
        }

        public override uint[] Apply(uint[] pixels, ref int xSize, int height)
        {
            int width = xSize;
            // Top-left starts at fixed value 0xff000000 (opaque black) for delta calc.
            // Row 0: predictor 0 forces literal-equality so first row's "predicted"
            // is whatever's stored. We special-case it.
            // For simplicity, follow libwebp's algorithm exactly:
            //   pixel[0,0] is stored relative to opaque black
            //   row 0 (y == 0) for x > 0 uses predictor mode 1 (left)
            //   col 0 (x == 0) for y > 0 uses predictor mode 2 (top)
            //   else uses the predictor mode from the predictor data
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int idx = y * width + x;
                    uint cur = pixels[idx];
                    uint pred;
                    if (x == 0 && y == 0)
                        pred = 0xFF000000u;
                    else if (y == 0)
                        pred = pixels[idx - 1];
                    else if (x == 0)
                        pred = pixels[idx - width];
                    else
                    {
                        int blockX = x >> _bits;
                        int blockY = y >> _bits;
                        int mode = (int)((_data[blockY * _blockWidth + blockX] >> 8) & 0x0F);
                        pred = Predict(mode, pixels, x, y, width);
                    }
                    pixels[idx] = AddArgb(cur, pred);
                }
            }
            return pixels;
        }

        private static uint AddArgb(uint a, uint b)
        {
            uint a0 = (a + b) & 0xFF;
            uint a1 = ((((a >> 8) & 0xFF) + ((b >> 8) & 0xFF)) & 0xFF) << 8;
            uint a2 = ((((a >> 16) & 0xFF) + ((b >> 16) & 0xFF)) & 0xFF) << 16;
            uint a3 = ((((a >> 24) & 0xFF) + ((b >> 24) & 0xFF)) & 0xFF) << 24;
            return a0 | a1 | a2 | a3;
        }

        private static uint Predict(int mode, uint[] px, int x, int y, int w)
        {
            int idx = y * w + x;
            uint L = px[idx - 1];
            uint T = px[idx - w];
            uint TL = px[idx - w - 1];
            uint TR = x + 1 < w ? px[idx - w + 1] : T;
            switch (mode)
            {
                case 0: return 0xFF000000u;
                case 1: return L;
                case 2: return T;
                case 3: return TR;
                case 4: return TL;
                case 5: return Average2(Average2(L, TR), T);
                case 6: return Average2(L, TL);
                case 7: return Average2(L, T);
                case 8: return Average2(TL, T);
                case 9: return Average2(T, TR);
                case 10: return Average2(Average2(L, TL), Average2(T, TR));
                case 11: return Select(L, T, TL);
                case 12: return ClampAddSubFull(L, T, TL);
                case 13: return ClampAddSubHalf(Average2(L, T), TL);
                default: return 0xFF000000u;
            }
        }

        private static uint Average2(uint a, uint b)
        {
            uint r0 = ((a & 0xFF) + (b & 0xFF)) >> 1;
            uint r1 = (((a >> 8) & 0xFF) + ((b >> 8) & 0xFF)) >> 1 << 8;
            uint r2 = (((a >> 16) & 0xFF) + ((b >> 16) & 0xFF)) >> 1 << 16;
            uint r3 = (((a >> 24) & 0xFF) + ((b >> 24) & 0xFF)) >> 1 << 24;
            return r0 | r1 | r2 | r3;
        }

        private static uint Select(uint l, uint t, uint tl)
        {
            int pa = AbsDiffSum(t, tl);
            int pb = AbsDiffSum(l, tl);
            return pa <= pb ? l : t;
        }

        private static int AbsDiffSum(uint a, uint b)
        {
            int s = 0;
            for (int sh = 0; sh < 32; sh += 8)
                s += Math.Abs((int)((a >> sh) & 0xFF) - (int)((b >> sh) & 0xFF));
            return s;
        }

        private static uint ClampAddSubFull(uint a, uint b, uint c)
        {
            uint r0 = (uint)Clamp((int)(a & 0xFF) + (int)(b & 0xFF) - (int)(c & 0xFF));
            uint r1 = (uint)Clamp((int)((a >> 8) & 0xFF) + (int)((b >> 8) & 0xFF) - (int)((c >> 8) & 0xFF)) << 8;
            uint r2 = (uint)Clamp((int)((a >> 16) & 0xFF) + (int)((b >> 16) & 0xFF) - (int)((c >> 16) & 0xFF)) << 16;
            uint r3 = (uint)Clamp((int)((a >> 24) & 0xFF) + (int)((b >> 24) & 0xFF) - (int)((c >> 24) & 0xFF)) << 24;
            return r0 | r1 | r2 | r3;
        }

        private static uint ClampAddSubHalf(uint a, uint b)
        {
            uint r0 = (uint)Clamp((int)(a & 0xFF) + (((int)(a & 0xFF) - (int)(b & 0xFF)) >> 1));
            uint r1 = (uint)Clamp((int)((a >> 8) & 0xFF) + (((int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF)) >> 1)) << 8;
            uint r2 = (uint)Clamp((int)((a >> 16) & 0xFF) + (((int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF)) >> 1)) << 16;
            uint r3 = (uint)Clamp((int)((a >> 24) & 0xFF) + (((int)((a >> 24) & 0xFF) - (int)((b >> 24) & 0xFF)) >> 1)) << 24;
            return r0 | r1 | r2 | r3;
        }

        private static int Clamp(int v) => v < 0 ? 0 : v > 255 ? 255 : v;
    }
}
