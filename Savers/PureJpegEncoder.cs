using System;
using System.Collections.Generic;
using System.IO;

namespace CosmoImage.Savers;

/// <summary>
/// Pure-managed baseline JPEG encoder. Phase 2 of the JpegLibrary
/// drop (phase 1 was the matching <c>PureJpegDecoder</c>).
/// Emits SOF0 baseline sequential JPEGs with standard JFIF Huffman
/// tables and quality-scaled Annex K quantization tables.
///
/// <para>Supported input:</para>
/// <list type="bullet">
///   <item>1-band UChar greyscale → single-component JPEG.</item>
///   <item>3-band UChar RGB → YCbCr 4:2:0 chroma-subsampled JPEG
///         (the JFIF default, by far the most-deployed variant).</item>
/// </list>
///
/// <para>Output stream layout: SOI → DQT (lum + chr) → SOF0 →
/// DHT (4 tables) → SOS → entropy-coded scan → EOI. The caller
/// (<see cref="VipsJpegSaver"/>) then splices in APP1 EXIF/XMP and
/// APP2 ICC chunks between SOI and DQT, same as the existing path.</para>
/// </summary>
internal static class PureJpegEncoder
{
    /// <summary>
    /// Encode <paramref name="pixels"/> as a baseline JPEG byte stream.
    /// <paramref name="quality"/> is 1..100; 75 is the typical default.
    /// </summary>
    public static byte[] Encode(byte[] pixels, int width, int height, int channels, int quality)
    {
        if (pixels == null) throw new ArgumentNullException(nameof(pixels));
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (channels != 1 && channels != 3) throw new ArgumentException("channels must be 1 or 3", nameof(channels));
        if (quality < 1) quality = 1;
        if (quality > 100) quality = 100;

        var lumQ = ScaleQuantTable(StdLumQuant, quality);
        var chrQ = ScaleQuantTable(StdChrQuant, quality);

        using var ms = new MemoryStream();

        // SOI.
        WriteByte(ms, 0xFF); WriteByte(ms, 0xD8);

        // DQT (luminance always; chrominance only when 3-component).
        WriteDqt(ms, lumQ, 0);
        if (channels == 3) WriteDqt(ms, chrQ, 1);

        // SOF0: baseline sequential.
        WriteSof0(ms, width, height, channels);

        // DHT: 4 standard tables for color, 2 for greyscale.
        WriteDht(ms, 0, 0, StdLumDcCounts, StdLumDcSymbols);
        WriteDht(ms, 1, 0, StdLumAcCounts, StdLumAcSymbols);
        if (channels == 3)
        {
            WriteDht(ms, 0, 1, StdChrDcCounts, StdChrDcSymbols);
            WriteDht(ms, 1, 1, StdChrAcCounts, StdChrAcSymbols);
        }

        // SOS: scan header.
        WriteSos(ms, channels);

        // Entropy-coded scan. Bit writer wraps the encoded-stream byte-stuffing
        // (real 0xFF samples emit 0xFF 0x00).
        var bits = new BitWriter(ms);
        if (channels == 1)
            EncodeGreyscale(bits, pixels, width, height, lumQ);
        else
            EncodeRgbAs420(bits, pixels, width, height, lumQ, chrQ);
        bits.Flush();

        // EOI.
        WriteByte(ms, 0xFF); WriteByte(ms, 0xD9);
        return ms.ToArray();
    }

    // ---- Marker emission ----

    private static void WriteByte(Stream s, byte b) => s.WriteByte(b);
    private static void WriteShortBE(Stream s, int v)
    {
        s.WriteByte((byte)((v >> 8) & 0xFF));
        s.WriteByte((byte)(v & 0xFF));
    }

    private static void WriteDqt(Stream s, byte[] table, int tableId)
    {
        WriteByte(s, 0xFF); WriteByte(s, 0xDB);
        WriteShortBE(s, 67); // 2 length + 1 PqTq + 64 entries
        WriteByte(s, (byte)tableId); // Pq=0 (8-bit), Tq = tableId
        // Table written in zigzag order — JPEG spec.
        for (int k = 0; k < 64; k++)
            WriteByte(s, table[ZigzagOrder[k]]);
    }

    private static void WriteSof0(Stream s, int width, int height, int channels)
    {
        WriteByte(s, 0xFF); WriteByte(s, 0xC0);
        int len = 8 + 3 * channels;
        WriteShortBE(s, len);
        WriteByte(s, 8); // P = sample precision
        WriteShortBE(s, height);
        WriteShortBE(s, width);
        WriteByte(s, (byte)channels);
        if (channels == 1)
        {
            // Y component, no subsampling, quant table 0.
            WriteByte(s, 1); WriteByte(s, 0x11); WriteByte(s, 0);
        }
        else
        {
            // Y: 2×2 sampling (drives the MCU = 16×16 pixels).
            WriteByte(s, 1); WriteByte(s, 0x22); WriteByte(s, 0);
            // Cb, Cr: 1×1 sampling, quant table 1.
            WriteByte(s, 2); WriteByte(s, 0x11); WriteByte(s, 1);
            WriteByte(s, 3); WriteByte(s, 0x11); WriteByte(s, 1);
        }
    }

    private static void WriteDht(Stream s, int tableClass, int tableId, byte[] counts, byte[] symbols)
    {
        WriteByte(s, 0xFF); WriteByte(s, 0xC4);
        int len = 2 + 1 + 16 + symbols.Length;
        WriteShortBE(s, len);
        WriteByte(s, (byte)((tableClass << 4) | tableId));
        for (int i = 0; i < 16; i++) WriteByte(s, counts[i]);
        s.Write(symbols, 0, symbols.Length);
    }

    private static void WriteSos(Stream s, int channels)
    {
        WriteByte(s, 0xFF); WriteByte(s, 0xDA);
        int len = 6 + 2 * channels;
        WriteShortBE(s, len);
        WriteByte(s, (byte)channels);
        if (channels == 1)
        {
            WriteByte(s, 1); WriteByte(s, 0x00); // Y, DC tbl 0, AC tbl 0
        }
        else
        {
            WriteByte(s, 1); WriteByte(s, 0x00);
            WriteByte(s, 2); WriteByte(s, 0x11);
            WriteByte(s, 3); WriteByte(s, 0x11);
        }
        WriteByte(s, 0);  // Ss = 0 (baseline)
        WriteByte(s, 63); // Se = 63
        WriteByte(s, 0);  // AhAl = 0
    }

    // ---- Block encoder ----

    /// <summary>Per-component entropy state — DC predictor across blocks.</summary>
    private sealed class ComponentState { public int LastDc; }

    /// <summary>
    /// Encode a single 8×8 block: forward DCT, quantize, zigzag, Huffman
    /// encode. Updates <see cref="ComponentState.LastDc"/> for differential
    /// DC coding. Block input is level-shifted (-128) per spec — caller
    /// supplies samples in 0..255 range.
    /// </summary>
    private static void EncodeBlock(BitWriter bits, double[] block, byte[] qtable,
        ComponentState state, byte[] dcCounts, byte[] dcSymbols, byte[] acCounts, byte[] acSymbols)
    {
        // Forward DCT — type-II 8×8, orthonormal scaling.
        var dct = new double[64];
        ForwardDct(block, dct);

        // Quantize to integer coefficients (round-to-nearest).
        var coef = new int[64];
        for (int k = 0; k < 64; k++)
        {
            int q = qtable[k];
            coef[k] = (int)Math.Round(dct[k] / q);
        }

        // DC: differential coding.
        int dcDiff = coef[0] - state.LastDc;
        state.LastDc = coef[0];
        EncodeDc(bits, dcDiff, dcCounts, dcSymbols);

        // AC: zigzag scan, run-length-encoded with Huffman codes.
        EncodeAc(bits, coef, acCounts, acSymbols);
    }

    private static void EncodeDc(BitWriter bits, int diff, byte[] counts, byte[] symbols)
    {
        int absDiff = Math.Abs(diff);
        int size = BitLength(absDiff);
        bits.WriteHuffman(size, counts, symbols);
        if (size > 0) WriteSignMagnitude(bits, diff, size);
    }

    private static void EncodeAc(BitWriter bits, int[] coef, byte[] counts, byte[] symbols)
    {
        int run = 0;
        // Iterate AC coefficients in zigzag order.
        for (int k = 1; k < 64; k++)
        {
            int v = coef[ZigzagOrder[k]];
            if (v == 0)
            {
                run++;
            }
            else
            {
                // Emit ZRL (0xF0) for every run of 16 zeros.
                while (run >= 16)
                {
                    bits.WriteHuffman(0xF0, counts, symbols);
                    run -= 16;
                }
                int size = BitLength(Math.Abs(v));
                int rs = (run << 4) | size;
                bits.WriteHuffman(rs, counts, symbols);
                WriteSignMagnitude(bits, v, size);
                run = 0;
            }
        }
        // Trailing zeros → EOB (0x00).
        if (run > 0) bits.WriteHuffman(0x00, counts, symbols);
    }

    private static void WriteSignMagnitude(BitWriter bits, int v, int size)
    {
        if (v < 0) v += (1 << size) - 1;
        bits.WriteBits(v, size);
    }

    /// <summary>Number of bits needed to represent the magnitude of v.</summary>
    private static int BitLength(int v)
    {
        if (v == 0) return 0;
        int n = 0;
        while (v > 0) { n++; v >>= 1; }
        return n;
    }

    // ---- Greyscale / RGB encoding loops ----

    private static void EncodeGreyscale(BitWriter bits, byte[] pixels, int W, int H, byte[] lumQ)
    {
        var state = new ComponentState();
        var block = new double[64];
        for (int by = 0; by < H; by += 8)
        {
            for (int bx = 0; bx < W; bx += 8)
            {
                FillGreyBlock(pixels, W, H, bx, by, block);
                EncodeBlock(bits, block, lumQ, state,
                    StdLumDcCounts, StdLumDcSymbols, StdLumAcCounts, StdLumAcSymbols);
            }
        }
    }

    private static void EncodeRgbAs420(BitWriter bits, byte[] pixels, int W, int H,
        byte[] lumQ, byte[] chrQ)
    {
        // 4:2:0 MCU = 16×16 pixels = four 8×8 Y blocks + 8×8 Cb + 8×8 Cr
        // (chroma subsampled 2× in each axis).
        var yState = new ComponentState();
        var cbState = new ComponentState();
        var crState = new ComponentState();
        var yBlock = new double[64];
        var cbBlock = new double[64];
        var crBlock = new double[64];

        for (int my = 0; my < H; my += 16)
        {
            for (int mx = 0; mx < W; mx += 16)
            {
                // Four Y blocks, raster order within the MCU.
                for (int by = 0; by < 2; by++)
                {
                    for (int bx = 0; bx < 2; bx++)
                    {
                        FillYBlock(pixels, W, H, mx + bx * 8, my + by * 8, yBlock);
                        EncodeBlock(bits, yBlock, lumQ, yState,
                            StdLumDcCounts, StdLumDcSymbols, StdLumAcCounts, StdLumAcSymbols);
                    }
                }
                // One subsampled Cb + one subsampled Cr.
                FillChromaBlock(pixels, W, H, mx, my, cbBlock, crBlock);
                EncodeBlock(bits, cbBlock, chrQ, cbState,
                    StdChrDcCounts, StdChrDcSymbols, StdChrAcCounts, StdChrAcSymbols);
                EncodeBlock(bits, crBlock, chrQ, crState,
                    StdChrDcCounts, StdChrDcSymbols, StdChrAcCounts, StdChrAcSymbols);
            }
        }
    }

    /// <summary>Fill an 8×8 block with greyscale samples, edge-clamped + level-shifted.</summary>
    private static void FillGreyBlock(byte[] pixels, int W, int H, int x0, int y0, double[] block)
    {
        for (int y = 0; y < 8; y++)
        {
            int sy = Math.Min(y0 + y, H - 1);
            int rowBase = sy * W;
            for (int x = 0; x < 8; x++)
            {
                int sx = Math.Min(x0 + x, W - 1);
                block[y * 8 + x] = pixels[rowBase + sx] - 128.0;
            }
        }
    }

    /// <summary>Fill an 8×8 Y block from RGB pixels, edge-clamped + level-shifted.</summary>
    private static void FillYBlock(byte[] pixels, int W, int H, int x0, int y0, double[] block)
    {
        for (int y = 0; y < 8; y++)
        {
            int sy = Math.Min(y0 + y, H - 1);
            int rowBase = sy * W * 3;
            for (int x = 0; x < 8; x++)
            {
                int sx = Math.Min(x0 + x, W - 1);
                int o = rowBase + sx * 3;
                int r = pixels[o], g = pixels[o + 1], b = pixels[o + 2];
                double Y = 0.299 * r + 0.587 * g + 0.114 * b;
                block[y * 8 + x] = Y - 128.0;
            }
        }
    }

    /// <summary>
    /// Fill 8×8 Cb and Cr blocks — each subsampled 2× from a 16×16
    /// region of the source. Box-filter average over the 2×2 luma
    /// neighbourhood per output pel.
    /// </summary>
    private static void FillChromaBlock(byte[] pixels, int W, int H, int x0, int y0,
        double[] cbBlock, double[] crBlock)
    {
        for (int y = 0; y < 8; y++)
        {
            int sy0 = Math.Min(y0 + y * 2 + 0, H - 1);
            int sy1 = Math.Min(y0 + y * 2 + 1, H - 1);
            for (int x = 0; x < 8; x++)
            {
                int sx0 = Math.Min(x0 + x * 2 + 0, W - 1);
                int sx1 = Math.Min(x0 + x * 2 + 1, W - 1);
                double cbSum = 0, crSum = 0;
                for (int yy = 0; yy < 2; yy++)
                {
                    int sy = yy == 0 ? sy0 : sy1;
                    int rowBase = sy * W * 3;
                    for (int xx = 0; xx < 2; xx++)
                    {
                        int sx = xx == 0 ? sx0 : sx1;
                        int o = rowBase + sx * 3;
                        int r = pixels[o], g = pixels[o + 1], b = pixels[o + 2];
                        cbSum += -0.168736 * r - 0.331264 * g + 0.5 * b;
                        crSum += 0.5 * r - 0.418688 * g - 0.081312 * b;
                    }
                }
                cbBlock[y * 8 + x] = cbSum / 4.0; // already centred at 0 (no -128)
                crBlock[y * 8 + x] = crSum / 4.0;
            }
        }
    }

    /// <summary>Forward 8×8 type-II DCT, naive O(N²) — symmetric with PureJpegDecoder's IDCT.</summary>
    private static void ForwardDct(double[] block, double[] dct)
    {
        Span<double> tmp = stackalloc double[64];
        // Pass 1: rows.
        for (int v = 0; v < 8; v++)
        {
            for (int u = 0; u < 8; u++)
            {
                double sum = 0;
                for (int x = 0; x < 8; x++)
                    sum += block[v * 8 + x] * Math.Cos((2 * x + 1) * u * Math.PI / 16);
                double cu = u == 0 ? InvSqrt8 : 0.5;
                tmp[v * 8 + u] = sum * cu;
            }
        }
        // Pass 2: cols.
        for (int u = 0; u < 8; u++)
        {
            for (int v = 0; v < 8; v++)
            {
                double sum = 0;
                for (int y = 0; y < 8; y++)
                    sum += tmp[y * 8 + u] * Math.Cos((2 * y + 1) * v * Math.PI / 16);
                double cv = v == 0 ? InvSqrt8 : 0.5;
                dct[v * 8 + u] = sum * cv;
            }
        }
    }

    private static readonly double InvSqrt8 = 1.0 / Math.Sqrt(8);

    // ---- Quality scaling ----

    /// <summary>JPEG Annex K quality scaling: produce a per-quality table.</summary>
    private static byte[] ScaleQuantTable(byte[] table, int quality)
    {
        int s = quality < 50 ? 5000 / quality : 200 - 2 * quality;
        var scaled = new byte[64];
        for (int i = 0; i < 64; i++)
        {
            int v = (table[i] * s + 50) / 100;
            scaled[i] = (byte)Math.Clamp(v, 1, 255);
        }
        return scaled;
    }

    // ---- Standard tables (JPEG Annex K) ----

    private static readonly byte[] StdLumQuant = new byte[]
    {
        16, 11, 10, 16, 24, 40, 51, 61,
        12, 12, 14, 19, 26, 58, 60, 55,
        14, 13, 16, 24, 40, 57, 69, 56,
        14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77,
        24, 35, 55, 64, 81, 104, 113, 92,
        49, 64, 78, 87, 103, 121, 120, 101,
        72, 92, 95, 98, 112, 100, 103, 99,
    };

    private static readonly byte[] StdChrQuant = new byte[]
    {
        17, 18, 24, 47, 99, 99, 99, 99,
        18, 21, 26, 66, 99, 99, 99, 99,
        24, 26, 56, 99, 99, 99, 99, 99,
        47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
    };

    private static readonly byte[] ZigzagOrder = new byte[]
    {
         0,  1,  8, 16,  9,  2,  3, 10,
        17, 24, 32, 25, 18, 11,  4,  5,
        12, 19, 26, 33, 40, 48, 41, 34,
        27, 20, 13,  6,  7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36,
        29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63,
    };

    // Standard JPEG Annex K Huffman encoding tables. Counts arrays are
    // 16 entries (one per code length 1..16); symbols arrays are the
    // ordered-by-code-length symbol assignments.
    private static readonly byte[] StdLumDcCounts = new byte[]
        { 0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0 };
    private static readonly byte[] StdLumDcSymbols = new byte[]
        { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };

    private static readonly byte[] StdChrDcCounts = new byte[]
        { 0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0 };
    private static readonly byte[] StdChrDcSymbols = new byte[]
        { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };

    private static readonly byte[] StdLumAcCounts = new byte[]
        { 0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 125 };
    private static readonly byte[] StdLumAcSymbols = new byte[]
    {
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12,
        0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
        0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xA1, 0x08,
        0x23, 0x42, 0xB1, 0xC1, 0x15, 0x52, 0xD1, 0xF0,
        0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0A, 0x16,
        0x17, 0x18, 0x19, 0x1A, 0x25, 0x26, 0x27, 0x28,
        0x29, 0x2A, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39,
        0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
        0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59,
        0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
        0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79,
        0x7A, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
        0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98,
        0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7,
        0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6,
        0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3, 0xC4, 0xC5,
        0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4,
        0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xE1, 0xE2,
        0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA,
        0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
        0xF9, 0xFA,
    };

    private static readonly byte[] StdChrAcCounts = new byte[]
        { 0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 119 };
    private static readonly byte[] StdChrAcSymbols = new byte[]
    {
        0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21,
        0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71,
        0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91,
        0xA1, 0xB1, 0xC1, 0x09, 0x23, 0x33, 0x52, 0xF0,
        0x15, 0x62, 0x72, 0xD1, 0x0A, 0x16, 0x24, 0x34,
        0xE1, 0x25, 0xF1, 0x17, 0x18, 0x19, 0x1A, 0x26,
        0x27, 0x28, 0x29, 0x2A, 0x35, 0x36, 0x37, 0x38,
        0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48,
        0x49, 0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58,
        0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68,
        0x69, 0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78,
        0x79, 0x7A, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
        0x88, 0x89, 0x8A, 0x92, 0x93, 0x94, 0x95, 0x96,
        0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5,
        0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4,
        0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3,
        0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2,
        0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA,
        0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9,
        0xEA, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
        0xF9, 0xFA,
    };

    // ---- Bit writer ----

    /// <summary>
    /// MSB-first bit writer with JPEG byte-stuffing — every literal
    /// 0xFF in the bitstream gets a 0x00 byte appended so the decoder
    /// can distinguish from real markers.
    /// </summary>
    private sealed class BitWriter
    {
        private readonly Stream _s;
        private uint _accum;
        private int _accumBits;

        public BitWriter(Stream s) { _s = s; }

        public void WriteBits(int value, int bitCount)
        {
            // Mask + accumulate.
            uint v = (uint)(value & ((1 << bitCount) - 1));
            _accum = (_accum << bitCount) | v;
            _accumBits += bitCount;
            // Flush whole bytes top-down.
            while (_accumBits >= 8)
            {
                byte b = (byte)((_accum >> (_accumBits - 8)) & 0xFF);
                _s.WriteByte(b);
                if (b == 0xFF) _s.WriteByte(0x00); // byte-stuffing
                _accumBits -= 8;
            }
        }

        public void WriteHuffman(int symbol, byte[] counts, byte[] symbols)
        {
            // Walk bit-length tiers in order, find the symbol's position.
            int idx = 0;
            int code = 0;
            for (int len = 1; len <= 16; len++)
            {
                int n = counts[len - 1];
                for (int i = 0; i < n; i++)
                {
                    if (symbols[idx] == symbol)
                    {
                        WriteBits(code, len);
                        return;
                    }
                    idx++;
                    code++;
                }
                code <<= 1;
            }
            throw new InvalidOperationException($"Symbol {symbol:X} not in Huffman table");
        }

        /// <summary>Pad the remaining bits with 1s and flush. Called once at scan end.</summary>
        public void Flush()
        {
            if (_accumBits > 0)
            {
                int pad = 8 - _accumBits;
                _accum = (_accum << pad) | (uint)((1 << pad) - 1);
                byte b = (byte)(_accum & 0xFF);
                _s.WriteByte(b);
                if (b == 0xFF) _s.WriteByte(0x00);
                _accumBits = 0;
                _accum = 0;
            }
        }
    }
}
