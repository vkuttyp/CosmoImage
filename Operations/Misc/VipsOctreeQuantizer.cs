using System;
using System.Collections.Generic;

namespace CosmoImage.Operations.Misc;

/// <summary>
/// Pure-managed Octree colour quantizer (Gervautz-Purgathofer 1990).
/// Mirrors ImageSharp's <c>OctreeQuantizer</c>. Works on greyscale (1-band),
/// RGB (3-band), and RGBA (4-band) 8-bit input; alpha is preserved
/// unmodified (the octree quantizes the colour channels only). Greyscale
/// uses an equal-population histogram partition rather than the octree
/// (which would degenerate to a 1-D bucket).
///
/// <para>Algorithm (RGB/RGBA): build an octree subdividing the RGB cube
/// one bit per level (depth 8 = full 8-bit precision). Every input pixel
/// is inserted at the deepest level. After all pixels are inserted, the
/// tree is reduced — the deepest non-leaf node with all-leaf children is
/// collapsed into a single leaf, repeating until the leaf count drops to
/// <see cref="Colors"/>. Final palette = leaf representative colours.
/// Each output pixel maps to its leaf via tree traversal.</para>
///
/// <para>Compared to <see cref="MagickQuantizer"/>: octree typically
/// preserves perceptually-distinct colours better (no median-cut
/// "voronoi cells"); slower for very large images. With
/// <see cref="Dither"/> enabled, applies Floyd-Steinberg error diffusion
/// on the colour channels for smoother gradients at the cost of some
/// noise.</para>
/// </summary>
public sealed class VipsOctreeQuantizer : IVipsQuantizer
{
    /// <summary>Maximum number of unique colours in the output. Range 2..256.</summary>
    public int Colors { get; init; } = 256;

    /// <summary>
    /// Apply Floyd-Steinberg error diffusion. Smoother gradient appearance
    /// at the cost of some noise; alpha (if present) is forwarded verbatim
    /// and not error-diffused.
    /// </summary>
    public bool Dither { get; init; } = false;

    private const int MaxDepth = 8;

    public VipsImage Apply(VipsImage input)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (Colors < 2 || Colors > 256)
            throw new ArgumentOutOfRangeException(nameof(Colors), "Colors must be in 2..256");
        if (input.Bands != 1 && input.Bands != 3 && input.Bands != 4)
            throw new ArgumentException("OctreeQuantizer requires 1, 3, or 4 band input", nameof(input));
        if (input.BandFormat != VipsBandFormat.UChar)
            throw new ArgumentException("OctreeQuantizer requires UChar input", nameof(input));

        int w = input.Width, h = input.Height, b = input.Bands;

        // Materialise input pixels.
        byte[] inputPixels;
        if (input.Pixels is { } existing) inputPixels = existing;
        else
        {
            var sink = new MemorySink(input);
            sink.RunAsync().GetAwaiter().GetResult();
            inputPixels = sink.Pixels;
        }

        byte[] outBuf = b == 1
            ? QuantizeGreyscale(inputPixels, w, h, Colors, Dither)
            : QuantizeRgb(inputPixels, w, h, b, Colors, Dither);

        var output = new VipsImage
        {
            Width = w, Height = h, Bands = b,
            BandFormat = VipsBandFormat.UChar,
            Interpretation = input.Interpretation,
            Coding = input.Coding, XRes = input.XRes, YRes = input.YRes,
            PixelsLazy = new Lazy<byte[]>(() => outBuf),
        };
        output.CopyMetadataFrom(input);
        output.SetPipeline(VipsDemandStyle.Any, input);
        return output;
    }

    // ---- RGB / RGBA quantization via the octree ----------------------------

    private static byte[] QuantizeRgb(byte[] src, int w, int h, int bands, int colors, bool dither)
    {
        bool hasAlpha = bands == 4;

        // Pass 1: build octree from all input pixels (skip fully-transparent
        // pixels — their RGB is undefined per spec and they shouldn't bias
        // the palette).
        var tree = new Octree();
        for (int i = 0; i < w * h; i++)
        {
            int off = i * bands;
            if (hasAlpha && src[off + 3] == 0) continue;
            tree.Insert(src[off], src[off + 1], src[off + 2]);
        }

        // Pass 2: reduce.
        while (tree.LeafCount > colors) tree.Reduce();

        // Pass 3: extract a flat palette (R,G,B triples) for fast nearest-
        // neighbour search by Floyd-Steinberg, plus the octree itself for
        // the no-dither fast path.
        var palette = tree.GetPalette();      // length = leafCount × 3

        var outBuf = new byte[w * h * bands];
        if (!dither)
        {
            // Fast path: octree lookup is O(depth=8) per pixel. Palette
            // search would be O(colors).
            for (int i = 0; i < w * h; i++)
            {
                int off = i * bands;
                if (hasAlpha && src[off + 3] == 0)
                {
                    outBuf[off] = outBuf[off + 1] = outBuf[off + 2] = 0;
                    outBuf[off + 3] = 0;
                    continue;
                }
                var (qR, qG, qB) = tree.Lookup(src[off], src[off + 1], src[off + 2]);
                outBuf[off]     = qR;
                outBuf[off + 1] = qG;
                outBuf[off + 2] = qB;
                if (hasAlpha) outBuf[off + 3] = src[off + 3];
            }
            return outBuf;
        }
        FloydSteinbergRgb(src, outBuf, w, h, bands, palette);
        return outBuf;
    }

    private static void FloydSteinbergRgb(
        byte[] src, byte[] dst, int w, int h, int bands, byte[] palette)
    {
        // Working buffers hold short signed RGB so the diffused error can
        // dip below 0 / above 255. Alpha is forwarded verbatim and not
        // error-diffused.
        var workR = new short[w * h];
        var workG = new short[w * h];
        var workB = new short[w * h];
        for (int i = 0, p = 0; i < src.Length; i += bands, p++)
        {
            workR[p] = src[i];
            workG[p] = src[i + 1];
            workB[p] = src[i + 2];
        }

        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int idx = row + x;
                int r = Clamp255(workR[idx]);
                int g = Clamp255(workG[idx]);
                int bl = Clamp255(workB[idx]);

                int p = NearestPaletteEntry(palette, r, g, bl);
                byte qr = palette[p * 3];
                byte qg = palette[p * 3 + 1];
                byte qb = palette[p * 3 + 2];

                int dstOff = idx * bands;
                dst[dstOff]     = qr;
                dst[dstOff + 1] = qg;
                dst[dstOff + 2] = qb;
                if (bands == 4) dst[dstOff + 3] = src[dstOff + 3];

                int errR = r - qr, errG = g - qg, errB = bl - qb;
                if (x + 1 < w)
                {
                    workR[idx + 1] = (short)(workR[idx + 1] + errR * 7 / 16);
                    workG[idx + 1] = (short)(workG[idx + 1] + errG * 7 / 16);
                    workB[idx + 1] = (short)(workB[idx + 1] + errB * 7 / 16);
                }
                if (y + 1 < h)
                {
                    if (x > 0)
                    {
                        workR[idx + w - 1] = (short)(workR[idx + w - 1] + errR * 3 / 16);
                        workG[idx + w - 1] = (short)(workG[idx + w - 1] + errG * 3 / 16);
                        workB[idx + w - 1] = (short)(workB[idx + w - 1] + errB * 3 / 16);
                    }
                    workR[idx + w] = (short)(workR[idx + w] + errR * 5 / 16);
                    workG[idx + w] = (short)(workG[idx + w] + errG * 5 / 16);
                    workB[idx + w] = (short)(workB[idx + w] + errB * 5 / 16);
                    if (x + 1 < w)
                    {
                        workR[idx + w + 1] = (short)(workR[idx + w + 1] + errR * 1 / 16);
                        workG[idx + w + 1] = (short)(workG[idx + w + 1] + errG * 1 / 16);
                        workB[idx + w + 1] = (short)(workB[idx + w + 1] + errB * 1 / 16);
                    }
                }
            }
        }
    }

    private static int NearestPaletteEntry(byte[] palette, int r, int g, int b)
    {
        int best = 0, bestDist = int.MaxValue;
        int n = palette.Length / 3;
        for (int i = 0; i < n; i++)
        {
            int dr = palette[i * 3]     - r;
            int dg = palette[i * 3 + 1] - g;
            int db = palette[i * 3 + 2] - b;
            int d = dr * dr + dg * dg + db * db;
            if (d < bestDist) { bestDist = d; best = i; if (d == 0) break; }
        }
        return best;
    }

    // ---- Greyscale: histogram-equal-population partition --------------------

    private static byte[] QuantizeGreyscale(byte[] src, int w, int h, int colors, bool dither)
    {
        // 1) histogram of pixel intensities
        Span<int> hist = stackalloc int[256];
        foreach (byte v in src) hist[v]++;

        // 2) build palette: split histogram so each bucket holds roughly the
        //    same total pixel count; bucket value = intensity-weighted mean.
        var palette = BuildGreyPalette(hist, colors);

        // 3) LUT for the no-dither fast path.
        var lut = new byte[256];
        for (int v = 0; v < 256; v++) lut[v] = palette[NearestGrey(palette, v)];

        var dst = new byte[src.Length];
        if (!dither)
        {
            for (int i = 0; i < src.Length; i++) dst[i] = lut[src[i]];
            return dst;
        }
        // Floyd-Steinberg on a single channel.
        var work = new short[src.Length];
        for (int i = 0; i < src.Length; i++) work[i] = src[i];
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int idx = row + x;
                int v = Clamp255(work[idx]);
                byte q = palette[NearestGrey(palette, v)];
                dst[idx] = q;
                int err = v - q;
                if (x + 1 < w) work[idx + 1] = (short)(work[idx + 1] + err * 7 / 16);
                if (y + 1 < h)
                {
                    if (x > 0)     work[idx + w - 1] = (short)(work[idx + w - 1] + err * 3 / 16);
                                   work[idx + w]     = (short)(work[idx + w]     + err * 5 / 16);
                    if (x + 1 < w) work[idx + w + 1] = (short)(work[idx + w + 1] + err * 1 / 16);
                }
            }
        }
        return dst;
    }

    private static byte[] BuildGreyPalette(ReadOnlySpan<int> hist, int colors)
    {
        int total = 0;
        for (int i = 0; i < 256; i++) total += hist[i];
        if (total == 0) return new byte[] { 0 };

        var pal = new List<byte>(colors);
        int target = total / colors;
        if (target == 0) target = 1;
        long sumI = 0, sumW = 0;
        int taken = 0;
        for (int i = 0; i < 256; i++)
        {
            int c = hist[i];
            if (c == 0) continue;
            sumI += (long)i * c;
            sumW += c;
            taken += c;
            if (taken >= target && pal.Count < colors - 1)
            {
                pal.Add((byte)(sumI / sumW));
                sumI = 0; sumW = 0; taken = 0;
            }
        }
        if (sumW > 0 && pal.Count < colors) pal.Add((byte)(sumI / sumW));
        if (pal.Count == 0) pal.Add(0);
        pal.Sort();
        return pal.ToArray();
    }

    private static int NearestGrey(byte[] palette, int v)
    {
        int best = 0, bestDist = int.MaxValue;
        for (int i = 0; i < palette.Length; i++)
        {
            int d = palette[i] - v;
            if (d < 0) d = -d;
            if (d < bestDist) { bestDist = d; best = i; if (d == 0) break; }
        }
        return best;
    }

    private static int Clamp255(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

    // ---------- Octree implementation ----------

    private sealed class OctreeNode
    {
        public bool IsLeaf;
        public int PixelCount;
        public int RedSum, GreenSum, BlueSum;
        public OctreeNode?[]? Children;
    }

    private sealed class Octree
    {
        private readonly OctreeNode _root = new();
        // Reducible-node lists by tree level (0..MaxDepth-1). The
        // collapse step picks from the deepest non-empty list.
        private readonly List<OctreeNode>[] _levels;
        public int LeafCount;

        public Octree()
        {
            _levels = new List<OctreeNode>[MaxDepth];
            for (int i = 0; i < MaxDepth; i++) _levels[i] = new List<OctreeNode>();
        }

        public void Insert(byte r, byte g, byte b)
        {
            InsertRecursive(_root, r, g, b, 0);
        }

        private void InsertRecursive(OctreeNode node, byte r, byte g, byte b, int level)
        {
            if (node.IsLeaf || level == MaxDepth)
            {
                if (!node.IsLeaf)
                {
                    node.IsLeaf = true;
                    LeafCount++;
                }
                node.PixelCount++;
                node.RedSum += r;
                node.GreenSum += g;
                node.BlueSum += b;
                return;
            }
            int shift = 7 - level;
            int idx = ((r >> shift) & 1) << 2
                    | ((g >> shift) & 1) << 1
                    | ((b >> shift) & 1);
            node.Children ??= new OctreeNode[8];
            if (node.Children[idx] == null)
            {
                var child = new OctreeNode();
                node.Children[idx] = child;
                // Register this node as reducible at its level. We walk
                // levels deep→shallow in Reduce() looking for a node
                // with all-leaf children to collapse.
                _levels[level].Add(node);
            }
            InsertRecursive(node.Children[idx]!, r, g, b, level + 1);
        }

        /// <summary>
        /// Find the deepest non-leaf node with all-leaf children and
        /// collapse them into a single leaf. Reduces leaf count by
        /// (childCount - 1) per call. Walks levels deep→shallow so we
        /// always merge the most-similar (lowest-significance-bit-
        /// difference) leaves first.
        /// </summary>
        public void Reduce()
        {
            for (int level = MaxDepth - 1; level >= 0; level--)
            {
                var bucket = _levels[level];
                for (int i = bucket.Count - 1; i >= 0; i--)
                {
                    var node = bucket[i];
                    if (node.IsLeaf || node.Children == null)
                    {
                        bucket.RemoveAt(i);
                        continue;
                    }
                    // Only collapse when every existing child is itself
                    // a leaf — otherwise there's still detail underneath
                    // that we'd lose by averaging zeros into the parent.
                    bool allLeaves = true;
                    foreach (var ch in node.Children)
                    {
                        if (ch != null && !ch.IsLeaf) { allLeaves = false; break; }
                    }
                    if (!allLeaves) continue;
                    int childCount = 0;
                    foreach (var ch in node.Children)
                    {
                        if (ch == null) continue;
                        node.PixelCount += ch.PixelCount;
                        node.RedSum += ch.RedSum;
                        node.GreenSum += ch.GreenSum;
                        node.BlueSum += ch.BlueSum;
                        childCount++;
                    }
                    node.IsLeaf = true;
                    node.Children = null;
                    LeafCount -= childCount - 1;
                    bucket.RemoveAt(i);
                    return;
                }
            }
        }

        public (byte r, byte g, byte b) Lookup(byte r, byte g, byte b)
        {
            var node = _root;
            for (int level = 0; level < MaxDepth; level++)
            {
                if (node.IsLeaf) break;
                int shift = 7 - level;
                int idx = ((r >> shift) & 1) << 2
                        | ((g >> shift) & 1) << 1
                        | ((b >> shift) & 1);
                if (node.Children == null || node.Children[idx] == null) break;
                node = node.Children[idx]!;
            }
            int n = Math.Max(1, node.PixelCount);
            return ((byte)(node.RedSum / n),
                    (byte)(node.GreenSum / n),
                    (byte)(node.BlueSum / n));
        }

        /// <summary>
        /// Walk the tree and emit a flat palette of (R,G,B) byte triples,
        /// one per leaf. Used by the Floyd-Steinberg path which needs an
        /// indexable palette for nearest-neighbour search after the
        /// octree's bit-prefix lookup is no longer the right fit (dithering
        /// pushes pixels into "wrong" octree branches).
        /// </summary>
        public byte[] GetPalette()
        {
            var pal = new List<byte>(LeafCount * 3);
            Walk(_root, pal);
            if (pal.Count == 0) return new byte[] { 0, 0, 0 };
            return pal.ToArray();
        }

        private static void Walk(OctreeNode node, List<byte> pal)
        {
            if (node.IsLeaf)
            {
                int n = Math.Max(1, node.PixelCount);
                pal.Add((byte)(node.RedSum   / n));
                pal.Add((byte)(node.GreenSum / n));
                pal.Add((byte)(node.BlueSum  / n));
                return;
            }
            if (node.Children == null) return;
            for (int i = 0; i < 8; i++)
                if (node.Children[i] is { } child) Walk(child, pal);
        }
    }
}
