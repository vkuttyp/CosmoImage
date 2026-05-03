using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Drawing;

/// <summary>
/// Smudge / soft-erase: replace each pixel within the
/// (<see cref="X"/>, <see cref="Y"/>, <see cref="Width"/>,
/// <see cref="Height"/>) rectangle with the local average over a
/// 3×3 neighbourhood. Repeated runs blur the area progressively —
/// the libvips equivalent of "drag the smudge tool here". Mirrors
/// libvips <c>vips_draw_smudge</c>.
///
/// <para>UChar only. Edge pixels of the smudge rectangle clamp to
/// the image bounds when sampling neighbours.</para>
/// </summary>
public class VipsDrawSmudge : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    public override int Build()
    {
        if (In == null) return -1;
        if (In.BandFormat != VipsBandFormat.UChar) return -1;
        if (Width < 0 || Height < 0) return -1;

        // Pre-compute the smudged buffer so the streaming output stays
        // simple. Local average over the smudge rect is cheap; doing it
        // up-front avoids a second prepare in Generate.
        byte[] inPixels;
        if (In.Pixels is { } existing) inPixels = existing;
        else
        {
            var sink = new MemorySink(In);
            sink.RunAsync().GetAwaiter().GetResult();
            inPixels = sink.Pixels;
        }
        int W = In.Width, H = In.Height;
        int pelSize = In.SizeOfPel;
        int bands = In.Bands;
        var output = (byte[])inPixels.Clone();

        int x0 = Math.Max(0, X);
        int y0 = Math.Max(0, Y);
        int x1 = Math.Min(W, X + Width);
        int y1 = Math.Min(H, Y + Height);
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                int dstOff = (y * W + x) * pelSize;
                for (int bnd = 0; bnd < bands; bnd++)
                {
                    int sum = 0, count = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int sy = Math.Clamp(y + dy, 0, H - 1);
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int sx = Math.Clamp(x + dx, 0, W - 1);
                            sum += inPixels[(sy * W + sx) * pelSize + bnd];
                            count++;
                        }
                    }
                    output[dstOff + bnd] = (byte)((sum + count / 2) / count);
                }
            }
        }

        Out = new VipsImage
        {
            Width = W, Height = H, Bands = bands, BandFormat = VipsBandFormat.UChar,
            Interpretation = In.Interpretation,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In, ClientB = output,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("DrawSmudge", RuntimeHelpers.GetHashCode(In),
            X, Y, Width, Height);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var output = (byte[])b!;
        VipsImage @out = outRegion.Image;
        VipsRect r = outRegion.Valid;
        int W = @out.Width;
        int pelSize = @out.SizeOfPel;
        for (int y = 0; y < r.Height; y++)
        {
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            int srcOff = ((r.Top + y) * W + r.Left) * pelSize;
            output.AsSpan(srcOff, r.Width * pelSize).CopyTo(outAddr);
        }
        return 0;
    }
}
