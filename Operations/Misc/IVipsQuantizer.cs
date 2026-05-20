using System;

namespace CosmoImage.Operations.Misc;

/// <summary>
/// Quantizer plugin — reduces an image's palette to a target number
/// of colors. Mirrors ImageSharp's <c>IQuantizer</c>.
///
/// <para>Implementations expose their tuning knobs (color count,
/// dithering, etc.) as their own configuration; the interface itself
/// only requires <see cref="Apply"/>. Users can plug custom
/// quantizers (Octree, Wu, Werner palette, custom voronoi-region
/// algorithms) into pipelines that previously hardcoded the
/// <see cref="MagickQuantizer"/> default.</para>
/// </summary>
public interface IVipsQuantizer
{
    /// <summary>
    /// Reduce <paramref name="input"/>'s palette. Output has the same
    /// dimensions and band count as input; only the unique-color
    /// count drops.
    /// </summary>
    VipsImage Apply(VipsImage input);
}

/// <summary>
/// Compatibility quantizer that preserves the long-standing
/// <see cref="MagickQuantizer"/> API while routing through the native
/// <see cref="VipsOctreeQuantizer"/> implementation.
/// </summary>
public sealed class MagickQuantizer : IVipsQuantizer
{
    /// <summary>Maximum number of unique colors in the output. Range 2..256.</summary>
    public int Colors { get; init; } = 256;

    /// <summary>Apply Floyd-Steinberg error diffusion. False for crisp banded output.</summary>
    public bool Dither { get; init; } = true;

    public VipsImage Apply(VipsImage input)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (Colors < 2 || Colors > 256)
            throw new ArgumentOutOfRangeException(nameof(Colors), "Colors must be in 2..256");
        if (input.Bands != 1 && input.Bands != 3 && input.Bands != 4)
            throw new ArgumentException("MagickQuantizer requires 1, 3, or 4 band input", nameof(input));
        return new VipsOctreeQuantizer
        {
            Colors = Colors,
            Dither = Dither,
        }.Apply(input);
    }
}
