using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Create;

/// <summary>
/// Build a tone-curve LUT from three high-level parameters:
/// <see cref="Shadows"/> (shadow lift, 0..1), <see cref="Midtones"/>
/// (midtone gamma, &gt;1 lifts mids, &lt;1 darkens), and
/// <see cref="Highlights"/> (highlight compression, 0..1). Output is
/// a 256-wide UChar single-band LUT for use with <c>Maplut</c>.
///
/// <para>This is a simplified, photographer-friendly take on libvips'
/// <c>vips_tonelut</c> (which exposes nine parameters tuned for
/// 16-bit Lab pipelines). The three knobs here cover what most
/// editing UIs surface: lift the shadows toward grey, bend the
/// mid-tones via gamma, compress the highlights toward grey.</para>
/// </summary>
public class VipsTonelut : VipsOperation
{
    public VipsImage? Out { get; set; }
    /// <summary>Black point lift, 0..1. 0 = no change.</summary>
    public double Shadows { get; set; } = 0;
    /// <summary>Midtone gamma. 1 = linear, &gt;1 lifts mids, &lt;1 darkens.</summary>
    public double Midtones { get; set; } = 1.0;
    /// <summary>White point compression, 0..1. 0 = no change.</summary>
    public double Highlights { get; set; } = 0;

    public override int Build()
    {
        if (Shadows < 0 || Shadows > 1) return -1;
        if (Highlights < 0 || Highlights > 1) return -1;
        if (Midtones <= 0) return -1;

        // Build the LUT directly: compose lift, gamma, compress.
        var buf = new byte[256];
        double black = Shadows;       // input 0 → output black*255
        double white = 1 - Highlights; // input 1 → output white*255
        for (int x = 0; x < 256; x++)
        {
            double t = x / 255.0;
            // Apply gamma to mid-tones first.
            double g = Math.Pow(t, 1.0 / Midtones);
            // Map [0, 1] → [black, white] linearly.
            double y = black + g * (white - black);
            buf[x] = (byte)Math.Clamp(Math.Round(y * 255), 0, 255);
        }

        Out = new VipsImage
        {
            Width = 256, Height = 1, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.Histogram,
            XRes = 1, YRes = 1,
            GenerateFn = Generate, ClientB = buf,
        };
        Out.SetPipeline(VipsDemandStyle.Any);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Tonelut", Shadows, Midtones, Highlights);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var buf = (byte[])b!;
        VipsRect r = outRegion.Valid;
        var outAddr = outRegion.GetAddress(r.Left, 0);
        buf.AsSpan(r.Left, r.Width).CopyTo(outAddr);
        return 0;
    }
}
