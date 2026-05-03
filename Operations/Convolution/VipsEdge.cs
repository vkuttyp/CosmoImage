using System;

namespace CosmoImage.Operations.Convolution;

public enum VipsEdgeMethod
{
    Sobel = 0,
    Compass = 1,
    Canny = 2,
}

/// <summary>
/// Generic edge-detector dispatcher. Mirrors libvips
/// <c>vips_edge</c>; selects between <see cref="VipsSobel"/>,
/// <see cref="VipsCompassEdge"/>, and <see cref="VipsCanny"/> via
/// <see cref="VipsEdgeMethod"/>.
///
/// <para>Useful when you want to swap edge detectors via a single
/// parameter (e.g. command-line flag) rather than wiring three
/// branches by hand.</para>
/// </summary>
public static class VipsEdge
{
    public static VipsImage Apply(VipsImage input, VipsEdgeMethod method = VipsEdgeMethod.Sobel,
        double cannySigma = 1.4, int cannyLow = 20, int cannyHigh = 60)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        return method switch
        {
            VipsEdgeMethod.Sobel => VipsImageOps.Sobel(input),
            VipsEdgeMethod.Compass => VipsImageOps.Compass(input),
            VipsEdgeMethod.Canny => VipsImageOps.Canny(input, cannySigma, cannyLow, cannyHigh),
            _ => throw new ArgumentOutOfRangeException(nameof(method)),
        };
    }
}
