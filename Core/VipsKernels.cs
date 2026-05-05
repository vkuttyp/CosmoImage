using System;

namespace CosmoImage.Core;

/// <summary>
/// Resampling kernels for Resize/Affine. Each kernel is a 1D function
/// k(x) that's zero outside [-Support, Support]. 2D resampling is the
/// outer product k(x) × k(y); the kernels here are separable in principle.
/// </summary>
internal static class VipsKernels
{
    /// <summary>
    /// Half-width of the kernel's nonzero support. The full sample window
    /// is <c>2 * Support</c> pixels in each axis.
    /// </summary>
    public static int Support(VipsKernel kernel) => kernel switch
    {
        VipsKernel.Nearest => 1,           // 1×1 sample
        VipsKernel.Linear => 1,            // 2×2
        VipsKernel.Cubic => 2,             // 4×4 Catmull-Rom
        VipsKernel.Mitchell => 2,
        VipsKernel.Lanczos2 => 2,
        VipsKernel.Lanczos3 => 3,          // 6×6
        VipsKernel.Lanczos5 => 5,          // 10×10
        VipsKernel.Hermite => 1,           // 2×2
        VipsKernel.BicubicSharper => 2,    // 4×4
        VipsKernel.BicubicSmoother => 2,   // 4×4
        VipsKernel.Lbb => 2,               // 4×4 Catmull-Rom + post-clamp
        _ => 1
    };

    /// <summary>
    /// Evaluate the kernel at offset <paramref name="x"/>. Caller is
    /// responsible for normalization — sum the weights across the window
    /// and divide the accumulated pixel value by that sum.
    /// </summary>
    public static double Evaluate(VipsKernel kernel, double x) => kernel switch
    {
        VipsKernel.Nearest => Math.Abs(x) < 0.5 ? 1.0 : 0.0,
        VipsKernel.Linear => Triangle(x),
        VipsKernel.Cubic => CatmullRom(x),
        VipsKernel.Mitchell => MitchellNetravali(x, 1.0 / 3.0, 1.0 / 3.0),
        VipsKernel.Lanczos2 => Lanczos(x, 2),
        VipsKernel.Lanczos3 => Lanczos(x, 3),
        VipsKernel.Lanczos5 => Lanczos(x, 5),
        VipsKernel.Hermite => Hermite(x),
        VipsKernel.BicubicSharper => MitchellNetravali(x, 0.0, 1.0),
        VipsKernel.BicubicSmoother => MitchellNetravali(x, 1.5, -0.25),
        // LBB shares Catmull-Rom's separable weights; the local clamp
        // happens in the resampler after the weighted sum, not here.
        VipsKernel.Lbb => CatmullRom(x),
        _ => Triangle(x)
    };

    private static double Triangle(double x)
    {
        x = Math.Abs(x);
        return x < 1.0 ? 1.0 - x : 0.0;
    }

    // Catmull-Rom (B=0, C=0.5 in the BC family).
    private static double CatmullRom(double x)
    {
        x = Math.Abs(x);
        if (x < 1.0) return ((1.5 * x - 2.5) * x) * x + 1.0;
        if (x < 2.0) return ((-0.5 * x + 2.5) * x - 4.0) * x + 2.0;
        return 0.0;
    }

    // Mitchell-Netravali BC family. B=1/3, C=1/3 is the "balanced" choice.
    // B=0, C=0.5 = Catmull-Rom. B=0, C=1 = sharper. B=1.5, C=-0.25 = smoother.
    private static double MitchellNetravali(double x, double B, double C)
    {
        x = Math.Abs(x);
        double xx = x * x;
        if (x < 1.0)
            return ((12 - 9 * B - 6 * C) * xx * x + (-18 + 12 * B + 6 * C) * xx + (6 - 2 * B)) / 6.0;
        if (x < 2.0)
            return ((-B - 6 * C) * xx * x + (6 * B + 30 * C) * xx + (-12 * B - 48 * C) * x + (8 * B + 24 * C)) / 6.0;
        return 0.0;
    }

    // Cubic Hermite spline — support 1 (4 samples). Smoother than Linear,
    // sharper than Mitchell, no overshoot.
    private static double Hermite(double x)
    {
        x = Math.Abs(x);
        if (x < 1.0) return ((2 * x - 3) * x * x) + 1.0;
        return 0.0;
    }

    private static double Sinc(double x)
    {
        if (x == 0.0) return 1.0;
        double px = Math.PI * x;
        return Math.Sin(px) / px;
    }

    private static double Lanczos(double x, int a)
    {
        double ax = Math.Abs(x);
        if (ax >= a) return 0.0;
        return Sinc(x) * Sinc(x / a);
    }
}
