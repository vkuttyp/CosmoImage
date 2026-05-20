using System;
using System.Runtime.CompilerServices;
using CosmoImage.Core;

namespace CosmoImage.Operations.Effects;

/// <summary>
/// Add a Polaroid-style white border and rotate the image. Output dimensions
/// expand to fit the rotated, framed result, and an alpha channel is added
/// for the transparent corners around the photo. Eager-applied during
/// <see cref="Build"/> because the effect is a small composition pipeline:
/// convert to RGBA, add a white photo border, then rotate with transparent
/// corners.
/// </summary>
public class VipsPolaroid : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }

    /// <summary>Rotation in degrees. Negative tilts left, positive right.</summary>
    public double Angle { get; set; } = -5.0;

    public override int Build()
    {
        if (In == null) return -1;
        int srcBands = In.Bands;
        if (srcBands != 1 && srcBands != 3 && srcBands != 4) return -1;

        VipsImage rgba = srcBands switch
        {
            1 => VipsImageOps.AddAlpha(VipsImageOps.Bandjoin(In, In, In)),
            3 => VipsImageOps.AddAlpha(In),
            4 => In,
            _ => throw new InvalidOperationException()
        };

        int sideBorder = Math.Max(4, (int)Math.Ceiling(In.Width * 0.08));
        int topBorder = Math.Max(4, (int)Math.Ceiling(In.Height * 0.08));
        int bottomBorder = Math.Max(topBorder + 4, (int)Math.Ceiling(In.Height * 0.18));

        var framed = VipsImageOps.Embed(
            rgba,
            sideBorder,
            topBorder,
            rgba.Width + sideBorder * 2,
            rgba.Height + topBorder + bottomBorder,
            VipsExtend.Background,
            new double[] { 255, 255, 255, 255 });

        var rotated = VipsImageOps.Rotate(framed, Angle, VipsKernel.Linear);
        rotated.Interpretation = VipsInterpretation.RGB;
        rotated.XRes = In.XRes;
        rotated.YRes = In.YRes;
        Out = rotated;
        return 0;
    }

    public override int GetCacheKey() => HashCode.Combine("Polaroid", RuntimeHelpers.GetHashCode(In), Angle);
}
