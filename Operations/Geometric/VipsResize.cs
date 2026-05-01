using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Geometric;

public class VipsResize : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public double Scale { get; set; }
    public double VScale { get; set; }
    public VipsKernel Kernel { get; set; } = VipsKernel.Linear;

    public override int Build()
    {
        if (In == null) return -1;
        if (Scale <= 0) return -1;
        if (VScale <= 0) VScale = Scale;

        VipsImage current = In;

        // Integer-Shrink fast path for big downscales — box-average is far
        // cheaper than running the kernel over a wide source window.
        if (Scale < 0.5 || VScale < 0.5)
        {
            int hShrink = (int)(1.0 / Scale);
            int vShrink = (int)(1.0 / VScale);
            if (hShrink > 1 || vShrink > 1)
                current = VipsImageOps.Shrink(current, hShrink, vShrink);
        }

        // Two separable kernel passes for the residual fractional scale.
        // Per-output-pixel cost goes from (2*support)^2 to 2 * (2*support)
        // — Lanczos3: 36 taps → 12 taps. Direct port of libvips reduceh/reducev.
        double remainingHScale = (Scale * In.Width) / current.Width;
        double remainingVScale = (VScale * In.Height) / current.Height;

        if (remainingHScale != 1.0)
            current = VipsImageOps.Resize1D(current, remainingHScale, vertical: false, Kernel);
        if (remainingVScale != 1.0)
            current = VipsImageOps.Resize1D(current, remainingVScale, vertical: true, Kernel);

        Out = current;
        return 0;
    }

    public override int GetCacheKey()
    {
        return HashCode.Combine("Resize", RuntimeHelpers.GetHashCode(In), Scale, VScale, Kernel);
    }
}
