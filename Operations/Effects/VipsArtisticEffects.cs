using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Effects;

/// <summary>
/// Painting-style effect: replaces each pixel with the most-frequent color in
/// a <paramref name="Radius"/>-pixel neighborhood, producing a smudgy oil-paint
/// look. Wraps Magick.NET's OilPaint via <see cref="VipsMagickHelper"/>.
/// </summary>
public class VipsOilPaint : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }

    /// <summary>Neighborhood radius in pixels. Larger = chunkier brushstrokes.</summary>
    public double Radius { get; set; } = 3.0;

    /// <summary>Edge softness within the neighborhood.</summary>
    public double Sigma { get; set; } = 1.0;

    public override int Build()
    {
        if (In == null || Radius <= 0 || Sigma <= 0) return -1;
        var src = In;
        double radius = Radius, sigma = Sigma;
        Out = VipsMagickHelper.NewLikeWithLazyPixels(In,
            () => VipsMagickHelper.ApplyEffect(src, img => img.OilPaint(radius, sigma)));
        return 0;
    }

    public override int GetCacheKey() => HashCode.Combine("OilPaint", RuntimeHelpers.GetHashCode(In), Radius, Sigma);
}

/// <summary>
/// Charcoal-sketch effect: high-pass + invert + tint to look like charcoal on
/// paper. Wraps Magick.NET's Charcoal.
/// </summary>
public class VipsCharcoal : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }

    /// <summary>Stroke radius — bigger = bolder strokes.</summary>
    public double Radius { get; set; } = 1.0;

    /// <summary>Edge sigma. Larger = softer detail.</summary>
    public double Sigma { get; set; } = 0.5;

    public override int Build()
    {
        if (In == null || Radius <= 0 || Sigma <= 0) return -1;
        var src = In;
        double radius = Radius, sigma = Sigma;
        Out = VipsMagickHelper.NewLikeWithLazyPixels(In,
            () => VipsMagickHelper.ApplyEffect(src, img => img.Charcoal(radius, sigma)));
        return 0;
    }

    public override int GetCacheKey() => HashCode.Combine("Charcoal", RuntimeHelpers.GetHashCode(In), Radius, Sigma);
}

/// <summary>
/// Sketch / pencil-line effect: high-pass + edge-trace producing pencil-style
/// linework. Wraps Magick.NET's Sketch.
/// </summary>
public class VipsSketch : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }

    /// <summary>Stroke length.</summary>
    public double Radius { get; set; } = 1.0;

    /// <summary>Edge softness.</summary>
    public double Sigma { get; set; } = 0.5;

    /// <summary>Stroke direction in degrees (0 = horizontal).</summary>
    public double Angle { get; set; } = 0.0;

    public override int Build()
    {
        if (In == null || Radius <= 0 || Sigma <= 0) return -1;
        var src = In;
        double radius = Radius, sigma = Sigma, angle = Angle;
        Out = VipsMagickHelper.NewLikeWithLazyPixels(In,
            () => VipsMagickHelper.ApplyEffect(src, img => img.Sketch(radius, sigma, angle)));
        return 0;
    }

    public override int GetCacheKey() => HashCode.Combine("Sketch", RuntimeHelpers.GetHashCode(In), Radius, Sigma, Angle);
}
