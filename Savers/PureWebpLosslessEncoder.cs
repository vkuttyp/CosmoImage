using System;
using System.Collections.Generic;
using CosmoImage.Core;

namespace CosmoImage.Savers;

/// <summary>
/// Pure-managed WebP VP8L (lossless) encoder. Produces a complete RIFF+VP8L
/// byte stream the existing <c>PureWebpLossless</c> decoder (and any spec
/// VP8L decoder) can read back losslessly. No native deps.
///
/// <para><b>Phase 2 implementation</b> (this revision):
/// <list type="bullet">
///   <item>SubtractGreen transform — encoder pre-pass subtracts the green
///   byte from R and B before histogramming; the decoder reverses it.
///   Reduces entropy noticeably on natural-image content with no cost.</item>
///   <item>DEFLATE-style repeat opcodes 16/17/18 in Huffman-tree
///   transmission — compresses runs of identical / zero code lengths in
///   the per-symbol length stream. Significant header savings on sparse
///   alphabets (the common case where most symbols are unused).</item>
/// </list>
/// Still missing for parity with libwebp: LZ77 backreferences, color
/// cache, Predictor/Color/ColorIndexing transforms, proper boundary
/// package-merge length-limiting, meta-Huffman grouping. Tracked under
/// task #19 follow-ons.</para>
///
/// <para>Input is RGBA bytes; alpha may be 255 (opaque). The encoder writes
/// the standard ARGB layout internally (G channel as the "main" symbol,
/// then R, B, A as parallel channels per the VP8L spec).</para>
/// </summary>
internal static class PureWebpLosslessEncoder
{
    /// <summary>
    /// Encode RGBA pixels (4 bytes per pixel, row-major) as a complete
    /// WebP VP8L file (RIFF wrapper included). The returned bytes can be
    /// written directly to disk or wrapped in a metadata mux.
    /// </summary>
    public static byte[] Encode(byte[] rgba, int width, int height)
    {
        if (rgba == null) throw new ArgumentNullException(nameof(rgba));
        if (width <= 0 || height <= 0) throw new ArgumentException("non-positive dimension");
        if (width > 16384 || height > 16384) throw new ArgumentException("VP8L max dimension is 16384");
        if (rgba.Length != width * height * 4) throw new ArgumentException("rgba length mismatch");

        // 1) Build the VP8L bitstream body.
        var bw = new BitWriter();
        WriteVp8lBody(bw, rgba, width, height);
        byte[] vp8lPayload = bw.ToArray();

        // 2) Wrap in RIFF.
        return WrapRiff(vp8lPayload);
    }

    /// <summary>RGBA → ARGB uint32 (matches decoder's internal layout).</summary>
    private static uint[] ToArgb(byte[] rgba, int width, int height)
    {
        var argb = new uint[width * height];
        for (int i = 0; i < argb.Length; i++)
        {
            int j = i * 4;
            argb[i] = ((uint)rgba[j + 3] << 24) | ((uint)rgba[j] << 16) | ((uint)rgba[j + 1] << 8) | rgba[j + 2];
        }
        return argb;
    }

    private static void WriteVp8lBody(BitWriter bw, byte[] rgba, int width, int height)
    {
        var argb = ToArgb(rgba, width, height);
        bool alphaUsed = false;
        for (int i = 0; i < argb.Length && !alphaUsed; i++)
            if ((argb[i] >> 24) != 0xFF) alphaUsed = true;

        // --- Header per VP8L spec ---
        bw.WriteBits(0x2F, 8);            // signature
        bw.WriteBits(width - 1, 14);
        bw.WriteBits(height - 1, 14);
        bw.WriteBits(alphaUsed ? 1 : 0, 1);
        bw.WriteBits(0, 3);                // version

        // --- Choose transform path ---
        // ColorIndexing (palette) when the image has ≤ 256 distinct colours.
        // Big win on synthetic / UI / palette content (4-colour stripes,
        // line-art, chart output): one G-channel index per pixel — possibly
        // bundled 2/4/8 per byte — instead of full ARGB. We skip
        // SubtractGreen on this path because the packed image has R=B=A=0
        // by construction; subtracting G would only inject entropy.
        if (TryBuildPalette(argb, out uint[]? palette))
        {
            WritePaletteTransformedBody(bw, argb, width, height, palette!);
            return;
        }

        // --- Non-palette path: ColorTransform (optional) → SubtractGreen → body.
        //
        // Decode order is reverse-of-write, so writing [ColorTransform,
        // SubtractGreen] means the decoder applies SubtractGreen first (R←R+G,
        // B←B+G) and then ColorTransform (adds the (g2r, g2b, r2b) delta
        // contributions back to R and B). Encoder forward order must mirror
        // this: ColorTransform first (subtracting the deltas), THEN
        // SubtractGreen.
        //
        // ColorTransform is gated on image area — for small images the
        // ~150-bit header (transform marker + 5-tree sub-image) exceeds the
        // savings from improved channel correlation.
        bool useColorTransform = TryFitColorTransform(argb, width, height,
            out int g2r, out int g2b, out int r2b);
        if (useColorTransform)
        {
            WriteColorTransform(bw, argb, width, height, g2r, g2b, r2b);
        }

        bw.WriteBits(1, 1);                // transform_present = 1
        bw.WriteBits(2, 2);                // transform_type = 2 (SubtractGreen) — no params
        ApplySubtractGreenForward(argb);

        bw.WriteBits(0, 1);                // transform_present = 0 → main image next
        WriteSpatialBodyLz77(bw, argb, width, height);
    }

    // ---------- ColorTransform (transform_type = 1) ----------

    /// <summary>
    /// Block size for ColorTransform meta-image: <c>bits = 4</c> means each
    /// block covers a 16×16 pixel region. Trade-off: smaller blocks give
    /// per-region triples (better fit for varying content) but a bigger
    /// meta-image; larger blocks amortise header cost.
    /// </summary>
    private const int ColorTransformBits = 4;

    /// <summary>
    /// Minimum pixel area below which we skip ColorTransform — the header
    /// (transform marker + 5 Huffman trees for the meta-image) costs roughly
    /// 150 bits, so we only emit when the savings can plausibly exceed it.
    /// </summary>
    private const int ColorTransformMinArea = 64 * 64;

    /// <summary>
    /// Coarse search for a single global <c>(g2r, g2b, r2b)</c> triple that
    /// minimises Σ|r' − 128| + |b' − 128| (a simple L1 entropy proxy that
    /// rewards values clustered near 128 — those compress to short Huffman
    /// codes in the literal alphabet). Returns false when the win is too
    /// small to overcome the ~150-bit header cost.
    /// </summary>
    private static bool TryFitColorTransform(uint[] argb, int width, int height,
                                             out int g2r, out int g2b, out int r2b)
    {
        g2r = g2b = r2b = 0;
        if (width * height < ColorTransformMinArea) return false;

        // Compute baseline L1 cost of (r − 128) + (b − 128) without CT.
        long baseline = 0;
        for (int i = 0; i < argb.Length; i++)
        {
            uint p = argb[i];
            baseline += Math.Abs((int)((p >> 16) & 0xFF) - 128);
            baseline += Math.Abs((int)(p & 0xFF) - 128);
        }

        // Search over a coarse signed grid. {-96, -64, -32, -16, 0, 16, 32, 64,
        // 96} per axis = 9 values; 9³ = 729 evaluations of the full image.
        // L1 evaluation is O(N), so total work O(729·N) = ~2.4M for 64×64.
        ReadOnlySpan<int> grid = stackalloc int[] { -96, -64, -32, -16, 0, 16, 32, 64, 96 };
        long bestCost = baseline;
        int bestR = 0, bestGB = 0, bestRB = 0;
        foreach (int gr in grid)
        foreach (int gb in grid)
        foreach (int rb in grid)
        {
            long cost = 0;
            for (int i = 0; i < argb.Length; i++)
            {
                uint p = argb[i];
                int g = (int)((p >> 8) & 0xFF);
                int r = (int)((p >> 16) & 0xFF);
                int b = (int)(p & 0xFF);
                int gSigned = (sbyte)g;
                int rOrigSigned = (sbyte)r;
                int rStored = (r - ((gr * gSigned) >> 5)) & 0xFF;
                int bStored = (b - ((gb * gSigned) >> 5) - ((rb * rOrigSigned) >> 5)) & 0xFF;
                cost += Math.Abs(rStored - 128);
                cost += Math.Abs(bStored - 128);
                // Early-cut: once we exceed the current best, abandon this triple.
                if (cost >= bestCost) break;
            }
            if (cost < bestCost)
            {
                bestCost = cost;
                bestR = gr; bestGB = gb; bestRB = rb;
            }
        }

        // Need meaningful win to justify the header cost. Threshold here is
        // ~75 L1 units per "pixel-of-image-area" of header cost, scaled by
        // 2 (since we measure both r and b). Coarse but defensive: declines
        // CT on inputs where it can't help (random pixels, single-channel,
        // very small images).
        long headerProxy = 150L * 1;   // ~150 bits proxy at ~1 L1-unit/bit
        if (baseline - bestCost < headerProxy) return false;

        g2r = bestR; g2b = bestGB; r2b = bestRB;
        return true;
    }

    /// <summary>
    /// Emit a ColorTransform transform header and meta-image, then apply
    /// the chosen triple forward to <paramref name="argb"/> in-place. All
    /// blocks share the global triple — the meta-image is constant-pixel
    /// and compresses to ~1 bit per pixel after Huffman.
    /// </summary>
    private static void WriteColorTransform(BitWriter bw, uint[] argb, int width, int height,
                                            int g2r, int g2b, int r2b)
    {
        // 1) Transform marker.
        bw.WriteBits(1, 1);                // transform_present = 1
        bw.WriteBits(1, 2);                // transform_type = 1 (ColorTransform)
        bw.WriteBits(ColorTransformBits - 2, 3);  // spec: bits = 2 + read(3)

        // 2) Meta-image: blockWidth × blockHeight pixels, each pixel packs
        //    g2r in B, g2b in G, r2b in R, A unused — see
        //    PureWebpLossless.ColorTransform.Apply.
        int blockWidth = (width  + (1 << ColorTransformBits) - 1) >> ColorTransformBits;
        int blockHeight = (height + (1 << ColorTransformBits) - 1) >> ColorTransformBits;
        uint cte = (0xFFu << 24)
                 | ((uint)(byte)r2b << 16)
                 | ((uint)(byte)g2b << 8)
                 | ((uint)(byte)g2r);
        var meta = new uint[blockWidth * blockHeight];
        for (int i = 0; i < meta.Length; i++) meta[i] = cte;
        WriteSpatialBody(bw, meta, blockWidth, blockHeight, topLevel: false);

        // 3) Apply forward to the main image pixels.
        ApplyColorTransformForward(argb, g2r, g2b, r2b);
    }

    /// <summary>
    /// Forward ColorTransform: compute r_stored and b_stored from the
    /// original (r, g, b) using the chosen triple. Mirror of the decoder's
    /// <c>ColorTransform.Apply</c>: encoder subtracts the deltas; decoder
    /// adds them back.
    /// </summary>
    private static void ApplyColorTransformForward(uint[] argb, int g2r, int g2b, int r2b)
    {
        // Decoder inverse (PureWebpLossless.ColorTransform.Apply):
        //   rNew = (r + delta(g2r, (sbyte)g)) mod 256                  ← uses g_signed
        //   bNew = (b + delta(g2b, (sbyte)g) + delta(r2b, (sbyte)rNew)) mod 256
        //                                                              ← uses rNew (= r_original)
        //
        // For the encoder's forward to round-trip we therefore need:
        //   rStored = (r_orig − delta(g2r, (sbyte)g)) mod 256
        //   bStored = (b_orig − delta(g2b, (sbyte)g) − delta(r2b, (sbyte)r_orig)) mod 256
        //
        // The r2b delta uses (sbyte)r_ORIGINAL, NOT (sbyte)rStored — that
        // was a previous-version bug because the decoder's `rNew` is the
        // post-inverse value, which equals r_original.
        for (int i = 0; i < argb.Length; i++)
        {
            uint p = argb[i];
            int g = (int)((p >> 8) & 0xFF);
            int r = (int)((p >> 16) & 0xFF);
            int b = (int)(p & 0xFF);
            int gSigned = (sbyte)g;
            int rOrigSigned = (sbyte)r;
            int rStored = (r - ((g2r * gSigned) >> 5)) & 0xFF;
            int bStored = (b - ((g2b * gSigned) >> 5) - ((r2b * rOrigSigned) >> 5)) & 0xFF;
            argb[i] = (p & 0xFF00FF00u) | ((uint)rStored << 16) | (uint)bStored;
        }
    }

    /// <summary>
    /// Scan the ARGB pixel buffer for distinct colour values. Returns true
    /// (and the sorted palette) iff the image uses at most 256 colours;
    /// false if there are more (caller falls back to the non-palette path).
    /// Sorted order is deterministic — required so delta-coding the palette
    /// is reproducible across encoder runs.
    /// </summary>
    private static bool TryBuildPalette(uint[] argb, out uint[]? palette)
    {
        var set = new HashSet<uint>();
        for (int i = 0; i < argb.Length; i++)
        {
            set.Add(argb[i]);
            if (set.Count > 256) { palette = null; return false; }
        }
        var list = new List<uint>(set);
        list.Sort();
        palette = list.ToArray();
        return true;
    }

    /// <summary>
    /// Write the ColorIndexing transform (palette + packed-index body) into
    /// <paramref name="bw"/>. Spec: VP8L §4 ColorIndexingTransform — the
    /// transform marker is followed by an 8-bit <c>color_table_size - 1</c>,
    /// then the (delta-coded) palette stored as a <c>color_table_size × 1</c>
    /// sub-image, then the main image with reduced width (when the table
    /// fits in ≤ 16 entries the indexes are bundled 2/4/8 per pixel into
    /// the G byte).
    /// </summary>
    private static void WritePaletteTransformedBody(
        BitWriter bw, uint[] argb, int width, int height, uint[] palette)
    {
        // 1) Transform marker + table size.
        bw.WriteBits(1, 1);                // transform_present = 1
        bw.WriteBits(3, 2);                // transform_type = 3 (ColorIndexing)
        int paletteSize = palette.Length;
        bw.WriteBits(paletteSize - 1, 8);  // color_table_size_minus_one

        // 2) Delta-code the palette per spec (cumulative add at decode).
        var deltaPalette = new uint[paletteSize];
        deltaPalette[0] = palette[0];
        for (int i = 1; i < paletteSize; i++)
            deltaPalette[i] = SubtractArgbModulo(palette[i], palette[i - 1]);

        // 3) Palette sub-image: paletteSize × 1, ordinary VP8L spatial body
        //    but at sub-image level (no meta-Huffman flag).
        WriteSpatialBody(bw, deltaPalette, paletteSize, 1, topLevel: false);

        // 4) No further outer transforms — main image follows.
        bw.WriteBits(0, 1);                // transform_present = 0

        // 5) Pack the indexed pixels and emit the main image at reduced
        //    width. Bundle factor per spec:
        //       ≤ 2 colours: 8 px/byte (bundleBits = 3)
        //       ≤ 4 colours: 4 px/byte (bundleBits = 2)
        //       ≤ 16 colours: 2 px/byte (bundleBits = 1)
        //       > 16 colours: 1 px/byte (bundleBits = 0)
        int bundleBits = paletteSize <= 2 ? 3
                       : paletteSize <= 4 ? 2
                       : paletteSize <= 16 ? 1
                       : 0;
        var packed = PackIndexedImage(argb, palette, width, height, bundleBits,
                                      out int packedWidth);
        WriteSpatialBodyLz77(bw, packed, packedWidth, height);
    }

    /// <summary>Component-wise (A, R, G, B) modular subtraction.</summary>
    private static uint SubtractArgbModulo(uint a, uint b)
    {
        uint aA = (((a >> 24) - (b >> 24)) & 0xFF) << 24;
        uint aR = (((a >> 16) - (b >> 16)) & 0xFF) << 16;
        uint aG = (((a >> 8)  - (b >> 8))  & 0xFF) << 8;
        uint aB =  ((a        - b)         & 0xFF);
        return aA | aR | aG | aB;
    }

    /// <summary>
    /// Map every pixel to its palette index and pack <c>bundleBits</c>-worth
    /// of indexes into the G byte of one packed pixel. Output pixels have
    /// R=B=A=0 (the decoder ignores those channels for indexed images, and
    /// constant-zero channels compress optimally — single-symbol Huffman
    /// trees take zero bits per pixel).
    /// </summary>
    private static uint[] PackIndexedImage(uint[] argb, uint[] palette,
                                           int width, int height, int bundleBits,
                                           out int packedWidth)
    {
        int pixelsPerByte = 1 << bundleBits;
        int bitsPerIndex = 8 >> bundleBits;          // 1, 2, 4, or 8
        packedWidth = (width + pixelsPerByte - 1) / pixelsPerByte;

        // Palette is sorted, so build a value→index map.
        var index = new Dictionary<uint, int>(palette.Length);
        for (int i = 0; i < palette.Length; i++) index[palette[i]] = i;

        var packed = new uint[packedWidth * height];
        for (int y = 0; y < height; y++)
        {
            for (int xb = 0; xb < packedWidth; xb++)
            {
                int g = 0;
                for (int k = 0; k < pixelsPerByte; k++)
                {
                    int x = xb * pixelsPerByte + k;
                    if (x >= width) break;            // partial trailing bundle
                    int idx = index[argb[y * width + x]];
                    g |= (idx & ((1 << bitsPerIndex) - 1)) << (k * bitsPerIndex);
                }
                // ARGB layout: A | R | G | B; we set only G, rest stay zero.
                packed[y * packedWidth + xb] = ((uint)g) << 8;
            }
        }
        return packed;
    }

    /// <summary>
    /// Encoder-side SubtractGreen: store (R - G) and (B - G) instead of R and B.
    /// Symmetric with <c>SubtractGreenTransform.Apply</c> in the decoder.
    /// </summary>
    private static void ApplySubtractGreenForward(uint[] pixels)
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            uint p = pixels[i];
            int g = (int)((p >> 8) & 0xFF);
            int r = (int)((p >> 16) & 0xFF);
            int b = (int)(p & 0xFF);
            int rNew = (r - g) & 0xFF;
            int bNew = (b - g) & 0xFF;
            pixels[i] = (p & 0xFF00FF00u) | ((uint)rNew << 16) | (uint)bNew;
        }
    }

    private static void WriteSpatialBody(BitWriter bw, uint[] pixels, int width, int height,
                                         bool topLevel = true)
    {
        // Color cache: 32 entries (5 bits) — good middle ground between
        // hit-rate on natural images (more entries help) and per-pixel
        // alphabet bloat (fewer entries help on unique-color images).
        // Setting to 0 would skip the cache entirely; we always enable it.
        const int CacheBits = 5;
        int cacheSize = 1 << CacheBits;
        bw.WriteBits(1, 1);                // color_cache_present = 1
        bw.WriteBits(CacheBits, 4);        // color_cache_bits (1..11)

        // Meta-Huffman is signaled only at top level. The decoder's
        // SpatialDecoder.Decode reads this flag iff topLevel is true; for
        // sub-image decodes (palette, predictor data, etc.) the bit MUST
        // NOT be present — otherwise the next bit (color_cache_present of
        // the inner image) gets parsed as a meta-Huffman flag instead.
        if (topLevel) bw.WriteBits(0, 1);  // meta_huffman_present = 0

        // G alphabet now includes literals (0..255), LZ77 length codes
        // (256..279 — placeholder for C2b), and cache lookups
        // (256+24..256+24+cacheSize-1).
        int alphabetG = 256 + 24 + cacheSize;
        int alphabetR = 256;
        int alphabetB = 256;
        int alphabetA = 256;
        int alphabetD = 40;

        var histG = new int[alphabetG];
        var histR = new int[alphabetR];
        var histB = new int[alphabetB];
        var histA = new int[alphabetA];
        var histD = new int[alphabetD];

        // First pass: simulate the encode pass to build histograms with the
        // cache hits already factored in. The decision (cache hit vs literal)
        // is purely a function of the pixel value + cache state, both of
        // which are deterministic per-position, so the second pass can
        // recreate the same decisions.
        var simCache = new uint[cacheSize];
        for (int i = 0; i < pixels.Length; i++)
        {
            uint p = pixels[i];
            uint key = (p * 0x1E35A7BDu) >> (32 - CacheBits);
            if (simCache[key] == p && (i > 0 || p != 0))
            {
                // Cache hit. (Skip the spurious hit at i=0 when p == 0 since
                // the decoder's cache is also zero-initialized, so it would
                // also hit — but only if we emit the cache symbol.) Actually
                // the decoder *does* hit at i=0 when p==0 if we emit a cache
                // code. So we treat it uniformly here.
                histG[256 + 24 + key]++;
            }
            else
            {
                histG[(p >> 8) & 0xFF]++;
                histR[(p >> 16) & 0xFF]++;
                histB[p & 0xFF]++;
                histA[(p >> 24) & 0xFF]++;
                simCache[key] = p;
            }
        }

        // D tree is unused without LZ77; give it a single token so it
        // transmits as a 1-symbol tree (no bits per pixel).
        histD[0] = 1;

        int[] codeLenG = HuffmanCodeBuilder.BuildLengths(histG, maxLen: 15);
        int[] codeLenR = HuffmanCodeBuilder.BuildLengths(histR, maxLen: 15);
        int[] codeLenB = HuffmanCodeBuilder.BuildLengths(histB, maxLen: 15);
        int[] codeLenA = HuffmanCodeBuilder.BuildLengths(histA, maxLen: 15);
        int[] codeLenD = HuffmanCodeBuilder.BuildLengths(histD, maxLen: 15);

        var codeG = HuffmanCodeBuilder.LengthsToCanonicalCodes(codeLenG);
        var codeR = HuffmanCodeBuilder.LengthsToCanonicalCodes(codeLenR);
        var codeB = HuffmanCodeBuilder.LengthsToCanonicalCodes(codeLenB);
        var codeA = HuffmanCodeBuilder.LengthsToCanonicalCodes(codeLenA);

        // Emit the five Huffman trees.
        WriteHuffmanTree(bw, codeLenG);
        WriteHuffmanTree(bw, codeLenR);
        WriteHuffmanTree(bw, codeLenB);
        WriteHuffmanTree(bw, codeLenA);
        WriteHuffmanTree(bw, codeLenD);

        // Single-symbol channels: the decoder's Single-shortcut tree returns
        // its only symbol *without consuming any bits*. Skip emission so the
        // stream stays aligned.
        bool singleG = IsSingleSymbol(codeLenG);
        bool singleR = IsSingleSymbol(codeLenR);
        bool singleB = IsSingleSymbol(codeLenB);
        bool singleA = IsSingleSymbol(codeLenA);

        // Second pass: emit symbols, re-deriving the same hit/miss decisions
        // by resetting the cache and reapplying the deterministic logic.
        Array.Clear(simCache);
        for (int i = 0; i < pixels.Length; i++)
        {
            uint p = pixels[i];
            uint key = (p * 0x1E35A7BDu) >> (32 - CacheBits);
            if (simCache[key] == p && (i > 0 || p != 0))
            {
                int cacheCode = 256 + 24 + (int)key;
                if (!singleG) EmitSymbol(bw, codeG[cacheCode], codeLenG[cacheCode]);
            }
            else
            {
                int g = (int)((p >> 8) & 0xFF);
                int r = (int)((p >> 16) & 0xFF);
                int b = (int)(p & 0xFF);
                int a = (int)((p >> 24) & 0xFF);
                if (!singleG) EmitSymbol(bw, codeG[g], codeLenG[g]);
                if (!singleR) EmitSymbol(bw, codeR[r], codeLenR[r]);
                if (!singleB) EmitSymbol(bw, codeB[b], codeLenB[b]);
                if (!singleA) EmitSymbol(bw, codeA[a], codeLenA[a]);
                simCache[key] = p;
            }
        }
    }

    // =========================================================================
    // LZ77 backreferences (top-level body) — spec §3.2 length / distance
    // codes plus the standard color-cache decision.
    // =========================================================================

    /// <summary>Minimum LZ77 match length. Below this, literals always win.</summary>
    private const int Lz77MinMatch = 3;

    /// <summary>Max LZ77 match length representable by the 24-code length
    /// alphabet (prefix code 23 yields values up to 4096).</summary>
    private const int Lz77MaxMatch = 4096;

    /// <summary>Max distance representable by the 40-code distance alphabet
    /// (prefix code 39 yields values up to 1048576).</summary>
    private const int Lz77MaxDist = 1048575;

    /// <summary>Hash table size for the match finder; 4096 buckets keeps
    /// memory bounded and matches deflate-class encoders.</summary>
    private const int Lz77HashBits = 12;
    private const int Lz77HashSize = 1 << Lz77HashBits;

    /// <summary>Max candidates explored per bucket. Higher → better matches
    /// but slower encode; 16 is a comfortable middle ground.</summary>
    private const int Lz77ChainDepth = 16;

    /// <summary>
    /// Top-level body writer with LZ77 backreferences. Replaces the
    /// cache-only emit pass with: (1) build an action list (literal /
    /// cache-hit / backref) via a hash-chain match finder, (2) histogram
    /// the resulting symbols, (3) build five Huffman trees, (4) emit the
    /// trees, (5) emit the actions using the trees + extra bits.
    ///
    /// <para>Sub-images (palette, ColorTransform meta) still call
    /// <see cref="WriteSpatialBody"/> — LZ77 buys little on those tiny
    /// pixel counts and the cache-only path is leaner.</para>
    /// </summary>
    private static void WriteSpatialBodyLz77(BitWriter bw, uint[] pixels, int width, int height)
    {
        const int CacheBits = 5;
        int cacheSize = 1 << CacheBits;
        bw.WriteBits(1, 1);                // color_cache_present = 1
        bw.WriteBits(CacheBits, 4);
        bw.WriteBits(0, 1);                // meta_huffman_present = 0 (top level)

        int alphabetG = 256 + 24 + cacheSize;
        int alphabetD = 40;

        // ---- Step 1: build action stream + cache state via greedy LZ77. ----
        var actions = BuildLz77Actions(pixels, cacheSize);

        // ---- Step 2: histogram. ----
        var histG = new int[alphabetG];
        var histR = new int[256];
        var histB = new int[256];
        var histA = new int[256];
        var histD = new int[alphabetD];
        // Iterate actions: each is encoded as a header byte + N args (see
        // BuildLz77Actions for the encoding).
        for (int p = 0; p < actions.Count; )
        {
            int op = actions[p++];
            switch (op)
            {
                case Lz77OpLiteral:
                {
                    uint argb = (uint)actions[p++];
                    histG[(argb >> 8) & 0xFF]++;
                    histR[(argb >> 16) & 0xFF]++;
                    histB[argb & 0xFF]++;
                    histA[(argb >> 24) & 0xFF]++;
                    break;
                }
                case Lz77OpCache:
                {
                    int key = actions[p++];
                    histG[256 + 24 + key]++;
                    break;
                }
                case Lz77OpBackref:
                {
                    int length = actions[p++];
                    int distance = actions[p++];
                    var (lenCode, _, _) = ValueToPrefixCode(length);
                    var (distCodeIdx, _, _) = ValueToPrefixCode(distance + 120);
                    histG[256 + lenCode]++;
                    histD[distCodeIdx]++;
                    break;
                }
            }
        }
        // If D tree got no occurrences (no backrefs in this image), force it
        // single-symbol so the encoder still emits a valid tree.
        bool anyD = false;
        for (int i = 0; i < alphabetD && !anyD; i++) if (histD[i] > 0) anyD = true;
        if (!anyD) histD[0] = 1;

        // ---- Step 3: build trees. ----
        int[] codeLenG = HuffmanCodeBuilder.BuildLengths(histG, maxLen: 15);
        int[] codeLenR = HuffmanCodeBuilder.BuildLengths(histR, maxLen: 15);
        int[] codeLenB = HuffmanCodeBuilder.BuildLengths(histB, maxLen: 15);
        int[] codeLenA = HuffmanCodeBuilder.BuildLengths(histA, maxLen: 15);
        int[] codeLenD = HuffmanCodeBuilder.BuildLengths(histD, maxLen: 15);

        var codeG = HuffmanCodeBuilder.LengthsToCanonicalCodes(codeLenG);
        var codeR = HuffmanCodeBuilder.LengthsToCanonicalCodes(codeLenR);
        var codeB = HuffmanCodeBuilder.LengthsToCanonicalCodes(codeLenB);
        var codeA = HuffmanCodeBuilder.LengthsToCanonicalCodes(codeLenA);
        var codeD = HuffmanCodeBuilder.LengthsToCanonicalCodes(codeLenD);

        WriteHuffmanTree(bw, codeLenG);
        WriteHuffmanTree(bw, codeLenR);
        WriteHuffmanTree(bw, codeLenB);
        WriteHuffmanTree(bw, codeLenA);
        WriteHuffmanTree(bw, codeLenD);

        bool singleG = IsSingleSymbol(codeLenG);
        bool singleR = IsSingleSymbol(codeLenR);
        bool singleB = IsSingleSymbol(codeLenB);
        bool singleA = IsSingleSymbol(codeLenA);
        bool singleD = IsSingleSymbol(codeLenD);

        // ---- Step 4: emit actions. ----
        for (int p = 0; p < actions.Count; )
        {
            int op = actions[p++];
            switch (op)
            {
                case Lz77OpLiteral:
                {
                    uint argb = (uint)actions[p++];
                    int g = (int)((argb >> 8) & 0xFF);
                    int r = (int)((argb >> 16) & 0xFF);
                    int b = (int)(argb & 0xFF);
                    int a = (int)((argb >> 24) & 0xFF);
                    if (!singleG) EmitSymbol(bw, codeG[g], codeLenG[g]);
                    if (!singleR) EmitSymbol(bw, codeR[r], codeLenR[r]);
                    if (!singleB) EmitSymbol(bw, codeB[b], codeLenB[b]);
                    if (!singleA) EmitSymbol(bw, codeA[a], codeLenA[a]);
                    break;
                }
                case Lz77OpCache:
                {
                    int key = actions[p++];
                    int sym = 256 + 24 + key;
                    if (!singleG) EmitSymbol(bw, codeG[sym], codeLenG[sym]);
                    break;
                }
                case Lz77OpBackref:
                {
                    int length = actions[p++];
                    int distance = actions[p++];
                    var (lenCode, lenEb, lenExtra) = ValueToPrefixCode(length);
                    var (distCodeIdx, distEb, distExtra) = ValueToPrefixCode(distance + 120);
                    int gSym = 256 + lenCode;
                    if (!singleG) EmitSymbol(bw, codeG[gSym], codeLenG[gSym]);
                    if (lenEb > 0) bw.WriteBits(lenExtra, lenEb);
                    if (!singleD) EmitSymbol(bw, codeD[distCodeIdx], codeLenD[distCodeIdx]);
                    if (distEb > 0) bw.WriteBits(distExtra, distEb);
                    break;
                }
            }
        }
    }

    private const int Lz77OpLiteral = 0;
    private const int Lz77OpCache = 1;
    private const int Lz77OpBackref = 2;

    /// <summary>
    /// Walk the pixel buffer left-to-right and produce a flat action list
    /// encoded as a <see cref="List{T}"/> of ints: each action is one
    /// header int (opcode) followed by 1–2 argument ints. The action
    /// stream is what the second pass histograms and emits.
    /// </summary>
    private static List<int> BuildLz77Actions(uint[] pixels, int cacheSize)
    {
        int n = pixels.Length;
        var actions = new List<int>(n * 2);

        // Color cache state (mirrors the decoder).
        int cacheBits = 0;
        int tmpSize = cacheSize;
        while ((tmpSize >>= 1) > 0) cacheBits++;
        var cache = new uint[cacheSize];

        // Hash chain match finder (deflate-style).
        var head = new int[Lz77HashSize];
        var prev = new int[n];
        for (int i = 0; i < Lz77HashSize; i++) head[i] = -1;
        for (int i = 0; i < n; i++) prev[i] = -1;

        int i_ = 0;
        while (i_ < n)
        {
            int bestLen = 1;
            int bestDist = 0;

            // Probe the hash chain for matches starting at i_.
            if (i_ + Lz77MinMatch <= n)
            {
                int h = Hash3(pixels[i_], pixels[i_ + 1], pixels[i_ + 2]);
                int candidate = head[h];
                int depth = 0;
                while (candidate >= 0 && depth < Lz77ChainDepth)
                {
                    int dist = i_ - candidate;
                    if (dist > Lz77MaxDist) break;
                    // Compute LCP between pixels[i_..] and pixels[candidate..].
                    int maxLen = Math.Min(n - i_, Lz77MaxMatch);
                    int len = 0;
                    while (len < maxLen && pixels[i_ + len] == pixels[candidate + len]) len++;
                    if (len > bestLen)
                    {
                        bestLen = len;
                        bestDist = dist;
                        if (len >= Lz77MaxMatch) break;
                    }
                    candidate = prev[candidate];
                    depth++;
                }
            }

            if (bestLen >= Lz77MinMatch)
            {
                // Emit a backref. Insert all source positions into the hash
                // chain so we can match further into the run later.
                actions.Add(Lz77OpBackref);
                actions.Add(bestLen);
                actions.Add(bestDist);
                for (int k = 0; k < bestLen; k++)
                {
                    int pos = i_ + k;
                    StoreCacheArgb(cache, cacheBits, pixels[pos]);
                    if (pos + Lz77MinMatch <= n)
                    {
                        int h = Hash3(pixels[pos], pixels[pos + 1], pixels[pos + 2]);
                        prev[pos] = head[h];
                        head[h] = pos;
                    }
                }
                i_ += bestLen;
            }
            else
            {
                // No usable match — check cache, else emit a literal.
                uint p = pixels[i_];
                uint key = unchecked(p * 0x1E35A7BDu) >> (32 - cacheBits);
                if (cache[key] == p && (i_ > 0 || p != 0))
                {
                    actions.Add(Lz77OpCache);
                    actions.Add((int)key);
                }
                else
                {
                    actions.Add(Lz77OpLiteral);
                    actions.Add(unchecked((int)p));
                }
                StoreCacheArgb(cache, cacheBits, p);
                if (i_ + Lz77MinMatch <= n)
                {
                    int h = Hash3(p, pixels[i_ + 1], pixels[i_ + 2]);
                    prev[i_] = head[h];
                    head[h] = i_;
                }
                i_++;
            }
        }
        return actions;
    }

    /// <summary>Cache update mirroring the decoder's StoreCache.</summary>
    private static void StoreCacheArgb(uint[] cache, int bits, uint argb)
    {
        uint key = unchecked(argb * 0x1E35A7BDu) >> (32 - bits);
        cache[key] = argb;
    }

    /// <summary>
    /// Hash 3 consecutive pixels into a <see cref="Lz77HashBits"/>-bit
    /// bucket index. Used as the hash-chain key for the match finder.
    /// </summary>
    private static int Hash3(uint p0, uint p1, uint p2)
    {
        ulong h = ((ulong)p0) ^ (((ulong)p1) << 21) ^ (((ulong)p2) << 42);
        h = unchecked(h * 0x9E3779B97F4A7C15UL);
        return (int)(h >> (64 - Lz77HashBits));
    }

    /// <summary>
    /// Inverse of the spec's <c>PrefixToValue</c>: given a positive
    /// integer value, return the prefix code plus the number of extra
    /// bits and the extra bits' value.
    ///
    /// <para>Formula: for values ≤ 4 the prefix code is <c>value - 1</c>
    /// with no extra bits. For larger values we find the smallest
    /// <c>eb ≥ 1</c> such that <c>(4 &lt;&lt; eb) &gt; (value - 1)</c>;
    /// then the value lies in either <c>[(2&lt;&lt;eb)+1, (3&lt;&lt;eb)]</c>
    /// (prefixCode = <c>2 + 2eb</c>) or
    /// <c>[(3&lt;&lt;eb)+1, (4&lt;&lt;eb)]</c> (prefixCode = <c>3 + 2eb</c>).</para>
    /// </summary>
    private static (int PrefixCode, int ExtraBits, int ExtraValue) ValueToPrefixCode(int value)
    {
        if (value <= 4) return (value - 1, 0, 0);
        int v = value - 1;
        int eb = 1;
        while ((4 << eb) <= v) eb++;
        // Now (2<<eb) ≤ v < (4<<eb); decide which half.
        if (v < (3 << eb))
            return (2 + 2 * eb, eb, v - (2 << eb));
        else
            return (3 + 2 * eb, eb, v - (3 << eb));
    }

    private static bool IsSingleSymbol(int[] lengths)
    {
        int nonZero = 0;
        for (int i = 0; i < lengths.Length && nonZero <= 1; i++)
            if (lengths[i] > 0) nonZero++;
        return nonZero == 1;
    }

    /// <summary>
    /// Emit a Huffman tree using "normal" transmission per VP8L spec:
    ///   - simple-mode flag (1 bit) — we always pick normal-mode here for
    ///     multi-symbol trees
    ///   - num_code_len_codes (4 bits, +4)
    ///   - that many 3-bit code-length-code lengths in the spec's
    ///     fixed permutation order
    ///   - max_symbol flag (1 bit) — we set 0 to mean "all symbols"
    ///   - per-symbol code lengths via the code-length-code alphabet,
    ///     using DEFLATE-style repeat opcodes 16 (repeat-prev 3..6),
    ///     17 (zero-run 3..10), 18 (zero-run 11..138) for compactness.
    /// </summary>
    private static void WriteHuffmanTree(BitWriter bw, int[] lengths)
    {
        // Special case: single-symbol tree. Use simple-mode (1 symbol).
        int nonZero = 0, onlySym = -1;
        for (int i = 0; i < lengths.Length; i++) if (lengths[i] > 0) { nonZero++; onlySym = i; }
        if (nonZero == 0) { /* impossible — at least one slot was incremented */ throw new InvalidOperationException(); }
        if (nonZero == 1)
        {
            bw.WriteBits(1, 1);                 // simple-mode
            bw.WriteBits(0, 1);                 // num_symbols - 1 = 0 → 1 symbol
            bool fitsIn1Bit = onlySym <= 1;
            bw.WriteBits(fitsIn1Bit ? 0 : 1, 1); // length-of-first-symbol code
            bw.WriteBits(onlySym, fitsIn1Bit ? 1 : 8);
            return;
        }

        // Normal mode.
        bw.WriteBits(0, 1);

        // Plan the opcode stream first — this lets the CL histogram exactly
        // reflect which codes (including 16/17/18) we'll emit.
        var ops = PlanTreeEmission(lengths);
        var clHist = new int[19];
        foreach (var op in ops) clHist[op.Code]++;

        int[] clLens = HuffmanCodeBuilder.BuildLengths(clHist, maxLen: 7);
        var clCodes = HuffmanCodeBuilder.LengthsToCanonicalCodes(clLens);

        // Determine num_code_len_codes (4..19). Find the highest used index
        // in the permutation order so we can transmit fewer.
        int[] permutation = {
            17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15
        };
        int numCl = 19;
        while (numCl > 4 && clLens[permutation[numCl - 1]] == 0) numCl--;
        bw.WriteBits(numCl - 4, 4);
        for (int i = 0; i < numCl; i++)
            bw.WriteBits(clLens[permutation[i]], 3);

        // max_symbol flag = 0 → transmit lengths for the full alphabet.
        bw.WriteBits(0, 1);

        // If the CL tree collapses to a single length-literal symbol (CL code
        // in 0..15), the decoder's Single-shortcut consumes zero bits per read
        // and fills every length with that value — we emit zero bits here too.
        // The shortcut is NOT safe for single-symbol opcode 16/17/18: those
        // need extra-bits per repeat, and the decoder will still demand them.
        if (IsSingleSymbol(clLens))
        {
            int onlyCl = -1;
            for (int i = 0; i < 19; i++) if (clLens[i] > 0) { onlyCl = i; break; }
            if (onlyCl < 16) return;
            // Otherwise fall through and emit each opcode's extra-bits.
        }

        // Emit the planned opcodes: CL code + optional extra bits per opcode.
        foreach (var op in ops)
        {
            EmitSymbol(bw, clCodes[op.Code], clLens[op.Code]);
            if (op.NExtra > 0) bw.WriteBits(op.Extra, op.NExtra);
        }
    }

    /// <summary>
    /// Plan the sequence of (CL code, extra-bits, n-extra) opcodes that
    /// transmits <paramref name="lengths"/> using DEFLATE-style opcode 16
    /// (repeat-previous 3..6), 17 (zero-run 3..10), and 18 (zero-run 11..138)
    /// in addition to literal 0..15. The planner is greedy: always packs the
    /// largest applicable run.
    /// </summary>
    private static List<TreeOp> PlanTreeEmission(int[] lengths)
    {
        var ops = new List<TreeOp>(lengths.Length / 4 + 4);
        int s = 0;
        while (s < lengths.Length)
        {
            int len = lengths[s];
            if (len == 0)
            {
                int runEnd = s;
                while (runEnd < lengths.Length && lengths[runEnd] == 0) runEnd++;
                int runLen = runEnd - s;
                while (runLen >= 3)
                {
                    if (runLen >= 11)
                    {
                        int emit = Math.Min(runLen, 138);
                        ops.Add(new TreeOp(18, emit - 11, 7));
                        s += emit; runLen -= emit;
                    }
                    else
                    {
                        // runLen in [3, 10]
                        ops.Add(new TreeOp(17, runLen - 3, 3));
                        s += runLen; runLen = 0;
                    }
                }
                while (runLen > 0) { ops.Add(new TreeOp(0, 0, 0)); s++; runLen--; }
            }
            else
            {
                // Emit one literal of this length.
                ops.Add(new TreeOp(len, 0, 0));
                s++;
                // Pack any following identical lengths via opcode-16 runs of 3..6.
                int sameRun = 0;
                while (s < lengths.Length && lengths[s] == len)
                {
                    sameRun++; s++;
                }
                while (sameRun >= 3)
                {
                    int emit = Math.Min(sameRun, 6);
                    ops.Add(new TreeOp(16, emit - 3, 2));
                    sameRun -= emit;
                }
                while (sameRun > 0) { ops.Add(new TreeOp(len, 0, 0)); sameRun--; }
            }
        }
        return ops;
    }

    private readonly record struct TreeOp(int Code, int Extra, int NExtra);

    private static void EmitSymbol(BitWriter bw, int code, int len)
    {
        // VP8L bitstream is LSB-first within bytes (matches the decoder's
        // BitReader). Canonical Huffman codes are MSB-first within the
        // code itself, so we reverse them per-symbol before writing.
        int reversed = 0;
        for (int b = 0; b < len; b++)
            if (((code >> b) & 1) != 0)
                reversed |= 1 << (len - 1 - b);
        bw.WriteBits(reversed, len);
    }

    private static byte[] WrapRiff(byte[] vp8lPayload)
    {
        // Pad VP8L payload to even length per RIFF rule (length field
        // counts payload bytes; padding byte not counted).
        int padded = (vp8lPayload.Length + 1) & ~1;
        int padding = padded - vp8lPayload.Length;
        int totalSize = 4 /* "WEBP" */ + 8 /* VP8L header */ + padded;
        var output = new byte[8 + totalSize];

        // RIFF header
        output[0] = (byte)'R'; output[1] = (byte)'I'; output[2] = (byte)'F'; output[3] = (byte)'F';
        BitConverter.GetBytes((uint)totalSize).CopyTo(output, 4);
        output[8] = (byte)'W'; output[9] = (byte)'E'; output[10] = (byte)'B'; output[11] = (byte)'P';

        // VP8L chunk header
        output[12] = (byte)'V'; output[13] = (byte)'P'; output[14] = (byte)'8'; output[15] = (byte)'L';
        BitConverter.GetBytes((uint)vp8lPayload.Length).CopyTo(output, 16);

        // VP8L payload
        Buffer.BlockCopy(vp8lPayload, 0, output, 20, vp8lPayload.Length);
        // padding byte (if any) already initialized to 0.

        return output;
    }

    // =========================================================================
    // BitWriter — LSB-first, matches PureWebpLossless.BitReader.
    // =========================================================================

    internal sealed class BitWriter
    {
        private byte[] _buf = new byte[256];
        private int _pos;
        private ulong _accum;
        private int _bits;

        public void WriteBits(int value, int n)
        {
            // Pack `value` (low n bits) into the accumulator, LSB-first.
            _accum |= ((ulong)value & ((1UL << n) - 1)) << _bits;
            _bits += n;
            while (_bits >= 8)
            {
                if (_pos == _buf.Length) Grow();
                _buf[_pos++] = (byte)(_accum & 0xFF);
                _accum >>= 8;
                _bits -= 8;
            }
        }

        public byte[] ToArray()
        {
            // Flush partial byte.
            if (_bits > 0)
            {
                if (_pos == _buf.Length) Grow();
                _buf[_pos++] = (byte)(_accum & 0xFF);
                _accum = 0;
                _bits = 0;
            }
            var result = new byte[_pos];
            Buffer.BlockCopy(_buf, 0, result, 0, _pos);
            return result;
        }

        private void Grow()
        {
            var next = new byte[_buf.Length * 2];
            Buffer.BlockCopy(_buf, 0, next, 0, _pos);
            _buf = next;
        }
    }

    // =========================================================================
    // HuffmanCodeBuilder — frequencies → canonical code lengths → codes.
    // =========================================================================

    internal static class HuffmanCodeBuilder
    {
        /// <summary>
        /// Given symbol frequencies, return per-symbol code lengths bounded
        /// by <paramref name="maxLen"/>. Uses the boundary package-merge
        /// algorithm (Larmore &amp; Hirschberg, 1990) — produces *optimal*
        /// length-limited Huffman codes, not a Kraft-valid approximation,
        /// so the bitstream is guaranteed parseable even on adversarial
        /// input distributions where a swap-heuristic might fail to
        /// converge.
        /// </summary>
        public static int[] BuildLengths(int[] freq, int maxLen)
        {
            int n = freq.Length;
            var lengths = new int[n];

            int nz = 0, onlySym = -1;
            for (int i = 0; i < n; i++)
                if (freq[i] > 0) { nz++; if (onlySym < 0) onlySym = i; }

            if (nz == 0) return lengths;
            if (nz == 1)
            {
                // Single-symbol tree — assign length 1 (decoder shortcut
                // handles it: returns the only symbol without consuming
                // any bits, so encoder also emits zero bits per pixel).
                lengths[onlySym] = 1;
                return lengths;
            }

            // Kraft sanity: maxLen must allow at least ceil(log2(nz)) bits
            // for nz symbols. For our alphabets (nz ≤ 280) and maxLen = 15
            // (2^15 = 32768), this always holds.
            if ((1L << maxLen) < nz)
                throw new InvalidOperationException(
                    $"maxLen={maxLen} cannot encode {nz} symbols (need at least {(int)Math.Ceiling(Math.Log2(nz))})");

            return PackageMerge(freq, maxLen);
        }

        /// <summary>
        /// Optimal length-limited Huffman code lengths via boundary
        /// package-merge.
        ///
        /// <para>Construction: maintain L "levels". Level 0 = the sorted
        /// list of leaves. Level i = pair-and-merge of level (i-1) with
        /// the original leaves. From the final level we take the 2n−2
        /// lightest items; the code length of each symbol = how many of
        /// those selected items contain it in their ancestry.</para>
        ///
        /// <para>For our alphabet sizes (n ≤ 280) and maxLen = 15 the
        /// algorithm runs in microseconds.</para>
        /// </summary>
        private static int[] PackageMerge(int[] freq, int maxLen)
        {
            int n = freq.Length;
            var lengths = new int[n];

            // Non-zero leaves sorted ascending by frequency. We use a
            // *stable* sort by (Freq, OriginalIndex) so the resulting
            // canonical codes are deterministic across runs.
            var leaves = new List<(int Sym, long Freq)>();
            for (int i = 0; i < n; i++) if (freq[i] > 0) leaves.Add((i, freq[i]));
            leaves.Sort((a, b) =>
            {
                int c = a.Freq.CompareTo(b.Freq);
                return c != 0 ? c : a.Sym.CompareTo(b.Sym);
            });
            int nz = leaves.Count;

            // An item at any level is (weight, packageLeftChildIdx,
            // packageRightChildIdx, leafIdx). Leaves: Left = Right = -1,
            // LeafIdx = index into `leaves`. Packages: LeafIdx = -1,
            // Left/Right index into the *previous level's* item list.
            var levels = new List<List<(long W, int L, int R, int Leaf)>>(maxLen);

            // Level 0 = leaves in ascending order.
            var lvl0 = new List<(long W, int L, int R, int Leaf)>(nz);
            for (int i = 0; i < nz; i++) lvl0.Add((leaves[i].Freq, -1, -1, i));
            levels.Add(lvl0);

            // Build maxLen − 1 further levels.
            for (int L = 1; L < maxLen; L++)
            {
                var prev = levels[L - 1];
                int pairs = prev.Count / 2;

                // Step a: pair adjacent items into packages (already in
                // ascending order so packages come out ascending too).
                var packages = new List<(long W, int L, int R, int Leaf)>(pairs);
                for (int i = 0; i + 1 < prev.Count; i += 2)
                    packages.Add((prev[i].W + prev[i + 1].W, i, i + 1, -1));

                // Step b: merge sorted-ascending leaves with sorted-ascending
                // packages.
                var merged = new List<(long W, int L, int R, int Leaf)>(nz + pairs);
                int pi = 0, li = 0;
                while (pi < packages.Count || li < nz)
                {
                    bool takeLeaf = li < nz && (pi == packages.Count || leaves[li].Freq <= packages[pi].W);
                    if (takeLeaf) { merged.Add((leaves[li].Freq, -1, -1, li)); li++; }
                    else { merged.Add(packages[pi]); pi++; }
                }
                levels.Add(merged);
            }

            // Take the bottom 2nz − 2 items from the final level (lightest
            // first) and tally how many times each leaf is referenced.
            int take = 2 * nz - 2;
            var topLvl = levels[maxLen - 1];
            if (take > topLvl.Count) take = topLvl.Count;  // defensive

            var counts = new int[nz];

            void Trace(int level, int idx)
            {
                var item = levels[level][idx];
                if (item.Leaf >= 0) { counts[item.Leaf]++; return; }
                Trace(level - 1, item.L);
                Trace(level - 1, item.R);
            }

            for (int i = 0; i < take; i++) Trace(maxLen - 1, i);

            // Convert counts (= ancestry occurrences) into per-symbol
            // lengths. Counts are 1..maxLen by construction.
            for (int i = 0; i < nz; i++) lengths[leaves[i].Sym] = counts[i];

            return lengths;
        }

        /// <summary>
        /// Build canonical code values from code lengths. Mirrors the
        /// decoder's reading order (lower-numbered symbol at a length gets
        /// the numerically smaller code).
        /// </summary>
        public static int[] LengthsToCanonicalCodes(int[] lengths)
        {
            int n = lengths.Length;
            int maxLen = 0;
            for (int i = 0; i < n; i++) if (lengths[i] > maxLen) maxLen = lengths[i];

            var codes = new int[n];
            if (maxLen == 0) return codes;

            var blCount = new int[maxLen + 1];
            for (int i = 0; i < n; i++) blCount[lengths[i]]++;
            blCount[0] = 0;

            var nextCode = new int[maxLen + 2];
            int code = 0;
            for (int b = 1; b <= maxLen; b++)
            {
                code = (code + blCount[b - 1]) << 1;
                nextCode[b] = code;
            }
            for (int i = 0; i < n; i++)
            {
                if (lengths[i] != 0) codes[i] = nextCode[lengths[i]]++;
            }
            return codes;
        }
    }

    // =========================================================================
    // Minimal indexed-key priority queue (min-heap of (key, priority)).
    // .NET 6+ has System.Collections.Generic.PriorityQueue but that is a
    // (TElement, TPriority) tuple heap — equivalent. Wrapper kept for
    // long-priority clarity.
    // =========================================================================

    internal sealed class PriorityQueueLong<T>
    {
        private readonly PriorityQueue<T, long> _pq = new();
        public int Count => _pq.Count;
        public void Enqueue(T item, long priority) => _pq.Enqueue(item, priority);
        public T Dequeue() => _pq.Dequeue();
    }
}
