using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Drawing;

/// <summary>
/// Draw a circle (outline or filled) into a copy of the input.
/// Mirrors libvips <c>vips_draw_circle</c>.
///
/// <para>Outline uses the midpoint-circle (Bresenham) algorithm; fill
/// uses span-fill across the disc — both clip to the image bounds.
/// <see cref="Ink"/> length must match input <c>SizeOfPel</c>.</para>
/// </summary>
public class VipsDrawCircle : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public int Cx { get; set; }
    public int Cy { get; set; }
    public int Radius { get; set; }
    public byte[]? Ink { get; set; }
    public bool Fill { get; set; }

    public override int Build()
    {
        if (In == null || Ink == null || Radius < 0) return -1;

        Out = new VipsImage
        {
            Width = In.Width, Height = In.Height,
            Bands = In.Bands, BandFormat = In.BandFormat,
            Interpretation = In.Interpretation,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In, ClientB = (Cx, Cy, Radius, Ink, Fill),
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
    {
        var h = new HashCode();
        h.Add("DrawCircle"); h.Add(RuntimeHelpers.GetHashCode(In));
        h.Add(Cx); h.Add(Cy); h.Add(Radius); h.Add(Fill);
        if (Ink != null) foreach (var bb in Ink) h.Add(bb);
        return h.ToHashCode();
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        var (cx, cy, radius, ink, fill) = ((int, int, int, byte[], bool))b!;
        VipsRect r = outRegion.Valid;

        if (inRegion.Prepare(r) != 0) return -1;

        // Copy input to output verbatim, then overdraw the circle.
        int pelSize = @in.SizeOfPel;
        int rowBytes = r.Width * pelSize;
        for (int y = 0; y < r.Height; y++)
            inRegion.GetAddress(r.Left, r.Top + y).Slice(0, rowBytes)
                .CopyTo(outRegion.GetAddress(r.Left, r.Top + y));

        if (fill)
        {
            // Span-fill: iterate y from -r..+r; for each y, draw horizontal
            // span of width 2·sqrt(r²-y²)+1. Clip per-region.
            for (int dy = -radius; dy <= radius; dy++)
            {
                int gy = cy + dy;
                if (gy < r.Top || gy >= r.Top + r.Height) continue;
                int dx = (int)Math.Floor(Math.Sqrt((double)radius * radius - dy * dy));
                FillSpan(outRegion, cx - dx, cx + dx, gy, ink, pelSize);
            }
        }
        else
        {
            // Midpoint-circle algorithm.
            int x = radius, y = 0, err = 0;
            while (x >= y)
            {
                Plot8(outRegion, cx, cy, x, y, ink, pelSize);
                y++;
                if (err <= 0) { err += 2 * y + 1; }
                else { x--; err += 2 * (y - x) + 1; }
            }
        }
        return 0;
    }

    private static void FillSpan(VipsRegion reg, int x0, int x1, int y, byte[] ink, int pelSize)
    {
        if (y < reg.Valid.Top || y >= reg.Valid.Bottom) return;
        int left = Math.Max(x0, reg.Valid.Left);
        int right = Math.Min(x1, reg.Valid.Right - 1);
        if (left > right) return;
        var addr = reg.GetAddress(left, y);
        for (int i = 0; i <= right - left; i++)
            ink.AsSpan().CopyTo(addr.Slice(i * pelSize, pelSize));
    }

    private static void Plot(VipsRegion reg, int x, int y, byte[] ink, int pelSize)
    {
        if (x < reg.Valid.Left || x >= reg.Valid.Right) return;
        if (y < reg.Valid.Top || y >= reg.Valid.Bottom) return;
        ink.AsSpan().CopyTo(reg.GetAddress(x, y).Slice(0, pelSize));
    }

    /// <summary>Plot the 8 octants of a circle from one (x, y) sample.</summary>
    private static void Plot8(VipsRegion reg, int cx, int cy, int x, int y, byte[] ink, int pelSize)
    {
        Plot(reg, cx + x, cy + y, ink, pelSize);
        Plot(reg, cx - x, cy + y, ink, pelSize);
        Plot(reg, cx + x, cy - y, ink, pelSize);
        Plot(reg, cx - x, cy - y, ink, pelSize);
        Plot(reg, cx + y, cy + x, ink, pelSize);
        Plot(reg, cx - y, cy + x, ink, pelSize);
        Plot(reg, cx + y, cy - x, ink, pelSize);
        Plot(reg, cx - y, cy - x, ink, pelSize);
    }
}
