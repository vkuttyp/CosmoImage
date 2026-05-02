using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Create;

/// <summary>
/// Invert a 1D monotonic LUT: given a single-row UChar input that
/// maps <c>x → y</c> (with y monotonically non-decreasing), produce
/// the inverse mapping <c>y → x</c>. Mirrors libvips
/// <c>vips_invertlut</c>.
///
/// <para>Useful as the second half of histogram-equalisation-like
/// pipelines: build a forward CDF (e.g. via <c>HistCum</c> +
/// <c>HistNorm</c>), then <c>Invertlut</c> to get a remap suitable
/// for <c>Maplut</c>. Multi-band LUTs are handled per-band.</para>
///
/// <para>Output is the same width / band-count / format as the input;
/// non-monotonic inputs produce undefined behaviour at the
/// non-monotonic regions (we use a stable "first crossing wins"
/// rule).</para>
/// </summary>
public class VipsInvertlut : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    /// <summary>Output width. Default 0 = same as input.</summary>
    public int Size { get; set; } = 0;

    public override int Build()
    {
        if (In == null) return -1;
        if (In.Height != 1) return -1;
        if (In.BandFormat != VipsBandFormat.UChar) return -1;

        // Materialise input.
        byte[] inPixels;
        if (In.Pixels is { } existing) inPixels = existing;
        else
        {
            var sink = new MemorySink(In);
            sink.RunAsync().GetAwaiter().GetResult();
            inPixels = sink.Pixels;
        }

        int inW = In.Width;
        int outW = Size > 0 ? Size : inW;
        int bands = In.Bands;
        var outBuf = new byte[outW * bands];

        // For each band, walk x from 0..inW-1 and record at each y the
        // first x that reaches y. Then fill in any gaps by repeating
        // the last value (monotonic non-decreasing assumption).
        for (int bnd = 0; bnd < bands; bnd++)
        {
            int x = 0;
            int lastX = 0;
            for (int y = 0; y < outW; y++)
            {
                while (x < inW && inPixels[x * bands + bnd] <= (byte)Math.Min(255, y * 255 / Math.Max(1, outW - 1)))
                {
                    lastX = x;
                    x++;
                }
                outBuf[y * bands + bnd] = (byte)Math.Clamp(lastX * 255 / Math.Max(1, inW - 1), 0, 255);
            }
        }

        Out = new VipsImage
        {
            Width = outW, Height = 1, Bands = bands, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.Histogram,
            XRes = 1, YRes = 1,
            GenerateFn = Generate, ClientB = outBuf,
        };
        Out.SetPipeline(VipsDemandStyle.Any);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Invertlut", RuntimeHelpers.GetHashCode(In), Size);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var buf = (byte[])b!;
        VipsRect r = outRegion.Valid;
        int bands = outRegion.Image.Bands;
        var outAddr = outRegion.GetAddress(r.Left, 0);
        buf.AsSpan(r.Left * bands, r.Width * bands).CopyTo(outAddr);
        return 0;
    }
}
