using System;
using System.Collections.Generic;

namespace CosmoImage.Operations.Misc;

/// <summary>
/// Pure-managed Octree colour quantizer (Gervautz-Purgathofer 1990).
/// Mirrors ImageSharp's <c>OctreeQuantizer</c>. Works on RGB and RGBA
/// 8-bit input; alpha is preserved unmodified (the octree quantizes
/// the colour channels only).
///
/// <para>Algorithm: build an octree subdividing the RGB cube one bit
/// per level (depth 8 = full 8-bit precision). Every input pixel is
/// inserted at the deepest level. After all pixels are inserted, the
/// tree is reduced — the deepest non-leaf node with all-leaf children
/// is collapsed into a single leaf, repeating until the leaf count
/// drops to <see cref="Colors"/>. Final palette = leaf representative
/// colours. Each output pixel maps to its leaf via tree traversal.</para>
///
/// <para>Compared to <see cref="MagickQuantizer"/>: octree typically
/// preserves perceptually-distinct colours better (no median-cut
/// "voronoi cells"); slower for very large images. Both are
/// nearest-neighbour mappers — for dithering, layer Floyd-Steinberg
/// on top (not built into this round).</para>
/// </summary>
public sealed class VipsOctreeQuantizer : IVipsQuantizer
{
    /// <summary>Maximum number of unique colours in the output. Range 2..256.</summary>
    public int Colors { get; init; } = 256;

    private const int MaxDepth = 8;

    public VipsImage Apply(VipsImage input)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (Colors < 2 || Colors > 256)
            throw new ArgumentOutOfRangeException(nameof(Colors), "Colors must be in 2..256");
        if (input.Bands != 3 && input.Bands != 4)
            throw new ArgumentException("OctreeQuantizer requires 3 or 4 band input", nameof(input));
        if (input.BandFormat != VipsBandFormat.UChar)
            throw new ArgumentException("OctreeQuantizer requires UChar input", nameof(input));

        int w = input.Width, h = input.Height, b = input.Bands;
        bool hasAlpha = b == 4;

        // Materialise input pixels.
        byte[] inputPixels;
        if (input.Pixels is { } existing) inputPixels = existing;
        else
        {
            var sink = new MemorySink(input);
            sink.RunAsync().GetAwaiter().GetResult();
            inputPixels = sink.Pixels;
        }

        // Pass 1: build octree from all input pixels.
        var tree = new Octree();
        for (int i = 0; i < w * h; i++)
        {
            byte r = inputPixels[i * b + 0];
            byte g = inputPixels[i * b + 1];
            byte bl = inputPixels[i * b + 2];
            tree.Insert(r, g, bl);
        }

        // Pass 2: reduce until leaf count drops to target.
        while (tree.LeafCount > Colors) tree.Reduce();

        // Pass 3: map each input pixel to its leaf colour.
        var outBuf = new byte[w * h * b];
        for (int i = 0; i < w * h; i++)
        {
            byte r = inputPixels[i * b + 0];
            byte g = inputPixels[i * b + 1];
            byte bl = inputPixels[i * b + 2];
            var (qR, qG, qB) = tree.Lookup(r, g, bl);
            outBuf[i * b + 0] = qR;
            outBuf[i * b + 1] = qG;
            outBuf[i * b + 2] = qB;
            if (hasAlpha) outBuf[i * b + 3] = inputPixels[i * b + 3];
        }

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
    }
}
