using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Convolution;

public enum VipsEdgeKernel
{
    /// <summary>2×2 cross-difference; cheap, sensitive to noise.</summary>
    Roberts = 0,
    /// <summary>3×3 unweighted-centre Sobel cousin; less smoothing than Sobel.</summary>
    Prewitt = 1,
    /// <summary>3×3 isotropic 2nd derivative; single-kernel zero-crossing edge.</summary>
    Laplacian = 2,
}

/// <summary>
/// Edge-magnitude detector via a fixed kernel. For Roberts and
/// Prewitt the result is <c>sqrt(Gx² + Gy²)</c>; for Laplacian it's
/// the absolute value of a single isotropic 2nd-derivative kernel.
/// Mirrors the kernel-named members of ImageSharp's
/// <c>DetectEdges</c>: <c>Roberts</c>, <c>Prewitt</c>, <c>Laplacian3x3</c>.
///
/// <para>UChar 1-band input. Output is UChar 1-band, clamped to
/// 0..255. For Sobel / Compass / Canny use the existing
/// <see cref="VipsSobel"/> / <see cref="VipsCompassEdge"/> /
/// <see cref="VipsCanny"/> ops, or the dispatcher
/// <see cref="VipsEdge"/>.</para>
/// </summary>
public class VipsEdgeKernelOp : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public VipsEdgeKernel Kernel { get; set; } = VipsEdgeKernel.Roberts;

    public override int Build()
    {
        if (In == null) return -1;
        if (In.BandFormat != VipsBandFormat.UChar || In.Bands != 1) return -1;

        Out = new VipsImage
        {
            Width = In.Width, Height = In.Height,
            Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = In.Interpretation,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In, ClientB = Kernel,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.FatStrip, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("EdgeKernel", RuntimeHelpers.GetHashCode(In), Kernel);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inReg = (VipsRegion)seq!;
        VipsImage @in = inReg.Image;
        var kernel = (VipsEdgeKernel)b!;
        VipsRect r = outRegion.Valid;

        // Need a 1-pixel border of input for 3×3 kernels (Roberts is 2×2 but
        // we still need one extra row/col).
        var inRect = new VipsRect(r.Left - 1, r.Top - 1, r.Width + 2, r.Height + 2);
        var clipped = VipsRect.Intersect(inRect, new VipsRect(0, 0, @in.Width, @in.Height));
        if (inReg.Prepare(clipped) != 0) return -1;

        for (int y = 0; y < r.Height; y++)
        {
            int gy = r.Top + y;
            var outAddr = outRegion.GetAddress(r.Left, gy);
            for (int x = 0; x < r.Width; x++)
            {
                int gx = r.Left + x;
                byte result = kernel switch
                {
                    VipsEdgeKernel.Roberts => Roberts(inReg, @in, gx, gy),
                    VipsEdgeKernel.Prewitt => Prewitt(inReg, @in, gx, gy),
                    VipsEdgeKernel.Laplacian => Laplacian(inReg, @in, gx, gy),
                    _ => 0,
                };
                outAddr[x] = result;
            }
        }
        return 0;
    }

    private static byte Sample(VipsRegion reg, VipsImage @in, int x, int y)
    {
        if (x < 0) x = 0; else if (x >= @in.Width) x = @in.Width - 1;
        if (y < 0) y = 0; else if (y >= @in.Height) y = @in.Height - 1;
        return reg.GetAddress(x, y)[0];
    }

    private static byte Roberts(VipsRegion reg, VipsImage @in, int x, int y)
    {
        int p00 = Sample(reg, @in, x, y);
        int p11 = Sample(reg, @in, x + 1, y + 1);
        int p10 = Sample(reg, @in, x + 1, y);
        int p01 = Sample(reg, @in, x, y + 1);
        int gx = p00 - p11;
        int gy = p10 - p01;
        return (byte)Math.Clamp(Math.Sqrt(gx * gx + gy * gy), 0, 255);
    }

    private static byte Prewitt(VipsRegion reg, VipsImage @in, int x, int y)
    {
        int p00 = Sample(reg, @in, x - 1, y - 1);
        int p01 = Sample(reg, @in, x,     y - 1);
        int p02 = Sample(reg, @in, x + 1, y - 1);
        int p10 = Sample(reg, @in, x - 1, y);
        int p12 = Sample(reg, @in, x + 1, y);
        int p20 = Sample(reg, @in, x - 1, y + 1);
        int p21 = Sample(reg, @in, x,     y + 1);
        int p22 = Sample(reg, @in, x + 1, y + 1);
        // Unweighted centre column / row (vs Sobel's ×2).
        int gx = -p00 + p02 - p10 + p12 - p20 + p22;
        int gy = -p00 - p01 - p02 + p20 + p21 + p22;
        return (byte)Math.Clamp(Math.Sqrt(gx * gx + gy * gy), 0, 255);
    }

    private static byte Laplacian(VipsRegion reg, VipsImage @in, int x, int y)
    {
        int p01 = Sample(reg, @in, x,     y - 1);
        int p10 = Sample(reg, @in, x - 1, y);
        int p11 = Sample(reg, @in, x,     y);
        int p12 = Sample(reg, @in, x + 1, y);
        int p21 = Sample(reg, @in, x,     y + 1);
        // Standard 5-point Laplacian: 4·centre − N − S − E − W.
        int g = 4 * p11 - p01 - p10 - p12 - p21;
        return (byte)Math.Clamp(Math.Abs(g), 0, 255);
    }
}
