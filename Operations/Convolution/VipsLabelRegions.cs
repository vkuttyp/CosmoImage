using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Convolution;

/// <summary>
/// Connected-component labelling. Each maximal 4-connected region of
/// non-zero pixels gets a unique label (1, 2, 3, …); background stays
/// 0. Mirrors libvips <c>vips_labelregions</c>.
///
/// <para>Output is UInt 1-band — 32-bit labels. Common downstream uses
/// are size filtering, centroid computation, and per-region statistics
/// (the latter typically via <c>HistFind</c> over the label image).</para>
///
/// <para>Implementation: classic two-pass union-find on a flattened
/// pixel grid, with rank-by-size and path compression. Single-band
/// UChar input only; 4-connectivity (libvips' default).</para>
/// </summary>
public class VipsLabelRegions : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public int RegionCount { get; private set; }

    public override int Build()
    {
        if (In == null) return -1;
        if (In.BandFormat != VipsBandFormat.UChar) return -1;
        if (In.Bands != 1) return -1;

        byte[] pixels;
        if (In.Pixels is { } existing) pixels = existing;
        else
        {
            var sink = new MemorySink(In);
            sink.RunAsync().GetAwaiter().GetResult();
            pixels = sink.Pixels;
        }

        int W = In.Width, H = In.Height;
        var (labels, count) = TwoPassLabel(pixels, W, H);
        RegionCount = count;

        // Pack as UInt little-endian bytes.
        var labelBytes = new byte[W * H * 4];
        for (int i = 0; i < W * H; i++)
            BitConverter.TryWriteBytes(labelBytes.AsSpan(i * 4, 4), labels[i]);

        Out = new VipsImage
        {
            Width = W, Height = H, Bands = 1,
            BandFormat = VipsBandFormat.UInt,
            Interpretation = VipsInterpretation.BW,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In, ClientB = labelBytes,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("LabelRegions", RuntimeHelpers.GetHashCode(In));

    private static (uint[] labels, int count) TwoPassLabel(byte[] pixels, int W, int H)
    {
        // Union-find. parent[i] is a provisional label id; ids start at 1.
        var labels = new uint[W * H];
        var parent = new System.Collections.Generic.List<uint> { 0 }; // sentinel for label 0

        uint Find(uint x)
        {
            while (parent[(int)x] != x)
            {
                parent[(int)x] = parent[(int)parent[(int)x]]; // path compression
                x = parent[(int)x];
            }
            return x;
        }

        void Union(uint a, uint b)
        {
            uint ra = Find(a), rb = Find(b);
            if (ra == rb) return;
            // Lower-id becomes the root for stability.
            if (ra < rb) parent[(int)rb] = ra;
            else parent[(int)ra] = rb;
        }

        // Pass 1: assign provisional labels and union neighbours.
        uint nextId = 1;
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                if (pixels[i] == 0) continue;
                uint up = y > 0 ? labels[i - W] : 0;
                uint left = x > 0 ? labels[i - 1] : 0;
                if (up == 0 && left == 0)
                {
                    labels[i] = nextId;
                    parent.Add(nextId);
                    nextId++;
                }
                else if (up == 0) labels[i] = left;
                else if (left == 0) labels[i] = up;
                else
                {
                    labels[i] = Math.Min(up, left);
                    if (up != left) Union(up, left);
                }
            }
        }

        // Pass 2: replace provisional labels with their root, then
        // remap roots into a dense 1..K range.
        var remap = new System.Collections.Generic.Dictionary<uint, uint>();
        uint nextDense = 1;
        for (int i = 0; i < W * H; i++)
        {
            if (labels[i] == 0) continue;
            uint root = Find(labels[i]);
            if (!remap.TryGetValue(root, out var dense))
            {
                dense = nextDense++;
                remap[root] = dense;
            }
            labels[i] = dense;
        }
        return (labels, (int)(nextDense - 1));
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var labelBytes = (byte[])b!;
        VipsImage @out = outRegion.Image;
        VipsRect r = outRegion.Valid;
        int W = @out.Width;

        for (int y = 0; y < r.Height; y++)
        {
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            int srcOff = ((r.Top + y) * W + r.Left) * 4;
            labelBytes.AsSpan(srcOff, r.Width * 4).CopyTo(outAddr);
        }
        return 0;
    }
}
