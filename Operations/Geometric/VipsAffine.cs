using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Geometric;

public class VipsAffine : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public double A { get; set; }
    public double B { get; set; }
    public double C { get; set; }
    public double D { get; set; }
    public double Idx { get; set; }
    public double Idy { get; set; }
    public VipsKernel Interpolate { get; set; } = VipsKernel.Linear;

    /// <summary>Output width; 0 = use input width. Set explicitly when the
    /// transform changes the bounding box (e.g. arbitrary-angle rotation).</summary>
    public int OutWidth { get; set; }

    /// <summary>Output height; 0 = use input height. See <see cref="OutWidth"/>.</summary>
    public int OutHeight { get; set; }

    public override int Build()
    {
        if (In == null) return -1;

        int outW = OutWidth > 0 ? OutWidth : In.Width;
        int outH = OutHeight > 0 ? OutHeight : In.Height;

        Out = new VipsImage
        {
            Width = outW,
            Height = outH,
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
            ClientB = new { A, B, C, D, Idx, Idy, Interpolate }
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.SmallTile, In);

        return 0;
    }

    public override int GetCacheKey()
    {
        var hash = new HashCode();
        hash.Add("Affine");
        hash.Add(RuntimeHelpers.GetHashCode(In));
        hash.Add(A); hash.Add(B); hash.Add(C); hash.Add(D);
        hash.Add(Idx); hash.Add(Idy);
        hash.Add(Interpolate);
        hash.Add(OutWidth); hash.Add(OutHeight);
        return hash.ToHashCode();
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = (VipsImage)a!;
        dynamic config = b!;
        double A = config.A;
        double B = config.B;
        double C = config.C;
        double D = config.D;
        double Idx = config.Idx;
        double Idy = config.Idy;
        VipsKernel kernel = config.Interpolate;
        VipsRect r = outRegion.Valid;

        // Find the bounding box in the input that we need for this output region
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        int[] cornersX = { r.Left, r.Right, r.Left, r.Right };
        int[] cornersY = { r.Top, r.Top, r.Bottom, r.Bottom };

        for (int i = 0; i < 4; i++)
        {
            double ix = A * cornersX[i] + B * cornersY[i] + Idx;
            double iy = C * cornersX[i] + D * cornersY[i] + Idy;
            minX = Math.Min(minX, ix); maxX = Math.Max(maxX, ix);
            minY = Math.Min(minY, iy); maxY = Math.Max(maxY, iy);
        }

        // Pad input rect by kernel support so the kernel window stays in-image
        // (or in the prepared rect after replicate-extend at the edges).
        int support = VipsKernels.Support(kernel);
        int windowSize = 2 * support;
        int left = Math.Clamp((int)Math.Floor(minX) - support + 1, 0, @in.Width - 1);
        int top = Math.Clamp((int)Math.Floor(minY) - support + 1, 0, @in.Height - 1);
        int right = Math.Clamp((int)Math.Floor(maxX) + support + 1, 0, @in.Width);
        int bottom = Math.Clamp((int)Math.Floor(maxY) + support + 1, 0, @in.Height);

        if (inRegion.Prepare(new VipsRect(left, top, right - left, bottom - top)) != 0) return -1;

        int bands = @in.Bands;
        int pelSize = @in.SizeOfPel;
        int W = @in.Width;
        int H = @in.Height;

        Span<double> wy = stackalloc double[windowSize];
        Span<double> wx = stackalloc double[windowSize];

        for (int y = 0; y < r.Height; y++)
        {
            var outLine = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                double srcX = A * (r.Left + x) + B * (r.Top + y) + Idx;
                double srcY = C * (r.Left + x) + D * (r.Top + y) + Idy;

                if (srcX < 0 || srcX >= W - 1 || srcY < 0 || srcY >= H - 1)
                {
                    // Transparent/Background
                    for (int bnd = 0; bnd < bands; bnd++) outLine[x * pelSize + bnd] = 0;
                    continue;
                }

                if (kernel == VipsKernel.Nearest)
                {
                    var p = inRegion.GetAddress((int)Math.Round(srcX), (int)Math.Round(srcY));
                    p.Slice(0, pelSize).CopyTo(outLine.Slice(x * pelSize, pelSize));
                    continue;
                }

                int xStart = (int)Math.Floor(srcX) - support + 1;
                int yStart = (int)Math.Floor(srcY) - support + 1;
                double wxSum = 0, wySum = 0;
                for (int sx = 0; sx < windowSize; sx++)
                {
                    wx[sx] = VipsKernels.Evaluate(kernel, (xStart + sx) - srcX);
                    wxSum += wx[sx];
                }
                for (int sy = 0; sy < windowSize; sy++)
                {
                    wy[sy] = VipsKernels.Evaluate(kernel, (yStart + sy) - srcY);
                    wySum += wy[sy];
                }
                double normalizer = 1.0 / (wxSum * wySum);

                for (int bnd = 0; bnd < bands; bnd++)
                {
                    double sum = 0;
                    for (int sy = 0; sy < windowSize; sy++)
                    {
                        int iy = Math.Clamp(yStart + sy, 0, H - 1);
                        for (int sx = 0; sx < windowSize; sx++)
                        {
                            int ix = Math.Clamp(xStart + sx, 0, W - 1);
                            sum += inRegion.GetAddress(ix, iy)[bnd] * wx[sx] * wy[sy];
                        }
                    }
                    outLine[x * pelSize + bnd] = (byte)Math.Clamp(sum * normalizer, 0, 255);
                }
            }
        }

        return 0;
    }
}
