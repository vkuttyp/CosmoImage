using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Convolution;

/// <summary>
/// Separable 1D convolution. Applies the same 1D Float kernel
/// horizontally then vertically — O(W·H·N) instead of O(W·H·N²) for
/// the equivalent 2D mask. Mirrors libvips <c>vips_convsep</c>.
///
/// <para><see cref="Kernel"/> is a 1D array of weights (typically odd
/// length). A normalised Gaussian or boxcar is the common case;
/// large arbitrary kernels work too. UChar input clamps; Float
/// input is unclamped.</para>
/// </summary>
public class VipsConvSep : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public double[]? Kernel { get; set; }

    public override int Build()
    {
        if (In == null || Kernel == null || Kernel.Length == 0) return -1;
        if ((Kernel.Length & 1) == 0) return -1; // odd length only

        // Compose horizontal + vertical 1D passes via the existing Conv1D op.
        var hPass = VipsImageOps.Conv1D(In, Kernel, vertical: false);
        Out = VipsImageOps.Conv1D(hPass, Kernel, vertical: true);
        return 0;
    }

    public override int GetCacheKey()
    {
        var h = new HashCode();
        h.Add("ConvSep"); h.Add(RuntimeHelpers.GetHashCode(In));
        if (Kernel != null) foreach (var v in Kernel) h.Add(v);
        return h.ToHashCode();
    }
}
