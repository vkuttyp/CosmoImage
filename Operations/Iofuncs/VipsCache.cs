using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Iofuncs;

/// <summary>
/// Materialise the input once and stream cached pixels downstream.
/// Mirrors the practical effect of libvips <c>vips_cache</c>: a node
/// that has multiple consumers (a DAG fan-out) computes upstream
/// once instead of once per consumer.
///
/// <para>Coarser than libvips' tile-LRU cache — we cache the whole
/// image in memory at <c>Build</c> time. For streaming-friendly
/// pipelines this is usually fine; for very large images that won't
/// fit in RAM, fall back to the implicit on-demand pipeline by
/// omitting Cache.</para>
/// </summary>
public class VipsCacheOp : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }

    public override int Build()
    {
        if (In == null) return -1;

        // Materialise once.
        byte[] pixels;
        if (In.Pixels is { } existing) pixels = existing;
        else
        {
            var sink = new MemorySink(In);
            sink.RunAsync().GetAwaiter().GetResult();
            pixels = sink.Pixels;
        }

        Out = new VipsImage
        {
            Width = In.Width, Height = In.Height,
            Bands = In.Bands, BandFormat = In.BandFormat,
            Interpretation = In.Interpretation,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In, ClientB = pixels,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Cache", RuntimeHelpers.GetHashCode(In));

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var pixels = (byte[])b!;
        VipsImage @out = outRegion.Image;
        VipsRect r = outRegion.Valid;
        int W = @out.Width;
        int pelSize = @out.SizeOfPel;
        int rowBytes = r.Width * pelSize;
        for (int y = 0; y < r.Height; y++)
        {
            var addr = outRegion.GetAddress(r.Left, r.Top + y);
            int srcOff = ((r.Top + y) * W + r.Left) * pelSize;
            pixels.AsSpan(srcOff, rowBytes).CopyTo(addr);
        }
        return 0;
    }
}
