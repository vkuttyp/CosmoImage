using System;

namespace CosmoImage.Operations.Color;

/// <summary>
/// Modes supported by <see cref="VipsColorBlindness.Apply"/>.
/// Matrices are the standard Brettel-Vienot-Mollon (1997)
/// dichromacy-simulation matrices used by ImageSharp's
/// <c>ColorBlindness</c> processor.
/// </summary>
public enum VipsColorBlindnessMode
{
    /// <summary>Red-blind (loss of L cones).</summary>
    Protanopia = 0,
    /// <summary>Red-weak.</summary>
    Protanomaly = 1,
    /// <summary>Green-blind (loss of M cones).</summary>
    Deuteranopia = 2,
    /// <summary>Green-weak.</summary>
    Deuteranomaly = 3,
    /// <summary>Blue-blind (loss of S cones).</summary>
    Tritanopia = 4,
    /// <summary>Blue-weak.</summary>
    Tritanomaly = 5,
    /// <summary>All colour vision absent (rod-only).</summary>
    Achromatopsia = 6,
    /// <summary>Reduced colour discrimination across the spectrum.</summary>
    Achromatomaly = 7,
}

/// <summary>
/// Apply a stylised tone+chroma transform mimicking the Kodachrome
/// film stock — saturated reds, deep blues, slightly compressed
/// shadows. Mirrors ImageSharp's <c>Kodachrome()</c> processor
/// (built atop ColorMatrix).
///
/// <para>RGB or RGBA UChar input. Implementation is a 3×3 Recomb on
/// the colour bands with alpha untouched.</para>
/// </summary>
public static class VipsKodachrome
{
    private static readonly double[,] Matrix = new double[,]
    {
        { 0.7297023, 0.4567681, -0.1530, },
        { -0.0250,  0.8794651,  0.0512, },
        { 0.0270,   0.0972,     0.7406, },
    };

    public static VipsImage Apply(VipsImage input)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        return VipsImageOps.Recomb(input, Matrix);
    }
}

/// <summary>
/// Apply a stylised Lomograph-style tone+chroma transform —
/// saturated, slightly cooler colours mimicking Lomo / cross-process
/// looks. Mirrors ImageSharp's <c>Lomograph()</c>.
/// </summary>
public static class VipsLomograph
{
    private static readonly double[,] Matrix = new double[,]
    {
        { 1.5,  0,    0,   },
        { 0,    1.45, 0,   },
        { 0,    0,    1.09 },
    };

    public static VipsImage Apply(VipsImage input)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        return VipsImageOps.Recomb(input, Matrix);
    }
}

/// <summary>
/// Simulate colour-vision deficiency. Used as an accessibility
/// preview ("what does this UI look like to a Deuteranope?"). The
/// matrices come from Brettel, Vienot &amp; Mollon (1997) and match
/// the values ImageSharp's <c>ColorBlindness</c> processor uses.
/// </summary>
public static class VipsColorBlindness
{
    public static VipsImage Apply(VipsImage input, VipsColorBlindnessMode mode)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        return VipsImageOps.Recomb(input, MatrixFor(mode));
    }

    private static double[,] MatrixFor(VipsColorBlindnessMode mode) => mode switch
    {
        VipsColorBlindnessMode.Protanopia => new double[,]
        {
            { 0.567, 0.433, 0.000 },
            { 0.558, 0.442, 0.000 },
            { 0.000, 0.242, 0.758 },
        },
        VipsColorBlindnessMode.Protanomaly => new double[,]
        {
            { 0.817, 0.183, 0.000 },
            { 0.333, 0.667, 0.000 },
            { 0.000, 0.125, 0.875 },
        },
        VipsColorBlindnessMode.Deuteranopia => new double[,]
        {
            { 0.625, 0.375, 0.000 },
            { 0.700, 0.300, 0.000 },
            { 0.000, 0.300, 0.700 },
        },
        VipsColorBlindnessMode.Deuteranomaly => new double[,]
        {
            { 0.800, 0.200, 0.000 },
            { 0.258, 0.742, 0.000 },
            { 0.000, 0.142, 0.858 },
        },
        VipsColorBlindnessMode.Tritanopia => new double[,]
        {
            { 0.950, 0.050, 0.000 },
            { 0.000, 0.433, 0.567 },
            { 0.000, 0.475, 0.525 },
        },
        VipsColorBlindnessMode.Tritanomaly => new double[,]
        {
            { 0.967, 0.033, 0.000 },
            { 0.000, 0.733, 0.267 },
            { 0.000, 0.183, 0.817 },
        },
        VipsColorBlindnessMode.Achromatopsia => new double[,]
        {
            { 0.299, 0.587, 0.114 },
            { 0.299, 0.587, 0.114 },
            { 0.299, 0.587, 0.114 },
        },
        VipsColorBlindnessMode.Achromatomaly => new double[,]
        {
            { 0.618, 0.320, 0.062 },
            { 0.163, 0.775, 0.062 },
            { 0.163, 0.320, 0.516 },
        },
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
}
