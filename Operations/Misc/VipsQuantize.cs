using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Misc;

/// <summary>
/// Reduce the image to at most <see cref="Colors"/> distinct colors,
/// optionally with Floyd-Steinberg dithering. Output stays in the input's
/// pixel format (RGB stays RGB) but with reduced unique colors. Useful for
/// visual effects, GIF frame preparation, and palette-PNG export pipelines.
/// Implementation delegates to <see cref="VipsOctreeQuantizer"/> — pure
/// managed, no Magick dependency.
/// </summary>
public class VipsQuantize : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }

    /// <summary>Maximum number of unique colors in the output. Range 2..256.</summary>
    public int Colors { get; set; } = 256;

    /// <summary>Apply Floyd-Steinberg error diffusion — better visual quality
    /// at the cost of some noise. False for crisp banded output.</summary>
    public bool Dither { get; set; } = true;

    public override int Build()
    {
        if (In == null) return -1;
        if (Colors < 2 || Colors > 256) return -1;
        if (In.Bands != 1 && In.Bands != 3 && In.Bands != 4) return -1;

        Out = new VipsOctreeQuantizer { Colors = Colors, Dither = Dither }.Apply(In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Quantize", RuntimeHelpers.GetHashCode(In), Colors, Dither);
}
