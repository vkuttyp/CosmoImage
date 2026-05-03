using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Analysis;

/// <summary>
/// Circle Hough transform for a fixed radius (or a band of radii).
/// For every pixel above <see cref="Threshold"/>, vote at every
/// possible centre <c>(cx, cy)</c> on a circle of the chosen radius
/// — the locus of points whose distance to (x, y) equals <c>r</c>.
/// Local maxima in the output image identify circle centres.
/// Mirrors libvips <c>vips_hough_circle</c>.
///
/// <para>Output is UInt single-band, the same dimensions as the
/// input. UChar 1-band input. <see cref="MinRadius"/> /
/// <see cref="MaxRadius"/> bracket the radii to search; equal
/// values reduce to a single-radius search.</para>
/// </summary>
public class VipsHoughCircle : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public int MinRadius { get; set; } = 10;
    public int MaxRadius { get; set; } = 20;
    public int Threshold { get; set; } = 128;

    public override int Build()
    {
        if (In == null) return -1;
        if (In.BandFormat != VipsBandFormat.UChar || In.Bands != 1) return -1;
        if (MinRadius < 1 || MaxRadius < MinRadius) return -1;

        // Materialise.
        byte[] pixels;
        if (In.Pixels is { } existing) pixels = existing;
        else
        {
            var sink = new MemorySink(In);
            sink.RunAsync().GetAwaiter().GetResult();
            pixels = sink.Pixels;
        }

        int W = In.Width, H = In.Height;
        var accum = new uint[W * H];

        // Pre-compute the circle pixel offsets for each radius, once,
        // via Bresenham — cheaper than re-computing per edge pixel.
        var circles = new int[MaxRadius - MinRadius + 1][];
        for (int r = MinRadius; r <= MaxRadius; r++)
            circles[r - MinRadius] = BresenhamCircleOffsets(r);

        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                if (pixels[y * W + x] <= Threshold) continue;
                foreach (var circle in circles)
                {
                    for (int i = 0; i < circle.Length; i += 2)
                    {
                        int cx = x - circle[i];
                        int cy = y - circle[i + 1];
                        if (cx >= 0 && cx < W && cy >= 0 && cy < H)
                            accum[cy * W + cx]++;
                    }
                }
            }
        }

        var outBytes = new byte[W * H * 4];
        for (int i = 0; i < W * H; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(outBytes.AsSpan(i * 4, 4), accum[i]);

        Out = new VipsImage
        {
            Width = W, Height = H, Bands = 1, BandFormat = VipsBandFormat.UInt,
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
        => HashCode.Combine("HoughCircle", RuntimeHelpers.GetHashCode(In),
            MinRadius, MaxRadius, Threshold);

    /// <summary>
    /// Midpoint-circle algorithm: returns interleaved (x, y) offsets
    /// for the 8 octants of a discrete radius-r circle.
    /// </summary>
    private static int[] BresenhamCircleOffsets(int r)
    {
        var pts = new System.Collections.Generic.List<int>();
        int x = r, y = 0, err = 0;
        while (x >= y)
        {
            pts.Add(+x); pts.Add(+y);
            pts.Add(+x); pts.Add(-y);
            pts.Add(-x); pts.Add(+y);
            pts.Add(-x); pts.Add(-y);
            pts.Add(+y); pts.Add(+x);
            pts.Add(+y); pts.Add(-x);
            pts.Add(-y); pts.Add(+x);
            pts.Add(-y); pts.Add(-x);
            y++;
            if (err <= 0) { err += 2 * y + 1; }
            else { x--; err += 2 * (y - x) + 1; }
        }
        return pts.ToArray();
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var buf = (byte[])b!;
        VipsImage @out = outRegion.Image;
        VipsRect r = outRegion.Valid;
        int W = @out.Width;
        for (int y = 0; y < r.Height; y++)
        {
            var addr = outRegion.GetAddress(r.Left, r.Top + y);
            int srcOff = ((r.Top + y) * W + r.Left) * 4;
            buf.AsSpan(srcOff, r.Width * 4).CopyTo(addr);
        }
        return 0;
    }
}
