using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Mosaicing;

/// <summary>
/// Lay N inputs out into a grid. Each grid column takes its width
/// from the widest input in that column; each row takes its height
/// from the tallest input in that row. Cells that fall outside any
/// input are filled with <see cref="Background"/>. Mirrors libvips
/// <c>vips_arrayjoin</c>.
///
/// <para>With <c>Across = 0</c> (default) all inputs go side-by-side
/// in a single row. <c>Shim</c> inserts a per-axis gap between cells
/// (filled with the background colour). All inputs must agree on
/// band count and band format.</para>
/// </summary>
public class VipsArrayjoin : VipsOperation
{
    public VipsImage[]? Inputs { get; set; }
    public VipsImage? Out { get; set; }
    /// <summary>Cells per row; 0 (default) puts everything in one row.</summary>
    public int Across { get; set; }
    /// <summary>Pixel gap between cells in both axes.</summary>
    public int Shim { get; set; } = 0;
    /// <summary>Per-band background fill. Defaults to zero.</summary>
    public double[]? Background { get; set; }
    /// <summary>Cross-axis alignment within each cell.</summary>
    public VipsAlign HAlign { get; set; } = VipsAlign.Low;
    public VipsAlign VAlign { get; set; } = VipsAlign.Low;

    public override int Build()
    {
        if (Inputs == null || Inputs.Length == 0) return -1;
        var first = Inputs[0];
        foreach (var img in Inputs)
        {
            if (img == null) return -1;
            if (img.Bands != first.Bands || img.BandFormat != first.BandFormat) return -1;
        }
        int N = Inputs.Length;
        int across = Across <= 0 ? N : Across;
        int down = (N + across - 1) / across;

        // Per-column width = max width across that column's inputs.
        var colW = new int[across];
        var rowH = new int[down];
        for (int i = 0; i < N; i++)
        {
            int col = i % across;
            int row = i / across;
            if (Inputs[i].Width > colW[col]) colW[col] = Inputs[i].Width;
            if (Inputs[i].Height > rowH[row]) rowH[row] = Inputs[i].Height;
        }

        // Cumulative origin of each cell.
        var colX = new int[across];
        var rowY = new int[down];
        for (int c = 1; c < across; c++) colX[c] = colX[c - 1] + colW[c - 1] + Shim;
        for (int r = 1; r < down; r++) rowY[r] = rowY[r - 1] + rowH[r - 1] + Shim;

        int outW = colX[across - 1] + colW[across - 1];
        int outH = rowY[down - 1] + rowH[down - 1];

        // Per-input top-left within the output (factoring in alignment).
        var origins = new (int x, int y)[N];
        for (int i = 0; i < N; i++)
        {
            int col = i % across, row = i / across;
            int slackX = colW[col] - Inputs[i].Width;
            int slackY = rowH[row] - Inputs[i].Height;
            int dx = HAlign switch { VipsAlign.Centre => slackX / 2, VipsAlign.High => slackX, _ => 0 };
            int dy = VAlign switch { VipsAlign.Centre => slackY / 2, VipsAlign.High => slackY, _ => 0 };
            origins[i] = (colX[col] + dx, rowY[row] + dy);
        }

        Out = new VipsImage
        {
            Width = outW, Height = outH, Bands = first.Bands, BandFormat = first.BandFormat,
            Interpretation = first.Interpretation,
            Coding = first.Coding, XRes = first.XRes, YRes = first.YRes,
            StartFn = VipsSeq.StartMany, GenerateFn = Generate, StopFn = VipsSeq.StopMany,
            ClientA = Inputs, ClientB = (origins, Background ?? new double[first.Bands]),
        };
        Out.CopyMetadataFrom(first);
        Out.SetPipeline(VipsDemandStyle.Any, Inputs);
        return 0;
    }

    public override int GetCacheKey()
    {
        var h = new HashCode();
        h.Add("Arrayjoin"); h.Add(Across); h.Add(Shim);
        if (Inputs != null) foreach (var i in Inputs) h.Add(RuntimeHelpers.GetHashCode(i));
        if (Background != null) foreach (var v in Background) h.Add(v);
        return h.ToHashCode();
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var regions = (VipsRegion[])seq!;
        var (origins, background) = (((int x, int y)[], double[]))b!;
        VipsImage @out = outRegion.Image;
        VipsRect r = outRegion.Valid;
        bool isFloat = @out.BandFormat == VipsBandFormat.Float;
        int sampleSize = isFloat ? 4 : 1;
        int bands = @out.Bands;
        int pelBytes = bands * sampleSize;

        // Fill the whole region with the background first; per-input copies
        // will overwrite where they apply.
        var bgPel = new byte[pelBytes];
        if (isFloat)
        {
            for (int bnd = 0; bnd < bands; bnd++)
                System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(
                    bgPel.AsSpan(bnd * 4, 4), (float)background[bnd]);
        }
        else
        {
            for (int bnd = 0; bnd < bands; bnd++)
                bgPel[bnd] = (byte)Math.Clamp(background[bnd], 0, 255);
        }
        for (int y = 0; y < r.Height; y++)
        {
            var addr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
                bgPel.AsSpan().CopyTo(addr.Slice(x * pelBytes, pelBytes));
        }

        // Per-input slabs — only request the input rect that overlaps `r`.
        for (int i = 0; i < regions.Length; i++)
        {
            var img = regions[i].Image;
            var (ox, oy) = origins[i];
            // Image i covers (ox, oy, img.W, img.H) in output coords.
            int x0 = Math.Max(r.Left, ox);
            int y0 = Math.Max(r.Top, oy);
            int x1 = Math.Min(r.Left + r.Width, ox + img.Width);
            int y1 = Math.Min(r.Top + r.Height, oy + img.Height);
            if (x0 >= x1 || y0 >= y1) continue;

            var inRect = new VipsRect(x0 - ox, y0 - oy, x1 - x0, y1 - y0);
            if (regions[i].Prepare(inRect) != 0) return -1;
            int rowBytes = (x1 - x0) * pelBytes;
            for (int sy = 0; sy < y1 - y0; sy++)
            {
                var inAddr = regions[i].GetAddress(inRect.Left, inRect.Top + sy);
                var outAddr = outRegion.GetAddress(x0, y0 + sy);
                inAddr.Slice(0, rowBytes).CopyTo(outAddr);
            }
        }
        return 0;
    }
}
