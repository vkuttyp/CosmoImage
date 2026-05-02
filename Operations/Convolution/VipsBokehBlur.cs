using System;

namespace CosmoImage.Operations.Convolution;

/// <summary>
/// Hexagonal-aperture bokeh blur. Composes the existing 2D <see cref="VipsConv"/>
/// with a hex-disc kernel: pixels inside a regular hexagon of radius
/// <paramref name="radius"/> contribute equally; pixels outside are zero.
/// Produces the photographic specular-highlight shape that a
/// <see cref="VipsImageOps.GaussBlur"/> can't (Gaussian highlights round to
/// soft circles). Direct port of libvips <c>vips_bokeh</c>'s simplest mode.
/// </summary>
public static class VipsBokehBlur
{
    /// <summary>
    /// Build a normalized hexagonal kernel of radius <paramref name="radius"/>
    /// (in pixels, ≥ 1). Result is a square <c>(2r+1) × (2r+1)</c> matrix
    /// summing to 1. Hex orientation: flat top, vertices left/right.
    /// </summary>
    public static double[,] HexagonKernel(int radius)
    {
        if (radius < 1) throw new ArgumentOutOfRangeException(nameof(radius));
        int size = 2 * radius + 1;
        var k = new double[size, size];
        double r = radius;
        // Hexagon (flat-top) inscribed in a circle of radius r. The hexagon
        // is the intersection of three slabs at 0°, 60°, and 120°.
        double cos30 = Math.Sqrt(3) / 2;
        double sum = 0;
        for (int y = -radius; y <= radius; y++)
        {
            for (int x = -radius; x <= radius; x++)
            {
                // Flat-top hexagon test: |y| ≤ r·cos30 AND |y/2 ± x·cos30| ≤ r·cos30.
                double absX = Math.Abs(x);
                double absY = Math.Abs(y);
                bool inHex =
                    absY <= r * cos30 + 1e-9 &&
                    (absX * cos30 + absY * 0.5) <= r * cos30 + 1e-9;
                if (inHex)
                {
                    k[y + radius, x + radius] = 1.0;
                    sum += 1.0;
                }
            }
        }
        if (sum <= 0) sum = 1;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                k[y, x] /= sum;
        return k;
    }

    public static VipsImage Run(VipsImage input, int radius)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (radius < 1) return input;
        var kernel = HexagonKernel(radius);
        return CosmoImage.Core.VipsImageOps.Conv(input, kernel);
    }
}
