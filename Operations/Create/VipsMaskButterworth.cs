using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Create;

/// <summary>
/// Butterworth frequency-domain mask. The standard
/// adjustable-rolloff filter family — order <c>n</c> sets how
/// sharply the response transitions at the cutoff. Mirrors
/// libvips <c>vips_mask_butterworth</c> family.
///
/// <para>Lowpass formula:
/// <c>H(d) = 1 / (1 + (d / cutoff)^(2n))</c>; highpass is the
/// complement <c>1 − H(d)</c>; ring is the band-pass form
/// <c>1 / (1 + ((d² − r²) / (d · width))^(2n))</c>. Higher
/// <see cref="Order"/> approaches an ideal rectangular response
/// (with the corresponding spatial-domain ringing); lower order
/// is gentler and visually preferable.</para>
///
/// <para>Mask is centred; FFT-shift before
/// <see cref="Operations.Analysis.VipsFreqmult"/>.</para>
/// </summary>
public class VipsMaskButterworth : VipsOperation
{
    public VipsImage? Out { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public VipsMaskMode Mode { get; set; } = VipsMaskMode.Lowpass;
    public double FrequencyCutoff { get; set; } = 0.5;
    public double RingWidth { get; set; } = 0.1;
    /// <summary>Filter order; higher = sharper cutoff. Practical range 1..10.</summary>
    public int Order { get; set; } = 2;

    public override int Build()
    {
        if (Width < 1 || Height < 1) return -1;
        if (FrequencyCutoff < 0 || FrequencyCutoff > 1) return -1;
        if (Order < 1 || Order > 32) return -1;

        Out = new VipsImage
        {
            Width = Width, Height = Height, Bands = 1, BandFormat = VipsBandFormat.Float,
            Interpretation = VipsInterpretation.Fourier,
            XRes = 1, YRes = 1,
            GenerateFn = Generate,
            ClientB = (Width, Height, Mode, FrequencyCutoff, RingWidth, Order),
        };
        Out.SetPipeline(VipsDemandStyle.Any);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("MaskButterworth",
            Width, Height, Mode, FrequencyCutoff, RingWidth, Order);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var (W, H, mode, fc, rw, order) =
            ((int, int, VipsMaskMode, double, double, int))b!;
        VipsRect r = outRegion.Valid;
        double cx = W / 2.0, cy = H / 2.0;
        double half = Math.Min(W, H) / 2.0;
        double cutoff = Math.Max(1e-12, fc * half);
        double width = Math.Max(1e-12, rw * half);
        int twoN = 2 * order;

        for (int y = 0; y < r.Height; y++)
        {
            double dy = (r.Top + y) - cy;
            double dy2 = dy * dy;
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                double dx = (r.Left + x) - cx;
                double d2 = dx * dx + dy2;
                double d = Math.Sqrt(d2);
                float v;
                switch (mode)
                {
                    case VipsMaskMode.Lowpass:
                        v = (float)(1.0 / (1 + Math.Pow(d / cutoff, twoN)));
                        break;
                    case VipsMaskMode.Highpass:
                        v = (float)(1.0 - 1.0 / (1 + Math.Pow(d / cutoff, twoN)));
                        break;
                    default: // Ring
                        // Band-pass at cutoff with bandwidth `width`. Standard form.
                        double r2 = cutoff * cutoff;
                        double denom = d > 0 ? (d2 - r2) / (d * width) : double.PositiveInfinity;
                        v = (float)(1.0 / (1 + Math.Pow(denom, twoN)));
                        break;
                }
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(x * 4, 4), v);
            }
        }
        return 0;
    }
}
