using System;
using System.Collections.Generic;
using System.Text;

namespace CosmoImage.Operations.Create;

/// <summary>
/// QR Code (Model 2) generator, byte-mode only, all 40 versions, all four
/// error-correction levels, all eight data masks. Pure-managed
/// implementation of ISO/IEC 18004:2015 — no external dependencies.
///
/// <para>Scope: byte mode (UTF-8) is the universally compatible encoding
/// and covers the common use cases (URLs, vCard, ZATCA TLV, etc.).
/// Numeric / alphanumeric / kanji compaction is left as a follow-up; they
/// shrink the symbol but byte mode always works.</para>
///
/// <para>Output is a single-band UChar <see cref="VipsImage"/> with
/// 0 = black module, 255 = white module, including a configurable
/// quiet-zone border (default 4 modules per spec).</para>
/// </summary>
public static class VipsQrCode
{
    /// <summary>Error-correction level: roughly 7 / 15 / 25 / 30 % recovery capacity.</summary>
    public enum Ecc : byte { Low = 0, Medium = 1, Quartile = 2, High = 3 }

    /// <summary>
    /// Generate a QR Code from a text payload. Picks the smallest version
    /// (1..40) that fits <paramref name="text"/> at the requested
    /// <paramref name="ecc"/>; throws if the payload would not fit even at
    /// version 40.
    /// </summary>
    /// <param name="text">Payload — encoded as UTF-8 bytes in byte mode.</param>
    /// <param name="pixelsPerModule">Render scale; each QR module becomes
    /// a <c>pixelsPerModule × pixelsPerModule</c> pixel block.</param>
    /// <param name="ecc">Error-correction level.</param>
    /// <param name="borderModules">Quiet-zone border width in modules.
    /// Spec mandates ≥ 4 for reliable scanning; pass 0 if you'll add the
    /// quiet zone yourself.</param>
    public static VipsImage Generate(
        string text,
        int pixelsPerModule = 4,
        Ecc ecc = Ecc.Medium,
        int borderModules = 4)
    {
        if (text == null) throw new ArgumentNullException(nameof(text));
        if (pixelsPerModule < 1) throw new ArgumentOutOfRangeException(nameof(pixelsPerModule));
        if (borderModules < 0) throw new ArgumentOutOfRangeException(nameof(borderModules));

        byte[] payload = Encoding.UTF8.GetBytes(text);
        bool[,] matrix = BuildMatrix(payload, ecc);
        return Render(matrix, pixelsPerModule, borderModules);
    }

    // =========================================================================
    // High-level pipeline: payload → smallest fitting version → matrix.
    // =========================================================================

    private static bool[,] BuildMatrix(byte[] data, Ecc ecc)
    {
        int version = SelectVersion(data.Length, ecc);
        int numDataCodewords = GetNumDataCodewords(version, ecc);

        // ---- 1. Build the data bitstream (mode + char count + data + padding). ----
        var bb = new BitBuffer();
        bb.Append(0b0100, 4);                              // mode indicator: byte
        int ccBits = version <= 9 ? 8 : 16;                // char-count indicator width
        bb.Append(data.Length, ccBits);
        foreach (byte b in data) bb.Append(b, 8);
        // Terminator: up to 4 zero bits, capped by remaining capacity.
        int remaining = numDataCodewords * 8 - bb.Length;
        bb.Append(0, Math.Min(4, remaining));
        // Pad to byte boundary.
        bb.Append(0, (8 - bb.Length % 8) % 8);
        // Pad-byte fill with alternating 0xEC / 0x11 (spec §7.4.10).
        for (int padIdx = 0; bb.Length < numDataCodewords * 8; padIdx++)
            bb.Append(padIdx % 2 == 0 ? 0xEC : 0x11, 8);

        byte[] dataCodewords = bb.ToBytes();
        byte[] allCodewords = AddEccAndInterleave(dataCodewords, version, ecc);

        // ---- 2. Allocate the symbol matrix + function-module mask. ----
        int size = 17 + 4 * version;
        var matrix = new bool[size, size];
        var isFunction = new bool[size, size];          // true → module is reserved (no data, no mask)

        DrawFunctionPatterns(matrix, isFunction, version);
        DrawCodewords(matrix, isFunction, allCodewords);

        // ---- 3. Mask selection: try all 8 patterns, pick lowest penalty. ----
        int bestMask = 0;
        long bestPenalty = long.MaxValue;
        for (int mask = 0; mask < 8; mask++)
        {
            ApplyMask(matrix, isFunction, mask);
            DrawFormatBits(matrix, ecc, mask);
            long penalty = ComputePenalty(matrix);
            if (penalty < bestPenalty) { bestPenalty = penalty; bestMask = mask; }
            ApplyMask(matrix, isFunction, mask);        // XOR-undo (mask is self-inverse)
        }
        ApplyMask(matrix, isFunction, bestMask);
        DrawFormatBits(matrix, ecc, bestMask);

        return matrix;
    }

    /// <summary>Smallest version (1..40) whose data capacity holds <paramref name="dataLen"/> bytes.</summary>
    private static int SelectVersion(int dataLen, Ecc ecc)
    {
        for (int v = 1; v <= 40; v++)
        {
            int capacity = GetNumDataCodewords(v, ecc);
            int ccBits = v <= 9 ? 8 : 16;
            // mode (4) + char-count + data*8 must fit in capacity*8 bits.
            int neededBits = 4 + ccBits + dataLen * 8;
            if (neededBits <= capacity * 8) return v;
        }
        throw new ArgumentException(
            $"Data length {dataLen} bytes exceeds QR capacity at ECC {ecc} even at version 40.");
    }

    // =========================================================================
    // Reed-Solomon error correction (GF(256), primitive polynomial 0x11D).
    // =========================================================================

    /// <summary>Split data into ECC blocks, append RS parity, interleave per spec §7.6.</summary>
    private static byte[] AddEccAndInterleave(byte[] data, int version, Ecc ecc)
    {
        int numBlocks = NumErrorCorrectionBlocks[(int)ecc, version];
        int totalEccCodewords = EccCodewordsPerBlock[(int)ecc, version] * numBlocks;
        int totalDataCodewords = data.Length;
        int rawCodewords = GetNumRawDataModules(version) / 8;
        if (totalDataCodewords + totalEccCodewords != rawCodewords)
            throw new InvalidOperationException("internal: codeword count mismatch");

        int shortBlockLen = rawCodewords / numBlocks;
        int numShortBlocks = numBlocks - rawCodewords % numBlocks;
        int blockEccLen = EccCodewordsPerBlock[(int)ecc, version];

        byte[] rsDivisor = ReedSolomonComputeDivisor(blockEccLen);

        // Per-block (data || ecc).
        var blocks = new byte[numBlocks][];
        int dataOffset = 0;
        for (int i = 0; i < numBlocks; i++)
        {
            int blockDataLen = (shortBlockLen + 1) - blockEccLen - (i < numShortBlocks ? 1 : 0);
            // (shortBlockLen - blockEccLen) for short blocks, +1 for long blocks.
            var blockData = new byte[blockDataLen];
            Array.Copy(data, dataOffset, blockData, 0, blockDataLen);
            dataOffset += blockDataLen;
            var ecc_ = ReedSolomonComputeRemainder(blockData, rsDivisor);
            var combined = new byte[blockDataLen + blockEccLen];
            Array.Copy(blockData, combined, blockDataLen);
            Array.Copy(ecc_, 0, combined, blockDataLen, blockEccLen);
            blocks[i] = combined;
        }

        // Interleave codewords column-wise: data first, then ECC.
        var result = new byte[rawCodewords];
        int writeIdx = 0;
        int longestData = shortBlockLen - blockEccLen + 1;       // length of the longest block's data
        for (int col = 0; col < longestData; col++)
        {
            for (int row = 0; row < numBlocks; row++)
            {
                // Skip the last data byte of short blocks (they're 1 byte shorter).
                if (col == longestData - 1 && row < numShortBlocks) continue;
                result[writeIdx++] = blocks[row][col];
            }
        }
        for (int col = 0; col < blockEccLen; col++)
            for (int row = 0; row < numBlocks; row++)
                result[writeIdx++] = blocks[row][blocks[row].Length - blockEccLen + col];

        return result;
    }

    /// <summary>Build the Reed-Solomon generator polynomial of degree <paramref name="degree"/>.</summary>
    private static byte[] ReedSolomonComputeDivisor(int degree)
    {
        // Start with x^0 = [1]; multiply by (x - α^i) for i in 0..degree-1.
        var result = new byte[degree];
        result[degree - 1] = 1;
        int root = 1;
        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < degree; j++)
            {
                result[j] = (byte)GfMul(result[j], (byte)root);
                if (j + 1 < degree) result[j] ^= result[j + 1];
            }
            root = GfMul((byte)root, 0x02);
        }
        return result;
    }

    /// <summary>Compute RS remainder = <paramref name="data"/> · x^deg mod divisor.</summary>
    private static byte[] ReedSolomonComputeRemainder(byte[] data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (byte b in data)
        {
            byte factor = (byte)(b ^ result[0]);
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (int i = 0; i < result.Length; i++)
                result[i] ^= (byte)GfMul(divisor[i], factor);
        }
        return result;
    }

    /// <summary>Multiply two GF(256) field elements using primitive polynomial 0x11D.</summary>
    private static int GfMul(byte x, byte y)
    {
        int z = 0;
        for (int i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return z & 0xFF;
    }

    // =========================================================================
    // Function-pattern placement (finders, separators, timing, alignment,
    // dark module, format/version reservations).
    // =========================================================================

    private static void DrawFunctionPatterns(bool[,] m, bool[,] mask, int version)
    {
        int size = m.GetLength(0);

        // Timing patterns: row 6 + column 6 alternate 1-0-1-0...
        for (int i = 0; i < size; i++)
        {
            SetFunction(m, mask, i, 6, i % 2 == 0);
            SetFunction(m, mask, 6, i, i % 2 == 0);
        }

        // Finder patterns: three 7×7 squares at corners + separators.
        DrawFinder(m, mask, 3, 3);
        DrawFinder(m, mask, size - 4, 3);
        DrawFinder(m, mask, 3, size - 4);

        // Alignment patterns: per-version table of centre coordinates;
        // skip centres that would collide with the finder patterns.
        int[] alignCoords = AlignmentPatternPositions(version);
        int n = alignCoords.Length;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                // Corners overlap finders — spec excludes them.
                if ((i == 0 && j == 0) || (i == 0 && j == n - 1) || (i == n - 1 && j == 0))
                    continue;
                DrawAlignment(m, mask, alignCoords[i], alignCoords[j]);
            }

        // Reserve format-info area (filled later by DrawFormatBits).
        DrawFormatReservation(m, mask, size);

        // Reserve version-info area on versions 7+ (filled below).
        if (version >= 7)
        {
            int rem = version;
            for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            long bits = ((long)version << 12) | rem;     // 18 bits
            // Two 6×3 strips: bottom-left and top-right corners.
            for (int i = 0; i < 18; i++)
            {
                bool bit = ((bits >> i) & 1) != 0;
                int a = size - 11 + i % 3;
                int b = i / 3;
                SetFunction(m, mask, a, b, bit);
                SetFunction(m, mask, b, a, bit);
            }
        }
    }

    private static void DrawFinder(bool[,] m, bool[,] mask, int cx, int cy)
    {
        // 9×9 area centred on (cx, cy): outer 7×7 finder + 1-module separator.
        int size = m.GetLength(0);
        for (int dy = -4; dy <= 4; dy++)
            for (int dx = -4; dx <= 4; dx++)
            {
                int x = cx + dx, y = cy + dy;
                if (x < 0 || x >= size || y < 0 || y >= size) continue;
                int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                bool on = dist != 2 && dist != 4;       // inner solid + ring at dist 3 + outer dark
                SetFunction(m, mask, x, y, on);
            }
    }

    private static void DrawAlignment(bool[,] m, bool[,] mask, int cx, int cy)
    {
        // 5×5: outer dark ring + dark centre.
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
            {
                int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                SetFunction(m, mask, cx + dx, cy + dy, dist != 1);
            }
    }

    /// <summary>
    /// Reserve the format-info area around the top-left finder + the row/col
    /// strips adjacent to the other two finders. Placed as functions so the
    /// data layout skips them; actual format bits go in via DrawFormatBits.
    /// </summary>
    private static void DrawFormatReservation(bool[,] m, bool[,] mask, int size)
    {
        // 15 cells around top-left + 15 mirrored at top-right + bottom-left.
        for (int i = 0; i < 9; i++) SetFunction(m, mask, 8, i, false);
        for (int i = 0; i < 8; i++) SetFunction(m, mask, i, 8, false);
        for (int i = 0; i < 8; i++) SetFunction(m, mask, size - 1 - i, 8, false);
        for (int i = 0; i < 7; i++) SetFunction(m, mask, 8, size - 7 + i, false);

        // The "dark module" at (8, size-8) is always set.
        SetFunction(m, mask, 8, size - 8, true);
    }

    private static void SetFunction(bool[,] m, bool[,] mask, int x, int y, bool on)
    {
        int size = m.GetLength(0);
        if (x < 0 || x >= size || y < 0 || y >= size) return;
        m[x, y] = on;
        mask[x, y] = true;
    }

    // =========================================================================
    // Data placement: zig-zag from bottom-right, two columns at a time,
    // upward-then-downward; skip column 6 (vertical timing pattern).
    // =========================================================================

    private static void DrawCodewords(bool[,] m, bool[,] isFunction, byte[] codewords)
    {
        int size = m.GetLength(0);
        int bitIdx = 0;
        // Iterate pairs of columns from right edge inward. Skip column 6.
        for (int right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (int vert = 0; vert < size; vert++)
            {
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    bool upward = ((right + 1) & 2) == 0;
                    int y = upward ? size - 1 - vert : vert;
                    if (!isFunction[x, y] && bitIdx < codewords.Length * 8)
                    {
                        bool bit = ((codewords[bitIdx >> 3] >> (7 - (bitIdx & 7))) & 1) != 0;
                        m[x, y] = bit;
                        bitIdx++;
                    }
                }
            }
        }
    }

    // =========================================================================
    // Mask + penalty (spec §7.8.3).
    // =========================================================================

    private static void ApplyMask(bool[,] m, bool[,] isFunction, int mask)
    {
        int size = m.GetLength(0);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                if (isFunction[x, y]) continue;
                bool flip = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (x / 3 + y / 2) % 2 == 0,
                    5 => x * y % 2 + x * y % 3 == 0,
                    6 => (x * y % 2 + x * y % 3) % 2 == 0,
                    7 => ((x + y) % 2 + x * y % 3) % 2 == 0,
                    _ => false,
                };
                if (flip) m[x, y] ^= true;
            }
    }

    private static long ComputePenalty(bool[,] m)
    {
        int size = m.GetLength(0);
        long penalty = 0;

        // Rule 1: runs of ≥ 5 same-coloured modules in row/col (3 + extra).
        for (int y = 0; y < size; y++)
        {
            int runColor = -1, runLen = 0;
            for (int x = 0; x < size; x++)
            {
                int c = m[x, y] ? 1 : 0;
                if (c == runColor) { runLen++; if (runLen == 5) penalty += 3; else if (runLen > 5) penalty++; }
                else { runColor = c; runLen = 1; }
            }
        }
        for (int x = 0; x < size; x++)
        {
            int runColor = -1, runLen = 0;
            for (int y = 0; y < size; y++)
            {
                int c = m[x, y] ? 1 : 0;
                if (c == runColor) { runLen++; if (runLen == 5) penalty += 3; else if (runLen > 5) penalty++; }
                else { runColor = c; runLen = 1; }
            }
        }

        // Rule 2: 2×2 blocks of identical colour (3 per block).
        for (int y = 0; y < size - 1; y++)
            for (int x = 0; x < size - 1; x++)
                if (m[x, y] == m[x + 1, y] && m[x, y] == m[x, y + 1] && m[x, y] == m[x + 1, y + 1])
                    penalty += 3;

        // Rule 3: finder-like patterns 1011101 with 4-wide light side (40 each).
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                if (x + 10 < size && MatchFinderRun(m, x, y, dx: 1, dy: 0)) penalty += 40;
                if (y + 10 < size && MatchFinderRun(m, x, y, dx: 0, dy: 1)) penalty += 40;
            }

        // Rule 4: deviation of black/total ratio from 50% (per 5% step).
        int black = 0;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                if (m[x, y]) black++;
        int total = size * size;
        int dev = Math.Abs(black * 20 - total * 10) * 10 / total;       // = floor(|black/total - 0.5| / 0.05)
        penalty += dev * 10;

        return penalty;
    }

    private static bool MatchFinderRun(bool[,] m, int x0, int y0, int dx, int dy)
    {
        // Look for 10111010000 or 00001011101 along (dx,dy) — finder-like pattern.
        Span<bool> p = stackalloc bool[11];
        for (int i = 0; i < 11; i++) p[i] = m[x0 + dx * i, y0 + dy * i];
        bool patA = !p[0] && !p[1] && !p[2] && !p[3] && p[4] && !p[5] && p[6] && p[7] && p[8] && !p[9] && p[10];
        bool patB = p[0] && !p[1] && p[2] && p[3] && p[4] && !p[5] && p[6] && !p[7] && !p[8] && !p[9] && !p[10];
        return patA || patB;
    }

    // =========================================================================
    // Format-info (BCH 15,5): write 15 bits around the three finders.
    // =========================================================================

    private static void DrawFormatBits(bool[,] m, Ecc ecc, int mask)
    {
        int size = m.GetLength(0);
        // 5 data bits: 2-bit ECC mapping + 3-bit mask. Then add 10 BCH parity.
        int eccBits = ecc switch { Ecc.Low => 1, Ecc.Medium => 0, Ecc.Quartile => 3, Ecc.High => 2, _ => 0 };
        int data = (eccBits << 3) | mask;
        int rem = data;
        for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        int bits = ((data << 10) | rem) ^ 0x5412;        // 15 bits, mask per spec

        // Top-left strip: positions (8, 0..5), (8, 7), (8, 8), (7, 8), (5..0, 8).
        for (int i = 0; i <= 5; i++) m[8, i] = ((bits >> i) & 1) != 0;
        m[8, 7] = ((bits >> 6) & 1) != 0;
        m[8, 8] = ((bits >> 7) & 1) != 0;
        m[7, 8] = ((bits >> 8) & 1) != 0;
        for (int i = 9; i < 15; i++) m[14 - i, 8] = ((bits >> i) & 1) != 0;

        // Second copy: bottom-left + top-right.
        for (int i = 0; i < 8; i++) m[size - 1 - i, 8] = ((bits >> i) & 1) != 0;
        for (int i = 8; i < 15; i++) m[8, size - 15 + i] = ((bits >> i) & 1) != 0;
        m[8, size - 8] = true;                            // dark module — always 1
    }

    // =========================================================================
    // Render to VipsImage (single-band UChar, 0=black, 255=white).
    // =========================================================================

    private static VipsImage Render(bool[,] matrix, int pixelsPerModule, int borderModules)
    {
        int size = matrix.GetLength(0);
        int totalModules = size + 2 * borderModules;
        int outDim = totalModules * pixelsPerModule;
        var bytes = new byte[outDim * outDim];
        // Default white background.
        Array.Fill(bytes, (byte)255);

        for (int my = 0; my < size; my++)
            for (int mx = 0; mx < size; mx++)
            {
                if (!matrix[mx, my]) continue;            // white modules already filled
                int px0 = (mx + borderModules) * pixelsPerModule;
                int py0 = (my + borderModules) * pixelsPerModule;
                for (int py = py0; py < py0 + pixelsPerModule; py++)
                {
                    int rowOff = py * outDim;
                    for (int px = px0; px < px0 + pixelsPerModule; px++)
                        bytes[rowOff + px] = 0;
                }
            }

        return new VipsImage
        {
            Width = outDim,
            Height = outDim,
            Bands = 1,
            BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            Coding = VipsCoding.None,
            XRes = 1.0,
            YRes = 1.0,
            PixelsLazy = new Lazy<byte[]>(() => bytes),
        };
    }

    // =========================================================================
    // Spec data tables (ISO/IEC 18004:2015 §6 + Annex C/D/E).
    // Static arrays are smaller than 4 KiB total — hardcoded for cleanliness.
    // =========================================================================

    /// <summary>EccCodewordsPerBlock[ecc, version] — RS parity codewords per block.</summary>
    private static readonly short[,] EccCodewordsPerBlock = new short[4, 41]
    {
        // version: 0 (unused), 1, 2, ..., 40
        // L
        { -1, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
        // M
        { -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28 },
        // Q
        { -1, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
        // H
        { -1, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
    };

    /// <summary>NumErrorCorrectionBlocks[ecc, version] — RS block count.</summary>
    private static readonly byte[,] NumErrorCorrectionBlocks = new byte[4, 41]
    {
        // L
        { 0, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25 },
        // M
        { 0, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49 },
        // Q
        { 0, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8, 8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68 },
        // H
        { 0, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81 },
    };

    /// <summary>Data codewords available (total raw - ECC).</summary>
    private static int GetNumDataCodewords(int version, Ecc ecc)
        => GetNumRawDataModules(version) / 8
            - EccCodewordsPerBlock[(int)ecc, version] * NumErrorCorrectionBlocks[(int)ecc, version];

    /// <summary>Total raw data modules (excludes finders, alignment, timing, format, version).</summary>
    private static int GetNumRawDataModules(int version)
    {
        int size = 17 + 4 * version;
        int result = size * size - 64 * 3 - 15 * 2 - 1;       // less finders+separators+format+dark
        if (version >= 2)
        {
            int n = version / 7 + 2;
            result -= (n - 2) * (n - 2) * 25 - (n - 2) * 2 * 20 - 9 * (version >= 2 && version <= 6 ? 1 : 0);
            // simpler: subtract alignment-pattern area minus overlap with timing.
            // Use Nayuki's direct formula:
            int numAlign = n;
            result = size * size;
            result -= 64 * 3;                                  // finders
            result -= 15 * 2 + 1;                              // format + dark
            result -= (size - 16);                             // timing (excluding finder overlap)
            // Alignment: numAlign² rings of 25 modules, minus overlap with timing strips.
            int alignArea = (numAlign * numAlign - 3) * 25;
            int alignTimingOverlap = (numAlign - 2) * 2 * 5;
            result -= alignArea - alignTimingOverlap;
            if (version >= 7) result -= 6 * 3 * 2;             // version info
        }
        else
        {
            // version 1: no alignment patterns
            result = size * size - 64 * 3 - 15 * 2 - 1 - (size - 16);
        }
        return result;
    }

    /// <summary>Per-version centre coordinates for alignment patterns; empty for v1.</summary>
    private static int[] AlignmentPatternPositions(int version)
    {
        if (version == 1) return Array.Empty<int>();
        int n = version / 7 + 2;
        int step = (version == 32) ? 26 : (version * 4 + n * 2 + 1) / (n * 2 - 2) * 2;
        var result = new int[n];
        result[0] = 6;
        for (int i = n - 1, pos = 17 + 4 * version - 7; i >= 1; i--, pos -= step)
            result[i] = pos;
        return result;
    }

    // =========================================================================
    // Tiny bit buffer.
    // =========================================================================

    private sealed class BitBuffer
    {
        private readonly List<byte> _bytes = new();
        public int Length { get; private set; }

        public void Append(int value, int nbits)
        {
            if (nbits == 0) return;
            if (nbits < 0 || nbits > 31) throw new ArgumentOutOfRangeException(nameof(nbits));
            for (int i = nbits - 1; i >= 0; i--)
            {
                int bit = (value >> i) & 1;
                if (Length % 8 == 0) _bytes.Add(0);
                if (bit != 0) _bytes[Length / 8] |= (byte)(1 << (7 - Length % 8));
                Length++;
            }
        }

        public byte[] ToBytes()
        {
            if (Length % 8 != 0) throw new InvalidOperationException("ToBytes called on non-byte-aligned buffer");
            return _bytes.ToArray();
        }
    }
}
