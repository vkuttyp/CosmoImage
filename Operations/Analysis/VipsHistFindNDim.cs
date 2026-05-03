using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Analysis;

/// <summary>
/// N-dimensional histogram. Each pixel of an N-band UChar input
/// drops a vote into the bin
/// <c>(in[0] / step, in[1] / step, …, in[N-1] / step)</c> of an
/// N-dim accumulator with <see cref="Bins"/> bins per axis. Mirrors
/// libvips <c>vips_hist_find_ndim</c>.
///
/// <para>For 1-band input behaves like <c>HistFind</c>: output is
/// a (bins × 1) UInt single-band image. For 2-band input output is
/// a (bins × bins) UInt single-band image. For 3-band input output
/// is a (bins × bins) UInt image with <see cref="Bins"/> bands —
/// each band represents a slice of the 3D RGB cube along the first
/// channel. The classic colour-cube quantisation step.</para>
/// </summary>
public class VipsHistFindNDim : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public int Bins { get; set; } = 10;

    public override int Build()
    {
        if (In == null) return -1;
        if (In.BandFormat != VipsBandFormat.UChar) return -1;
        if (In.Bands < 1 || In.Bands > 3) return -1;
        if (Bins < 1 || Bins > 256) return -1;

        // Materialise input.
        byte[] pixels;
        if (In.Pixels is { } existing) pixels = existing;
        else
        {
            var sink = new MemorySink(In);
            sink.RunAsync().GetAwaiter().GetResult();
            pixels = sink.Pixels;
        }

        int W = In.Width, H = In.Height, bands = In.Bands;
        int outW, outH, outBands;
        switch (bands)
        {
            case 1: outW = Bins; outH = 1; outBands = 1; break;
            case 2: outW = Bins; outH = Bins; outBands = 1; break;
            default: outW = Bins; outH = Bins; outBands = Bins; break;
        }

        // bin index for sample value v: v * Bins / 256, clamped.
        var accum = new uint[outW * outH * outBands];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int pelOff = (y * W + x) * bands;
                int bx = pixels[pelOff] * Bins / 256;
                if (bx >= Bins) bx = Bins - 1;
                if (bands == 1)
                {
                    accum[bx]++;
                }
                else
                {
                    int by = pixels[pelOff + 1] * Bins / 256;
                    if (by >= Bins) by = Bins - 1;
                    if (bands == 2)
                    {
                        accum[by * outW + bx]++;
                    }
                    else
                    {
                        int bz = pixels[pelOff + 2] * Bins / 256;
                        if (bz >= Bins) bz = Bins - 1;
                        // Layout: band = bz, position (bx, by).
                        accum[(by * outW + bx) * outBands + bz]++;
                    }
                }
            }
        }

        var outBytes = new byte[outW * outH * outBands * 4];
        for (int i = 0; i < outW * outH * outBands; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(outBytes.AsSpan(i * 4, 4), accum[i]);

        Out = new VipsImage
        {
            Width = outW, Height = outH, Bands = outBands, BandFormat = VipsBandFormat.UInt,
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
        => HashCode.Combine("HistFindNDim", RuntimeHelpers.GetHashCode(In), Bins);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var buf = (byte[])b!;
        VipsImage @out = outRegion.Image;
        VipsRect r = outRegion.Valid;
        int W = @out.Width;
        int bands = @out.Bands;
        for (int y = 0; y < r.Height; y++)
        {
            var addr = outRegion.GetAddress(r.Left, r.Top + y);
            int srcOff = ((r.Top + y) * W + r.Left) * bands * 4;
            buf.AsSpan(srcOff, r.Width * bands * 4).CopyTo(addr);
        }
        return 0;
    }
}
