using System;

namespace CosmoImage.Operations.Geometric;

/// <summary>
/// Resize fit modes — how the input image's aspect ratio is reconciled
/// with the target dimensions. Mirrors ImageSharp's <c>ResizeMode</c>.
/// </summary>
public enum VipsResizeMode
{
    /// <summary>Scale to exactly the target W×H, ignoring aspect ratio.</summary>
    Stretch = 0,
    /// <summary>Scale uniformly so the target box is fully covered, then crop the overflow.</summary>
    Crop = 1,
    /// <summary>Scale uniformly so the image fits inside the target box, then pad with <see cref="VipsResizeOptions.PadColor"/>.</summary>
    Pad = 2,
    /// <summary>
    /// Like <see cref="Pad"/> but never enlarges. If the source already fits
    /// inside the target box, it's just centred / padded; if it overflows,
    /// behaves identically to <see cref="Pad"/> (shrinks to fit, then pads).
    /// </summary>
    BoxPad = 3,
    /// <summary>Scale down to fit inside the target if larger; never enlarges.</summary>
    Max = 4,
    /// <summary>Scale up to cover the target if smaller; never shrinks.</summary>
    Min = 5,
}

/// <summary>
/// Resize options bundle. Mirrors ImageSharp's <c>ResizeOptions</c>.
/// Pass to <c>VipsImageOps.Resize(input, options)</c> for full
/// control over fit mode, anchor position, pad colour, and sampler.
/// </summary>
public sealed class VipsResizeOptions
{
    /// <summary>Target width.</summary>
    public int Width { get; init; }
    /// <summary>Target height.</summary>
    public int Height { get; init; }
    /// <summary>How aspect mismatch with the target is reconciled.</summary>
    public VipsResizeMode Mode { get; init; } = VipsResizeMode.Stretch;
    /// <summary>
    /// Anchor for the kept content within the target box (only relevant
    /// for <see cref="VipsResizeMode.Crop"/>, <see cref="VipsResizeMode.Pad"/>,
    /// and <see cref="VipsResizeMode.BoxPad"/>).
    /// </summary>
    public VipsCompass Position { get; init; } = VipsCompass.Centre;
    /// <summary>
    /// Per-band pad colour for <see cref="VipsResizeMode.Pad"/> /
    /// <see cref="VipsResizeMode.BoxPad"/>. <c>null</c> = black with full alpha.
    /// </summary>
    public double[]? PadColor { get; init; }
    /// <summary>Resampling kernel for the underlying scale step.</summary>
    public VipsKernel Kernel { get; init; } = VipsKernel.Linear;
}

/// <summary>
/// Apply a <see cref="VipsResizeOptions"/> bundle: chooses scale +
/// crop / pad based on <see cref="VipsResizeOptions.Mode"/>.
/// </summary>
public static class VipsResizeWithOptions
{
    public static VipsImage Apply(VipsImage input, VipsResizeOptions options)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (options == null) throw new ArgumentNullException(nameof(options));
        if (options.Width <= 0) throw new ArgumentException("Width must be positive", nameof(options));
        if (options.Height <= 0) throw new ArgumentException("Height must be positive", nameof(options));

        int w = options.Width, h = options.Height;
        double hScale = (double)w / input.Width;
        double vScale = (double)h / input.Height;
        var kernel = options.Kernel;

        switch (options.Mode)
        {
            case VipsResizeMode.Stretch:
                return VipsImageOps.Resize(input, hScale, vScale, kernel);

            case VipsResizeMode.Crop:
            {
                // Cover: use the larger ratio so the whole target box fills.
                double scale = Math.Max(hScale, vScale);
                var resized = VipsImageOps.Resize(input, scale, 0, kernel);
                int left = HOffset(resized.Width, w, options.Position);
                int top = VOffset(resized.Height, h, options.Position);
                return VipsImageOps.ExtractArea(resized, left, top, w, h);
            }

            case VipsResizeMode.Pad:
            {
                // Fit: use the smaller ratio so the image fits inside.
                double scale = Math.Min(hScale, vScale);
                var resized = VipsImageOps.Resize(input, scale, 0, kernel);
                return VipsImageOps.Pad(resized, w, h, options.PadColor, options.Position);
            }

            case VipsResizeMode.BoxPad:
            {
                // No-enlarge variant. If the source already fits, just pad
                // without scaling; otherwise shrink-fit then pad.
                if (input.Width <= w && input.Height <= h)
                    return VipsImageOps.Pad(input, w, h, options.PadColor, options.Position);
                double scale = Math.Min(hScale, vScale);
                var resized = VipsImageOps.Resize(input, scale, 0, kernel);
                return VipsImageOps.Pad(resized, w, h, options.PadColor, options.Position);
            }

            case VipsResizeMode.Max:
            {
                // Shrink to fit if too big; otherwise pass through unchanged.
                if (input.Width <= w && input.Height <= h) return input;
                double scale = Math.Min(hScale, vScale);
                return VipsImageOps.Resize(input, scale, 0, kernel);
            }

            case VipsResizeMode.Min:
            {
                // Enlarge to cover if too small; otherwise pass through unchanged.
                if (input.Width >= w && input.Height >= h) return input;
                double scale = Math.Max(hScale, vScale);
                return VipsImageOps.Resize(input, scale, 0, kernel);
            }

            default:
                throw new ArgumentException($"Unsupported resize mode: {options.Mode}", nameof(options));
        }
    }

    private static int HOffset(int actual, int target, VipsCompass pos) => pos switch
    {
        VipsCompass.NorthWest or VipsCompass.West or VipsCompass.SouthWest => 0,
        VipsCompass.NorthEast or VipsCompass.East or VipsCompass.SouthEast => actual - target,
        _ => (actual - target) / 2,
    };

    private static int VOffset(int actual, int target, VipsCompass pos) => pos switch
    {
        VipsCompass.NorthWest or VipsCompass.North or VipsCompass.NorthEast => 0,
        VipsCompass.SouthWest or VipsCompass.South or VipsCompass.SouthEast => actual - target,
        _ => (actual - target) / 2,
    };
}
