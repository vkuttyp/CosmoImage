using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Convolution;

/// <summary>
/// Rank (order-statistic) filter over a width×height window. <see cref="Index"/>
/// selects the k-th smallest value (0-based) inside each window. <c>Median =
/// Rank with Index = (width*height) / 2</c>; <c>Min = 0</c> = Erode-equivalent;
/// <c>Max = width*height - 1</c> = Dilate-equivalent. Mirrors libvips
/// <c>vips_rank</c>.
/// </summary>
public class VipsRank : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }
    public int Index { get; set; }

    public override int Build()
    {
        if (In == null) return -1;
        if (WindowWidth <= 0 || WindowHeight <= 0) return -1;
        if (Index < 0 || Index >= WindowWidth * WindowHeight) return -1;

        Out = new VipsImage
        {
            Width = In.Width,
            Height = In.Height,
            Bands = In.Bands,
            BandFormat = In.BandFormat,
            Interpretation = In.Interpretation,
            Coding = In.Coding,
            XRes = In.XRes,
            YRes = In.YRes,
            StartFn = VipsSeq.StartOne,
            GenerateFn = Generate,
            StopFn = VipsSeq.StopOne,
            ClientA = In,
            ClientB = new { WindowWidth, WindowHeight, Index }
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.FatStrip, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Rank", RuntimeHelpers.GetHashCode(In), WindowWidth, WindowHeight, Index);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        dynamic config = b!;
        int mw = config.WindowWidth;
        int mh = config.WindowHeight;
        int idx = config.Index;
        VipsRect r = outRegion.Valid;

        int ox = mw / 2;
        int oy = mh / 2;

        VipsRect inRect = new VipsRect(r.Left - ox, r.Top - oy, r.Width + mw - 1, r.Height + mh - 1);
        VipsRect clipped = VipsRect.Intersect(inRect, new VipsRect(0, 0, @in.Width, @in.Height));
        if (inRegion.Prepare(clipped) != 0) return -1;

        int bands = @in.Bands;
        int pelSize = @in.SizeOfPel;
        Span<byte> window = stackalloc byte[mw * mh];

        for (int y = 0; y < r.Height; y++)
        {
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                for (int bnd = 0; bnd < bands; bnd++)
                {
                    int n = 0;
                    for (int my = 0; my < mh; my++)
                    {
                        for (int mx = 0; mx < mw; mx++)
                        {
                            int ix = r.Left + x + mx - ox;
                            int iy = r.Top + y + my - oy;
                            if (ix < 0 || ix >= @in.Width || iy < 0 || iy >= @in.Height) continue;
                            window[n++] = inRegion.GetAddress(ix, iy)[bnd];
                        }
                    }
                    if (n == 0) { outAddr[x * pelSize + bnd] = 0; continue; }

                    // Partial sort: we only need the idx-th smallest. For typical
                    // small windows (3x3, 5x5) full sort is comparable.
                    var slice = window.Slice(0, n);
                    int useIdx = Math.Min(idx, n - 1);
                    SelectK(slice, useIdx);
                    outAddr[x * pelSize + bnd] = slice[useIdx];
                }
            }
        }
        return 0;
    }

    /// <summary>
    /// In-place quickselect: after return <c>data[k]</c> is the k-th smallest
    /// element. Other positions are partitioned around it, not fully sorted.
    /// </summary>
    private static void SelectK(Span<byte> data, int k)
    {
        int lo = 0, hi = data.Length - 1;
        while (lo < hi)
        {
            byte pivot = data[(lo + hi) / 2];
            int i = lo, j = hi;
            while (i <= j)
            {
                while (data[i] < pivot) i++;
                while (data[j] > pivot) j--;
                if (i <= j)
                {
                    (data[i], data[j]) = (data[j], data[i]);
                    i++; j--;
                }
            }
            if (k <= j) hi = j;
            else if (k >= i) lo = i;
            else return;
        }
    }
}
