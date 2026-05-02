using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Geometric;

/// <summary>
/// Single-axis kernel resampler. The two-pass (X then Y) composition of this
/// op replaces the per-pixel 2D kernel sampling in <see cref="VipsResize"/> —
/// turning Lanczos3's 36-tap-per-pixel cost into 6+6 = 12 taps. Direct C#
/// analog of libvips' <c>vips_reducev</c> / <c>vips_reduceh</c>.
/// </summary>
public class VipsResize1D : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }

    /// <summary>Scale factor applied to the chosen axis. &gt;1 = upscale.</summary>
    public double Scale { get; set; }

    /// <summary>False = scale X (width) only; true = scale Y (height) only.</summary>
    public bool Vertical { get; set; }

    public VipsKernel Kernel { get; set; } = VipsKernel.Linear;

    public override int Build()
    {
        if (In == null || Scale <= 0) return -1;

        int newW = Vertical ? In.Width : (int)Math.Max(1, Math.Round(In.Width * Scale));
        int newH = Vertical ? (int)Math.Max(1, Math.Round(In.Height * Scale)) : In.Height;

        Out = new VipsImage
        {
            Width = newW,
            Height = newH,
            Bands = In.Bands,
            BandFormat = In.BandFormat,
            Interpretation = In.Interpretation,
            Coding = In.Coding,
            XRes = Vertical ? In.XRes : In.XRes * Scale,
            YRes = Vertical ? In.YRes * Scale : In.YRes,
            StartFn = VipsSeq.StartOne,
            GenerateFn = Generate,
            StopFn = VipsSeq.StopOne,
            ClientA = In,
            ClientB = new { Scale, Vertical, Kernel }
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.SmallTile, In);

        return 0;
    }

    public override int GetCacheKey()
    {
        return HashCode.Combine("Resize1D", RuntimeHelpers.GetHashCode(In), Scale, Vertical, Kernel);
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        dynamic config = b!;
        double scale = config.Scale;
        bool vertical = config.Vertical;
        VipsKernel kernel = config.Kernel;
        VipsRect r = outRegion.Valid;

        int support = VipsKernels.Support(kernel);
        int windowSize = 2 * support;
        int bands = @in.Bands;
        int pelSize = @in.SizeOfPel;
        int W = @in.Width;
        int H = @in.Height;
        bool isFloat = @in.BandFormat == VipsBandFormat.Float;

        if (vertical)
            return isFloat
                ? GenerateYFloat(inRegion, outRegion, r, scale, kernel, support, windowSize, bands, H)
                : GenerateY(inRegion, outRegion, r, scale, kernel, support, windowSize, bands, pelSize, H);
        return isFloat
            ? GenerateXFloat(inRegion, outRegion, r, scale, kernel, support, windowSize, bands, W)
            : GenerateX(inRegion, outRegion, r, scale, kernel, support, windowSize, bands, pelSize, W);
    }

    private static int GenerateX(VipsRegion inRegion, VipsRegion outRegion, VipsRect r,
        double scale, VipsKernel kernel, int support, int windowSize, int bands, int pelSize, int W)
    {
        // X-only: prepare a horizontal strip covering the kernel window in x,
        // exactly r.Height rows.
        double minSrc = (r.Left + 0.5) / scale - 0.5;
        double maxSrc = (r.Right - 1 + 0.5) / scale - 0.5;
        int left = Math.Clamp((int)Math.Floor(minSrc) - support + 1, 0, W - 1);
        int right = Math.Clamp((int)Math.Floor(maxSrc) + support + 1, 0, W);
        if (inRegion.Prepare(new VipsRect(left, r.Top, right - left, r.Height)) != 0) return -1;

        // Per-output-column weights are constant across rows — precompute once.
        int[] xStarts = new int[r.Width];
        double[] wxFlat = new double[r.Width * windowSize];
        double[] wxNorm = new double[r.Width];
        for (int x = 0; x < r.Width; x++)
        {
            double srcX = (r.Left + x + 0.5) / scale - 0.5;
            int xStart = (int)Math.Floor(srcX) - support + 1;
            xStarts[x] = xStart;
            double sum = 0;
            for (int sx = 0; sx < windowSize; sx++)
            {
                double w = VipsKernels.Evaluate(kernel, (xStart + sx) - srcX);
                wxFlat[x * windowSize + sx] = w;
                sum += w;
            }
            wxNorm[x] = sum == 0 ? 1.0 : 1.0 / sum;
        }

        for (int y = 0; y < r.Height; y++)
        {
            int iy = r.Top + y;
            var outLine = outRegion.GetAddress(r.Left, iy);
            for (int x = 0; x < r.Width; x++)
            {
                int xStart = xStarts[x];
                double normalizer = wxNorm[x];
                int wxBase = x * windowSize;
                for (int bnd = 0; bnd < bands; bnd++)
                {
                    double sum = 0;
                    for (int sx = 0; sx < windowSize; sx++)
                    {
                        int ix = Math.Clamp(xStart + sx, 0, W - 1);
                        sum += inRegion.GetAddress(ix, iy)[bnd] * wxFlat[wxBase + sx];
                    }
                    outLine[x * pelSize + bnd] = (byte)Math.Clamp(sum * normalizer, 0, 255);
                }
            }
        }
        return 0;
    }

    /// <summary>
    /// Float-input X resize. Same prepare/window math as the UChar
    /// <see cref="GenerateX"/>; reads/writes 4 bytes per band, no clamp.
    /// </summary>
    private static int GenerateXFloat(VipsRegion inRegion, VipsRegion outRegion, VipsRect r,
        double scale, VipsKernel kernel, int support, int windowSize, int bands, int W)
    {
        double minSrc = (r.Left + 0.5) / scale - 0.5;
        double maxSrc = (r.Right - 1 + 0.5) / scale - 0.5;
        int left = Math.Clamp((int)Math.Floor(minSrc) - support + 1, 0, W - 1);
        int right = Math.Clamp((int)Math.Floor(maxSrc) + support + 1, 0, W);
        if (inRegion.Prepare(new VipsRect(left, r.Top, right - left, r.Height)) != 0) return -1;

        int[] xStarts = new int[r.Width];
        double[] wxFlat = new double[r.Width * windowSize];
        double[] wxNorm = new double[r.Width];
        for (int x = 0; x < r.Width; x++)
        {
            double srcX = (r.Left + x + 0.5) / scale - 0.5;
            int xStart = (int)Math.Floor(srcX) - support + 1;
            xStarts[x] = xStart;
            double sum = 0;
            for (int sx = 0; sx < windowSize; sx++)
            {
                double w = VipsKernels.Evaluate(kernel, (xStart + sx) - srcX);
                wxFlat[x * windowSize + sx] = w;
                sum += w;
            }
            wxNorm[x] = sum == 0 ? 1.0 : 1.0 / sum;
        }

        for (int y = 0; y < r.Height; y++)
        {
            int iy = r.Top + y;
            var outLine = outRegion.GetAddress(r.Left, iy);
            for (int x = 0; x < r.Width; x++)
            {
                int xStart = xStarts[x];
                double normalizer = wxNorm[x];
                int wxBase = x * windowSize;
                for (int bnd = 0; bnd < bands; bnd++)
                {
                    double sum = 0;
                    for (int sx = 0; sx < windowSize; sx++)
                    {
                        int ix = Math.Clamp(xStart + sx, 0, W - 1);
                        var pel = inRegion.GetAddress(ix, iy);
                        float v = BinaryPrimitives.ReadSingleLittleEndian(pel.Slice(bnd * 4, 4));
                        sum += v * wxFlat[wxBase + sx];
                    }
                    BinaryPrimitives.WriteSingleLittleEndian(outLine.Slice((x * bands + bnd) * 4, 4), (float)(sum * normalizer));
                }
            }
        }
        return 0;
    }

    private static int GenerateY(VipsRegion inRegion, VipsRegion outRegion, VipsRect r,
        double scale, VipsKernel kernel, int support, int windowSize, int bands, int pelSize, int H)
    {
        // Y-only: prepare a vertical strip covering the kernel window in y,
        // exactly r.Width columns.
        double minSrc = (r.Top + 0.5) / scale - 0.5;
        double maxSrc = (r.Bottom - 1 + 0.5) / scale - 0.5;
        int top = Math.Clamp((int)Math.Floor(minSrc) - support + 1, 0, H - 1);
        int bottom = Math.Clamp((int)Math.Floor(maxSrc) + support + 1, 0, H);
        if (inRegion.Prepare(new VipsRect(r.Left, top, r.Width, bottom - top)) != 0) return -1;

        // Per-output-row weights are constant across columns — precompute once.
        int[] yStarts = new int[r.Height];
        double[] wyFlat = new double[r.Height * windowSize];
        double[] wyNorm = new double[r.Height];
        for (int y = 0; y < r.Height; y++)
        {
            double srcY = (r.Top + y + 0.5) / scale - 0.5;
            int yStart = (int)Math.Floor(srcY) - support + 1;
            yStarts[y] = yStart;
            double sum = 0;
            for (int sy = 0; sy < windowSize; sy++)
            {
                double w = VipsKernels.Evaluate(kernel, (yStart + sy) - srcY);
                wyFlat[y * windowSize + sy] = w;
                sum += w;
            }
            wyNorm[y] = sum == 0 ? 1.0 : 1.0 / sum;
        }

        for (int y = 0; y < r.Height; y++)
        {
            int yStart = yStarts[y];
            double normalizer = wyNorm[y];
            int wyBase = y * windowSize;
            var outLine = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                int ix = r.Left + x;
                for (int bnd = 0; bnd < bands; bnd++)
                {
                    double sum = 0;
                    for (int sy = 0; sy < windowSize; sy++)
                    {
                        int iy = Math.Clamp(yStart + sy, 0, H - 1);
                        sum += inRegion.GetAddress(ix, iy)[bnd] * wyFlat[wyBase + sy];
                    }
                    outLine[x * pelSize + bnd] = (byte)Math.Clamp(sum * normalizer, 0, 255);
                }
            }
        }
        return 0;
    }

    /// <summary>
    /// Float-input Y resize. Same prepare/window math as the UChar
    /// <see cref="GenerateY"/>; reads/writes 4 bytes per band, no clamp.
    /// </summary>
    private static int GenerateYFloat(VipsRegion inRegion, VipsRegion outRegion, VipsRect r,
        double scale, VipsKernel kernel, int support, int windowSize, int bands, int H)
    {
        double minSrc = (r.Top + 0.5) / scale - 0.5;
        double maxSrc = (r.Bottom - 1 + 0.5) / scale - 0.5;
        int top = Math.Clamp((int)Math.Floor(minSrc) - support + 1, 0, H - 1);
        int bottom = Math.Clamp((int)Math.Floor(maxSrc) + support + 1, 0, H);
        if (inRegion.Prepare(new VipsRect(r.Left, top, r.Width, bottom - top)) != 0) return -1;

        int[] yStarts = new int[r.Height];
        double[] wyFlat = new double[r.Height * windowSize];
        double[] wyNorm = new double[r.Height];
        for (int y = 0; y < r.Height; y++)
        {
            double srcY = (r.Top + y + 0.5) / scale - 0.5;
            int yStart = (int)Math.Floor(srcY) - support + 1;
            yStarts[y] = yStart;
            double sum = 0;
            for (int sy = 0; sy < windowSize; sy++)
            {
                double w = VipsKernels.Evaluate(kernel, (yStart + sy) - srcY);
                wyFlat[y * windowSize + sy] = w;
                sum += w;
            }
            wyNorm[y] = sum == 0 ? 1.0 : 1.0 / sum;
        }

        for (int y = 0; y < r.Height; y++)
        {
            int yStart = yStarts[y];
            double normalizer = wyNorm[y];
            int wyBase = y * windowSize;
            var outLine = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                int ix = r.Left + x;
                for (int bnd = 0; bnd < bands; bnd++)
                {
                    double sum = 0;
                    for (int sy = 0; sy < windowSize; sy++)
                    {
                        int iy = Math.Clamp(yStart + sy, 0, H - 1);
                        var pel = inRegion.GetAddress(ix, iy);
                        float v = BinaryPrimitives.ReadSingleLittleEndian(pel.Slice(bnd * 4, 4));
                        sum += v * wyFlat[wyBase + sy];
                    }
                    BinaryPrimitives.WriteSingleLittleEndian(outLine.Slice((x * bands + bnd) * 4, 4), (float)(sum * normalizer));
                }
            }
        }
        return 0;
    }
}
