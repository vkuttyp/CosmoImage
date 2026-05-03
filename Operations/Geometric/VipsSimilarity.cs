using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Geometric;

/// <summary>
/// Similarity transform: uniform scale + rotate (about a centre) +
/// translate. Special-case of <see cref="VipsAffine"/> with the
/// constraint that horizontal and vertical scale agree (so straight
/// lines stay straight and angles preserved). Mirrors libvips
/// <c>vips_similarity</c>.
///
/// <para>The constrained parameter set
/// (<see cref="Scale"/>, <see cref="Angle"/>°, <see cref="Idx"/>,
/// <see cref="Idy"/>) is much friendlier than the four-element affine
/// matrix when you actually want a scale+rotate. Output is sized to
/// the same dimensions as the input — translate or wrap with
/// <c>Embed</c> for fit-to-bbox behaviour.</para>
/// </summary>
public class VipsSimilarity : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public double Scale { get; set; } = 1.0;
    /// <summary>Rotation in degrees (counter-clockwise).</summary>
    public double Angle { get; set; } = 0.0;
    public double Idx { get; set; } = 0.0;
    public double Idy { get; set; } = 0.0;
    public VipsKernel Interpolate { get; set; } = VipsKernel.Linear;

    public override int Build()
    {
        if (In == null) return -1;
        if (Scale <= 0) return -1;

        // Affine is x' = A·x + B·y + Idx, y' = C·x + D·y + Idy applied
        // *forward*; for a similarity transform with scale s and rotation θ:
        //   A = D = s · cos(θ);  B = -s · sin(θ);  C = +s · sin(θ).
        // (Affine reads source from output via the matrix as written, so
        // we want the inverse — divide by scale, negate the angle.)
        double inv = 1.0 / Scale;
        double rad = -Angle * Math.PI / 180.0;
        double a = inv * Math.Cos(rad);
        double bb = -inv * Math.Sin(rad);
        double c = inv * Math.Sin(rad);
        double d = inv * Math.Cos(rad);

        var aff = new VipsAffine {
            In = In,
            A = a, B = bb, C = c, D = d,
            Idx = Idx, Idy = Idy,
            Interpolate = Interpolate,
        };
        if (aff.Build() != 0) return -1;
        Out = aff.Out;
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Similarity", RuntimeHelpers.GetHashCode(In),
            Scale, Angle, Idx, Idy, Interpolate);
}
