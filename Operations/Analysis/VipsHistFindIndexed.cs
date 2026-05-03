using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Analysis;

public enum VipsHistIndexedReduction
{
    Sum = 0,
    Mean = 1,
    Min = 2,
    Max = 3,
}

/// <summary>
/// Per-bin reduction across an image, keyed by an index image.
/// For each bin <c>i</c>, output[i] is the chosen reduction
/// (<see cref="Reduction"/>) over all input pixels where
/// <c>index[x, y] == i</c>. Mirrors libvips
/// <c>vips_hist_find_indexed</c>.
///
/// <para>The standard look-up-table-driven analysis primitive: bin
/// every pixel into a category (e.g. by region label, by hue
/// quantisation) and accumulate per-category statistics in a single
/// pass. Output is a 1D Float image; width = max(index) + 1; bands
/// match the input.</para>
/// </summary>
public class VipsHistFindIndexed : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Index { get; set; }
    public VipsImage? Out { get; set; }
    public VipsHistIndexedReduction Reduction { get; set; } = VipsHistIndexedReduction.Sum;

    public override int Build()
    {
        if (In == null || Index == null) return -1;
        if (Index.BandFormat != VipsBandFormat.UChar || Index.Bands != 1) return -1;
        if (In.Width != Index.Width || In.Height != Index.Height) return -1;
        if (In.BandFormat != VipsBandFormat.UChar && In.BandFormat != VipsBandFormat.Float) return -1;

        // Materialise both.
        byte[] inPixels = MaterialiseAny(In);
        byte[] idxPixels = MaterialiseAny(Index);

        int W = In.Width, H = In.Height, bands = In.Bands;
        bool isFloat = In.BandFormat == VipsBandFormat.Float;
        int sampleSize = isFloat ? 4 : 1;
        int pelBytes = bands * sampleSize;

        // First pass: find max index, allocate accumulator.
        int maxIdx = 0;
        for (int i = 0; i < idxPixels.Length; i++)
            if (idxPixels[i] > maxIdx) maxIdx = idxPixels[i];
        int outW = maxIdx + 1;

        var accum = new double[outW * bands];
        var counts = new long[outW];
        var initialized = new bool[outW];

        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int bin = idxPixels[y * W + x];
                int inOff = (y * W + x) * pelBytes;
                for (int bnd = 0; bnd < bands; bnd++)
                {
                    double v = isFloat
                        ? BinaryPrimitives.ReadSingleLittleEndian(inPixels.AsSpan(inOff + bnd * 4, 4))
                        : inPixels[inOff + bnd];
                    int accOff = bin * bands + bnd;
                    if (!initialized[bin])
                    {
                        accum[accOff] = v;
                    }
                    else
                    {
                        switch (Reduction)
                        {
                            case VipsHistIndexedReduction.Sum:
                            case VipsHistIndexedReduction.Mean:
                                accum[accOff] += v;
                                break;
                            case VipsHistIndexedReduction.Min:
                                if (v < accum[accOff]) accum[accOff] = v;
                                break;
                            case VipsHistIndexedReduction.Max:
                                if (v > accum[accOff]) accum[accOff] = v;
                                break;
                        }
                    }
                }
                initialized[bin] = true;
                counts[bin]++;
            }
        }

        if (Reduction == VipsHistIndexedReduction.Mean)
        {
            for (int b = 0; b < outW; b++)
                if (counts[b] > 0)
                    for (int bnd = 0; bnd < bands; bnd++)
                        accum[b * bands + bnd] /= counts[b];
        }

        var outBytes = new byte[outW * bands * 4];
        for (int i = 0; i < outW * bands; i++)
            BinaryPrimitives.WriteSingleLittleEndian(outBytes.AsSpan(i * 4, 4), (float)accum[i]);

        Out = new VipsImage
        {
            Width = outW, Height = 1, Bands = bands, BandFormat = VipsBandFormat.Float,
            Interpretation = VipsInterpretation.Histogram,
            Coding = In.Coding, XRes = 1, YRes = 1,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In, ClientB = outBytes,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("HistFindIndexed", RuntimeHelpers.GetHashCode(In),
            RuntimeHelpers.GetHashCode(Index), Reduction);

    private static byte[] MaterialiseAny(VipsImage img)
    {
        if (img.Pixels is { } existing) return existing;
        var sink = new MemorySink(img);
        sink.RunAsync().GetAwaiter().GetResult();
        return sink.Pixels;
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var buf = (byte[])b!;
        VipsImage @out = outRegion.Image;
        VipsRect r = outRegion.Valid;
        int bands = @out.Bands;
        var addr = outRegion.GetAddress(r.Left, 0);
        buf.AsSpan(r.Left * bands * 4, r.Width * bands * 4).CopyTo(addr);
        return 0;
    }
}
