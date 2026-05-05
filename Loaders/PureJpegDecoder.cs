using System;
using System.Buffers.Binary;

namespace CosmoImage.Loaders;

/// <summary>
/// Pure-managed baseline JPEG decoder. Handles the dominant JPEG
/// subset on the web: SOF0 baseline sequential, 8-bit per sample,
/// Huffman-coded, 1 component (greyscale) or 3 components
/// (YCbCr 4:4:4 / 4:2:2 / 4:2:0 subsampling). Restart markers
/// (RST0..RST7) and standard zigzag + IDCT supported.
///
/// <para>Unsupported variants — progressive (SOF2), hierarchical (SOF5),
/// arithmetic-coded (SOF9..SOF15), 12-bit precision, lossless JPEG —
/// return <c>null</c> so the caller falls back to the JpegLibrary
/// path. The fast path is the goal: the long tail of exotic
/// JPEG variants stays on the well-tested third-party decoder.</para>
///
/// <para>Output is row-major interleaved RGB (3-band) for YCbCr inputs
/// and 1-band greyscale for single-component inputs. Pixel buffer is
/// <c>width * height * channels</c> bytes.</para>
/// </summary>
internal static class PureJpegDecoder
{
    /// <summary>
    /// Attempt to decode <paramref name="jpeg"/>. Returns null when the
    /// stream uses a JPEG variant we don't handle — caller should fall
    /// back to JpegLibrary in that case.
    /// </summary>
    public static byte[]? TryDecode(byte[] jpeg, out int width, out int height, out int channels)
    {
        width = height = channels = 0;
        if (jpeg == null || jpeg.Length < 4) return null;
        if (jpeg[0] != 0xFF || jpeg[1] != 0xD8) return null;

        var ctx = new Context();
        int p = 2;

        // Scan markers until SOS (start of scan), then enter entropy
        // decode. Skip APPn / COM segments — they're metadata we
        // don't interpret here.
        while (p + 1 < jpeg.Length)
        {
            if (jpeg[p] != 0xFF) return null;
            byte marker = jpeg[p + 1];
            p += 2;

            // Standalone markers (no length / payload).
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7) || marker == 0xD9)
                continue;

            // Read segment length (big-endian, includes itself).
            if (p + 2 > jpeg.Length) return null;
            int segLen = (jpeg[p] << 8) | jpeg[p + 1];
            if (segLen < 2 || p + segLen > jpeg.Length) return null;
            int segEnd = p + segLen;
            int segDataStart = p + 2;

            switch (marker)
            {
                case 0xC0: // SOF0 — baseline sequential
                    if (!ParseSof0(jpeg, segDataStart, segEnd, ctx)) return null;
                    break;
                case 0xC2: // SOF2 — progressive (unsupported here)
                case 0xC1: // SOF1 — extended sequential
                case 0xC3: // SOF3 — lossless
                case 0xC5: case 0xC6: case 0xC7:
                case 0xC9: case 0xCA: case 0xCB:
                case 0xCD: case 0xCE: case 0xCF:
                    return null; // bail to JpegLibrary
                case 0xC4: // DHT — Huffman tables
                    if (!ParseDht(jpeg, segDataStart, segEnd, ctx)) return null;
                    break;
                case 0xDB: // DQT — quantization tables
                    if (!ParseDqt(jpeg, segDataStart, segEnd, ctx)) return null;
                    break;
                case 0xDD: // DRI — restart interval
                    if (segEnd - segDataStart < 2) return null;
                    ctx.RestartInterval = (jpeg[segDataStart] << 8) | jpeg[segDataStart + 1];
                    break;
                case 0xDA: // SOS — start of scan, enters entropy-coded data
                    if (!ParseSos(jpeg, segDataStart, segEnd, ctx)) return null;
                    p = segEnd;
                    if (!DecodeScan(jpeg, ref p, ctx)) return null;
                    goto Done;
                default:
                    // Skip APPn / COM / DNL / unknown — metadata and
                    // out-of-scope optional segments.
                    break;
            }
            p = segEnd;
        }

        Done:
        if (ctx.Width == 0 || ctx.Height == 0 || ctx.Components == null) return null;
        width = ctx.Width;
        height = ctx.Height;
        channels = ctx.Components.Length == 1 ? 1 : 3;

        return AssembleOutput(ctx);
    }

    // ---- State containers ----

    private sealed class Context
    {
        public int Width;
        public int Height;
        public Component[]? Components;
        public byte[][] QuantTables = new byte[4][];
        public HuffmanTable[] DcTables = new HuffmanTable[4];
        public HuffmanTable[] AcTables = new HuffmanTable[4];
        public int RestartInterval;

        // Per-component dequantized 8×8 blocks, in raster scan order
        // (block-row × block-col), one entry per block. Filled during
        // DecodeScan; consumed by AssembleOutput.
        public short[][] BlockData = new short[4][];
    }

    private sealed class Component
    {
        public byte Id;
        public byte HSampling;
        public byte VSampling;
        public byte QuantTableId;
        public byte DcTableId;
        public byte AcTableId;
        public int BlocksWide;     // = ceil(width  / (8 * Hmax / H))
        public int BlocksHigh;     // = ceil(height / (8 * Vmax / V))
        public int LastDc;
    }

    private sealed class HuffmanTable
    {
        // Decoder lookup: codes ordered by length (1..16 bits).
        // We use a flat (code → symbol) decode by walking the bit
        // stream and matching against per-length code tables.
        public int[] MinCode = new int[17];     // smallest code at each length
        public int[] MaxCode = new int[17];     // largest code at each length
        public int[] ValOffset = new int[17];   // index into Symbols[]
        public byte[] Symbols = Array.Empty<byte>();
        public bool Valid;
    }

    // ---- Marker parsers ----

    private static bool ParseSof0(byte[] s, int p, int end, Context ctx)
    {
        if (end - p < 6) return false;
        int precision = s[p];
        if (precision != 8) return false; // 12-bit not supported
        ctx.Height = (s[p + 1] << 8) | s[p + 2];
        ctx.Width = (s[p + 3] << 8) | s[p + 4];
        int nComponents = s[p + 5];
        if (nComponents != 1 && nComponents != 3) return false; // greyscale / YCbCr
        if (end - p < 6 + nComponents * 3) return false;

        ctx.Components = new Component[nComponents];
        for (int i = 0; i < nComponents; i++)
        {
            int co = p + 6 + i * 3;
            byte sampling = s[co + 1];
            ctx.Components[i] = new Component
            {
                Id = s[co],
                HSampling = (byte)((sampling >> 4) & 0x0F),
                VSampling = (byte)(sampling & 0x0F),
                QuantTableId = s[co + 2],
            };
            if (ctx.Components[i].HSampling < 1 || ctx.Components[i].HSampling > 4) return false;
            if (ctx.Components[i].VSampling < 1 || ctx.Components[i].VSampling > 4) return false;
        }
        return true;
    }

    private static bool ParseDqt(byte[] s, int p, int end, Context ctx)
    {
        while (p < end)
        {
            int pq = s[p] >> 4;       // precision: 0 = 8-bit, 1 = 16-bit
            int tq = s[p] & 0x0F;     // table id (0..3)
            p++;
            if (tq > 3) return false;
            int tableSize = pq == 0 ? 64 : 128;
            if (pq == 1) return false; // 16-bit quant tables not supported
            if (p + tableSize > end) return false;
            var table = new byte[64];
            Buffer.BlockCopy(s, p, table, 0, 64);
            ctx.QuantTables[tq] = table;
            p += tableSize;
        }
        return true;
    }

    private static bool ParseDht(byte[] s, int p, int end, Context ctx)
    {
        while (p < end)
        {
            byte tcth = s[p++];
            int tc = (tcth >> 4) & 0x0F; // 0 = DC, 1 = AC
            int th = tcth & 0x0F;        // table id 0..3
            if (tc > 1 || th > 3) return false;
            if (p + 16 > end) return false;
            var counts = new int[17]; // 1-indexed (counts[1..16])
            int total = 0;
            for (int i = 1; i <= 16; i++) { counts[i] = s[p + i - 1]; total += counts[i]; }
            p += 16;
            if (p + total > end || total > 256) return false;
            var symbols = new byte[total];
            Buffer.BlockCopy(s, p, symbols, 0, total);
            p += total;

            var table = BuildHuffmanTable(counts, symbols);
            if (tc == 0) ctx.DcTables[th] = table;
            else ctx.AcTables[th] = table;
        }
        return true;
    }

    private static bool ParseSos(byte[] s, int p, int end, Context ctx)
    {
        if (ctx.Components == null) return false;
        if (end - p < 1) return false;
        int n = s[p++];
        if (n != ctx.Components.Length) return false; // we don't do partial scans
        if (end - p < n * 2 + 3) return false;
        for (int i = 0; i < n; i++)
        {
            byte id = s[p++];
            byte tdta = s[p++];
            var comp = FindComponent(ctx, id);
            if (comp == null) return false;
            comp.DcTableId = (byte)((tdta >> 4) & 0x0F);
            comp.AcTableId = (byte)(tdta & 0x0F);
        }
        // Ss / Se / AhAl follow but are baseline-fixed (0, 63, 0).
        return true;
    }

    private static Component? FindComponent(Context ctx, byte id)
    {
        if (ctx.Components == null) return null;
        foreach (var c in ctx.Components) if (c.Id == id) return c;
        return null;
    }

    /// <summary>
    /// Build a JPEG-style Huffman table per ITU-T T.81 Annex C — codes
    /// are derived from the bit-length counts (no explicit codes in the
    /// stream); MinCode / MaxCode / ValOffset arrays let the decoder
    /// match a bit-prefix against per-length code ranges in O(16).
    /// </summary>
    private static HuffmanTable BuildHuffmanTable(int[] counts, byte[] symbols)
    {
        var t = new HuffmanTable { Symbols = symbols };
        var codes = new int[symbols.Length];
        int idx = 0;
        int code = 0;
        for (int len = 1; len <= 16; len++)
        {
            for (int i = 0; i < counts[len]; i++)
            {
                codes[idx++] = code++;
            }
            code <<= 1;
        }

        // Per-length min/max + offset into the symbol table.
        idx = 0;
        for (int len = 1; len <= 16; len++)
        {
            if (counts[len] == 0)
            {
                t.MinCode[len] = -1;
                t.MaxCode[len] = -1;
                t.ValOffset[len] = 0;
                continue;
            }
            t.ValOffset[len] = idx - codes[idx];
            t.MinCode[len] = codes[idx];
            idx += counts[len];
            t.MaxCode[len] = codes[idx - 1];
        }
        t.Valid = true;
        return t;
    }

    // ---- Entropy-coded scan decode ----

    private static bool DecodeScan(byte[] s, ref int p, Context ctx)
    {
        if (ctx.Components == null) return false;

        // MCU sizing: max sampling factors set the MCU dimensions in 8×8 blocks.
        int hMax = 0, vMax = 0;
        foreach (var c in ctx.Components)
        {
            if (c.HSampling > hMax) hMax = c.HSampling;
            if (c.VSampling > vMax) vMax = c.VSampling;
        }
        int mcuWidth = hMax * 8;
        int mcuHeight = vMax * 8;
        int mcusAcross = (ctx.Width + mcuWidth - 1) / mcuWidth;
        int mcusDown = (ctx.Height + mcuHeight - 1) / mcuHeight;

        // Per-component block-grid dims and storage.
        for (int i = 0; i < ctx.Components.Length; i++)
        {
            var c = ctx.Components[i];
            c.BlocksWide = mcusAcross * c.HSampling;
            c.BlocksHigh = mcusDown * c.VSampling;
            ctx.BlockData[i] = new short[c.BlocksWide * c.BlocksHigh * 64];
            c.LastDc = 0;
        }

        var br = new BitReader(s, p);
        int mcusDecoded = 0;
        int restartCounter = ctx.RestartInterval;

        for (int my = 0; my < mcusDown; my++)
        {
            for (int mx = 0; mx < mcusAcross; mx++)
            {
                for (int ci = 0; ci < ctx.Components.Length; ci++)
                {
                    var comp = ctx.Components[ci];
                    for (int by = 0; by < comp.VSampling; by++)
                    {
                        for (int bx = 0; bx < comp.HSampling; bx++)
                        {
                            int blockX = mx * comp.HSampling + bx;
                            int blockY = my * comp.VSampling + by;
                            int blockIdx = blockY * comp.BlocksWide + blockX;
                            var block = new short[64];
                            if (!DecodeBlock(br, ctx, comp, block)) return false;
                            // Dequantize in-place against the comp's quant table.
                            var qt = ctx.QuantTables[comp.QuantTableId];
                            if (qt == null) return false;
                            for (int k = 0; k < 64; k++) block[k] = (short)(block[k] * qt[k]);
                            // Store in zigzag-ordered slot; IDCT happens at output.
                            Buffer.BlockCopy(block, 0, ctx.BlockData[ci], blockIdx * 64 * 2, 64 * 2);
                        }
                    }
                }
                mcusDecoded++;

                // Restart marker handling. Decoder must consume RSTn and
                // reset DC predictors + bit reader at every restart
                // interval boundary.
                if (ctx.RestartInterval > 0 && --restartCounter == 0
                    && mcusDecoded < mcusAcross * mcusDown)
                {
                    br.ConsumeRestartMarker();
                    foreach (var c in ctx.Components) c.LastDc = 0;
                    restartCounter = ctx.RestartInterval;
                }
            }
        }
        p = br.Position;
        return true;
    }

    private static bool DecodeBlock(BitReader br, Context ctx, Component comp, short[] block)
    {
        var dcTbl = ctx.DcTables[comp.DcTableId];
        var acTbl = ctx.AcTables[comp.AcTableId];
        if (!dcTbl.Valid || !acTbl.Valid) return false;

        // DC: differential coding; emit (LastDc + diff) at zigzag[0].
        int dcSize = DecodeHuffmanSymbol(br, dcTbl);
        if (dcSize < 0) return false;
        int dcDiff = dcSize == 0 ? 0 : br.ReceiveExtend(dcSize);
        comp.LastDc += dcDiff;
        block[0] = (short)comp.LastDc;

        // AC: 8-bit (run, size); 0x00 = EOB, 0xF0 = ZRL (zero-run of 16).
        int k = 1;
        while (k < 64)
        {
            int rs = DecodeHuffmanSymbol(br, acTbl);
            if (rs < 0) return false;
            int run = (rs >> 4) & 0x0F;
            int size = rs & 0x0F;
            if (size == 0)
            {
                if (run == 15) { k += 16; continue; } // ZRL
                break; // EOB
            }
            k += run;
            if (k >= 64) return false;
            int coef = br.ReceiveExtend(size);
            block[k++] = (short)coef;
        }
        return true;
    }

    private static int DecodeHuffmanSymbol(BitReader br, HuffmanTable tbl)
    {
        // Walk bit-by-bit; at each length, check if the accumulated code
        // falls in the valid range for that length.
        int code = 0;
        for (int len = 1; len <= 16; len++)
        {
            int bit = br.ReadBit();
            if (bit < 0) return -1;
            code = (code << 1) | bit;
            if (tbl.MaxCode[len] < 0) continue;
            if (code <= tbl.MaxCode[len])
            {
                int idx = tbl.ValOffset[len] + code;
                if (idx < 0 || idx >= tbl.Symbols.Length) return -1;
                return tbl.Symbols[idx];
            }
        }
        return -1;
    }

    // ---- IDCT + output assembly ----

    /// <summary>
    /// Run the inverse 8×8 DCT on every block, level-shift +128, then
    /// upsample chroma to luma resolution and convert to RGB
    /// (or pass through grayscale).
    /// </summary>
    private static byte[]? AssembleOutput(Context ctx)
    {
        if (ctx.Components == null) return null;
        int W = ctx.Width;
        int H = ctx.Height;
        int n = ctx.Components.Length;

        // Reconstruct each component to a per-component pixel plane at
        // luma resolution (upsampling chroma via nearest-neighbour
        // replication — the cheapest path; bilinear is a follow-up).
        var planes = new byte[n][];
        int hMax = 0, vMax = 0;
        foreach (var c in ctx.Components)
        {
            if (c.HSampling > hMax) hMax = c.HSampling;
            if (c.VSampling > vMax) vMax = c.VSampling;
        }
        int paddedW = ((W + 8 * hMax - 1) / (8 * hMax)) * 8 * hMax;
        int paddedH = ((H + 8 * vMax - 1) / (8 * vMax)) * 8 * vMax;

        for (int ci = 0; ci < n; ci++)
        {
            var comp = ctx.Components[ci];
            int compFullW = paddedW * comp.HSampling / hMax;
            int compFullH = paddedH * comp.VSampling / vMax;
            var compPlane = new byte[compFullW * compFullH];

            // IDCT each 8×8 block and place into the component plane.
            var spatial = new double[64];
            for (int by = 0; by < comp.BlocksHigh; by++)
            {
                for (int bx = 0; bx < comp.BlocksWide; bx++)
                {
                    int blockIdx = (by * comp.BlocksWide + bx) * 64;
                    // Inverse zigzag + IDCT.
                    InverseZigzagAndIdct(ctx.BlockData[ci], blockIdx, spatial);
                    int planeX = bx * 8;
                    int planeY = by * 8;
                    for (int y = 0; y < 8; y++)
                    {
                        int outRow = (planeY + y) * compFullW + planeX;
                        for (int x = 0; x < 8; x++)
                        {
                            // Level shift: JPEG stores -128..127 around the
                            // DC; +128 brings it back into 0..255.
                            int v = (int)(spatial[y * 8 + x]) + 128;
                            compPlane[outRow + x] = (byte)Math.Clamp(v, 0, 255);
                        }
                    }
                }
            }

            // Upsample to (paddedW, paddedH) by nearest-neighbour
            // replication. For 4:4:4 sub-sampling the plane is already
            // at full resolution — the loop is a no-op copy.
            int xRep = hMax / comp.HSampling;
            int yRep = vMax / comp.VSampling;
            if (xRep == 1 && yRep == 1)
            {
                planes[ci] = compPlane;
            }
            else
            {
                var full = new byte[paddedW * paddedH];
                for (int y = 0; y < paddedH; y++)
                {
                    int srcY = y / yRep;
                    if (srcY >= compFullH) srcY = compFullH - 1;
                    for (int x = 0; x < paddedW; x++)
                    {
                        int srcX = x / xRep;
                        if (srcX >= compFullW) srcX = compFullW - 1;
                        full[y * paddedW + x] = compPlane[srcY * compFullW + srcX];
                    }
                }
                planes[ci] = full;
            }
        }

        // Crop padded planes to actual W × H + interleave. We return
        // RAW component samples (Y, Cb, Cr or greyscale) interleaved
        // per-pixel — exactly the shape JpegLibrary produces. The
        // caller's ConvertColorSpace handles YCbCr → RGB conversion
        // so the conversion logic stays centralised.
        int outChannels = n == 1 ? 1 : 3;
        var output = new byte[W * H * outChannels];
        if (n == 1)
        {
            for (int y = 0; y < H; y++)
                Buffer.BlockCopy(planes[0], y * paddedW, output, y * W, W);
        }
        else
        {
            for (int y = 0; y < H; y++)
            {
                int srcRow = y * paddedW;
                int dstRow = y * W * 3;
                for (int x = 0; x < W; x++)
                {
                    output[dstRow + x * 3 + 0] = planes[0][srcRow + x];
                    output[dstRow + x * 3 + 1] = planes[1][srcRow + x];
                    output[dstRow + x * 3 + 2] = planes[2][srcRow + x];
                }
            }
        }
        return output;
    }

    // Standard JPEG zigzag — index k in stream-order maps to ZigZag[k] in row-major.
    private static readonly byte[] ZigZag = new byte[]
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

    /// <summary>
    /// Inverse zigzag the 64-coefficient block (stream order →
    /// row-major) and apply the 8×8 IDCT in-place. Output is in
    /// <paramref name="spatial"/> in row-major order, level-shifted
    /// to be added to 128 by the caller.
    /// </summary>
    private static void InverseZigzagAndIdct(short[] blockData, int blockOffset, double[] spatial)
    {
        Span<double> rowMajor = stackalloc double[64];
        for (int k = 0; k < 64; k++)
            rowMajor[ZigZag[k]] = blockData[blockOffset + k];

        // Type-II 8×8 inverse DCT — orthonormal scaling. Naive O(N²)
        // per dimension; fast enough for typical resolutions and
        // dramatically simpler than the AAN factorisation.
        Span<double> tmp = stackalloc double[64];
        for (int v = 0; v < 8; v++)
        {
            for (int u = 0; u < 8; u++)
            {
                double sum = 0;
                for (int ix = 0; ix < 8; ix++)
                {
                    double cu = ix == 0 ? InvSqrt8 : Half;
                    sum += cu * rowMajor[v * 8 + ix] *
                        Math.Cos((2 * u + 1) * ix * Math.PI / 16);
                }
                tmp[v * 8 + u] = sum;
            }
        }
        for (int u = 0; u < 8; u++)
        {
            for (int y = 0; y < 8; y++)
            {
                double sum = 0;
                for (int iv = 0; iv < 8; iv++)
                {
                    double cv = iv == 0 ? InvSqrt8 : Half;
                    sum += cv * tmp[iv * 8 + u] *
                        Math.Cos((2 * y + 1) * iv * Math.PI / 16);
                }
                spatial[y * 8 + u] = sum;
            }
        }
    }

    private static readonly double InvSqrt8 = 1.0 / Math.Sqrt(8);
    private const double Half = 0.5;

    // ---- Bit reader for entropy-coded segment ----

    /// <summary>
    /// MSB-first bit reader over a JPEG entropy-coded segment.
    /// Handles the 0xFF byte-stuffing rule (0xFF 0x00 → real 0xFF).
    /// Stops cleanly at restart markers (caller invokes
    /// <see cref="ConsumeRestartMarker"/>) and at the EOI marker.
    /// </summary>
    private sealed class BitReader
    {
        private readonly byte[] _data;
        private int _pos;
        private uint _accum;
        private int _accumBits;

        public BitReader(byte[] data, int startPos)
        {
            _data = data;
            _pos = startPos;
        }

        public int Position => _pos;

        public int ReadBit()
        {
            if (_accumBits == 0 && !RefillByte()) return -1;
            int bit = (int)((_accum >> (_accumBits - 1)) & 1);
            _accumBits--;
            return bit;
        }

        /// <summary>Read N bits and sign-extend per JPEG receive-extend semantics.</summary>
        public int ReceiveExtend(int n)
        {
            int v = 0;
            for (int i = 0; i < n; i++)
            {
                int b = ReadBit();
                if (b < 0) return 0;
                v = (v << 1) | b;
            }
            // If the high bit is 0, the original value was negative.
            if (v < (1 << (n - 1))) v += (-1 << n) + 1;
            return v;
        }

        private bool RefillByte()
        {
            while (_accumBits < 8)
            {
                if (_pos >= _data.Length) return _accumBits > 0;
                byte b = _data[_pos++];
                if (b == 0xFF)
                {
                    if (_pos >= _data.Length) return _accumBits > 0;
                    byte next = _data[_pos++];
                    if (next == 0x00)
                    {
                        // Stuffed FF — emit literal 0xFF.
                        _accum = (_accum << 8) | 0xFF;
                        _accumBits += 8;
                    }
                    else
                    {
                        // Real marker (RST / EOI / etc.). Step back so
                        // the higher-level loop can consume it.
                        _pos -= 2;
                        return _accumBits > 0;
                    }
                }
                else
                {
                    _accum = (_accum << 8) | b;
                    _accumBits += 8;
                }
            }
            return true;
        }

        /// <summary>
        /// Consume a restart marker (RST0..RST7) at the current position
        /// and reset the bit accumulator. Caller must reset DC predictors.
        /// </summary>
        public void ConsumeRestartMarker()
        {
            // Drop unread bits to byte boundary, then expect 0xFF Dx.
            _accum = 0;
            _accumBits = 0;
            if (_pos + 2 <= _data.Length && _data[_pos] == 0xFF
                && _data[_pos + 1] >= 0xD0 && _data[_pos + 1] <= 0xD7)
            {
                _pos += 2;
            }
        }
    }
}
