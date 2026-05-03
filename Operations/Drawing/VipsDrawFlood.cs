using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Drawing;

/// <summary>
/// 4-connected flood fill from a seed point. All pixels connected to
/// (<see cref="X"/>, <see cref="Y"/>) that match the seed's value are
/// replaced with <see cref="Ink"/>. Mirrors libvips
/// <c>vips_draw_flood</c>.
///
/// <para>Materialises the input — flood fill is fundamentally
/// random-access. Iterative scanline-fill (Smith 1979) keeps memory
/// flat for large solid regions instead of recursing per pixel.</para>
///
/// <para>UChar 1- or 3-band; <see cref="Ink"/> length must equal
/// input <c>SizeOfPel</c>.</para>
/// </summary>
public class VipsDrawFlood : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public byte[]? Ink { get; set; }

    public override int Build()
    {
        if (In == null || Ink == null) return -1;
        if (In.BandFormat != VipsBandFormat.UChar) return -1;
        int pelSize = In.SizeOfPel;
        if (Ink.Length != pelSize) return -1;
        if (X < 0 || X >= In.Width || Y < 0 || Y >= In.Height) return -1;

        // Materialise input.
        byte[] pixels;
        if (In.Pixels is { } existing) pixels = (byte[])existing.Clone();
        else
        {
            var sink = new MemorySink(In);
            sink.RunAsync().GetAwaiter().GetResult();
            pixels = (byte[])sink.Pixels.Clone();
        }

        FloodFillIterative(pixels, In.Width, In.Height, pelSize, X, Y, Ink);

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
    {
        var h = new HashCode();
        h.Add("DrawFlood"); h.Add(RuntimeHelpers.GetHashCode(In));
        h.Add(X); h.Add(Y);
        if (Ink != null) foreach (var bb in Ink) h.Add(bb);
        return h.ToHashCode();
    }

    private static void FloodFillIterative(byte[] pixels, int W, int H, int pel,
        int seedX, int seedY, byte[] ink)
    {
        // Capture target colour from the seed pixel.
        int seedOff = (seedY * W + seedX) * pel;
        var target = pixels.AsSpan(seedOff, pel).ToArray();
        // No-op if the seed already matches the ink colour.
        bool same = true;
        for (int i = 0; i < pel; i++) if (target[i] != ink[i]) { same = false; break; }
        if (same) return;

        // Smith-style scanline flood: per stack entry, fill the row at
        // (y) starting from x and push new candidates above/below.
        var stack = new Stack<(int x, int y)>();
        stack.Push((seedX, seedY));

        while (stack.Count > 0)
        {
            var (x, y) = stack.Pop();
            if (!Match(pixels, W, x, y, pel, target)) continue;

            // Walk left to find span start.
            int xL = x;
            while (xL > 0 && Match(pixels, W, xL - 1, y, pel, target)) xL--;
            // Walk right; paint as we go.
            int xR = x;
            while (xR < W - 1 && Match(pixels, W, xR + 1, y, pel, target)) xR++;
            // Paint the span.
            for (int i = xL; i <= xR; i++)
            {
                int off = (y * W + i) * pel;
                ink.AsSpan().CopyTo(pixels.AsSpan(off, pel));
            }
            // Push seed for above/below — at every position in span where
            // the neighbouring row matches target.
            PushRowSeeds(pixels, W, H, pel, target, xL, xR, y - 1, stack);
            PushRowSeeds(pixels, W, H, pel, target, xL, xR, y + 1, stack);
        }
    }

    private static void PushRowSeeds(byte[] pixels, int W, int H, int pel,
        byte[] target, int xL, int xR, int y, Stack<(int, int)> stack)
    {
        if (y < 0 || y >= H) return;
        bool inSpan = false;
        for (int i = xL; i <= xR; i++)
        {
            bool match = Match(pixels, W, i, y, pel, target);
            if (match && !inSpan)
            {
                stack.Push((i, y));
                inSpan = true;
            }
            else if (!match)
            {
                inSpan = false;
            }
        }
    }

    private static bool Match(byte[] pixels, int W, int x, int y, int pel, byte[] target)
    {
        int off = (y * W + x) * pel;
        for (int i = 0; i < pel; i++)
            if (pixels[off + i] != target[i]) return false;
        return true;
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var pixels = (byte[])b!;
        VipsImage @out = outRegion.Image;
        VipsRect r = outRegion.Valid;
        int W = @out.Width;
        int pelSize = @out.SizeOfPel;
        for (int y = 0; y < r.Height; y++)
        {
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            int srcOff = ((r.Top + y) * W + r.Left) * pelSize;
            pixels.AsSpan(srcOff, r.Width * pelSize).CopyTo(outAddr);
        }
        return 0;
    }
}
