using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Create;

/// <summary>
/// Operating mode for Gaussian / Butterworth frequency masks.
/// </summary>
public enum VipsMaskMode
{
    Lowpass = 0,
    Highpass = 1,
    /// <summary>Ring band-pass at radius <c>FrequencyCutoff</c>, width controlled by sigma / order.</summary>
    Ring = 2,
    /// <summary>Directional band-pass with two symmetric peaks at <c>(±FrequencyX, ±FrequencyY)</c>
    /// in normalised frequency space. Preserves real-FFT conjugate symmetry.</summary>
    Band = 3,
}

/// <summary>
/// Gaussian frequency-domain mask. Smooth fall-off avoids the
/// spatial-domain ringing of <see cref="VipsMaskIdealLowpass"/>;
/// the standard recommendation when designing FFT-domain filters.
/// Mirrors libvips <c>vips_mask_gaussian</c> family.
///
/// <para>For <see cref="VipsMaskMode.Lowpass"/> the response is
/// <c>H(d) = exp(-d² / (2σ²))</c> where σ is set so that
/// <c>d = FrequencyCutoff · min(W, H) / 2</c> sits at the
/// 1-σ point. <see cref="VipsMaskMode.Highpass"/> is the
/// complement <c>1 − H(d)</c>; <see cref="VipsMaskMode.Ring"/> is
/// <c>exp(−(d − r)² / (2σ²))</c> peaked at the ring radius.</para>
///
/// <para>Mask is centred at the image centre; FFT-shift before
/// multiplying with <see cref="Operations.Analysis.VipsFreqmult"/>.</para>
/// </summary>
public class VipsMaskGaussian : VipsOperation
{
    public VipsImage? Out { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public VipsMaskMode Mode { get; set; } = VipsMaskMode.Lowpass;
    /// <summary>Cutoff (or ring centre) as fraction of <c>min(W, H)/2</c>; 0..1.</summary>
    public double FrequencyCutoff { get; set; } = 0.5;
    /// <summary>Ring half-width (Ring mode only).</summary>
    public double RingWidth { get; set; } = 0.1;
    /// <summary>Band-mode peak X coordinate as fraction of <c>W/2</c> (signed).</summary>
    public double FrequencyX { get; set; } = 0.0;
    /// <summary>Band-mode peak Y coordinate as fraction of <c>H/2</c> (signed).</summary>
    public double FrequencyY { get; set; } = 0.0;

    public override int Build()
    {
        if (Width < 1 || Height < 1) return -1;
        if (FrequencyCutoff < 0 || FrequencyCutoff > 1) return -1;

        Out = new VipsImage
        {
            Width = Width, Height = Height, Bands = 1, BandFormat = VipsBandFormat.Float,
            Interpretation = VipsInterpretation.Fourier,
            XRes = 1, YRes = 1,
            GenerateFn = Generate,
            ClientB = (Width, Height, Mode, FrequencyCutoff, RingWidth, FrequencyX, FrequencyY),
        };
        Out.SetPipeline(VipsDemandStyle.Any);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("MaskGaussian", Width, Height, Mode,
            FrequencyCutoff, RingWidth, FrequencyX, FrequencyY);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var (W, H, mode, fc, rw, fx, fy) = ((int, int, VipsMaskMode, double, double, double, double))b!;
        VipsRect r = outRegion.Valid;
        double cx = W / 2.0, cy = H / 2.0;
        double half = Math.Min(W, H) / 2.0;
        double cutoff = fc * half;
        double sigma = (mode == VipsMaskMode.Ring || mode == VipsMaskMode.Band) ? rw * half : cutoff;
        double sig2 = 2 * sigma * sigma;
        if (sig2 == 0) sig2 = 1e-12;
        double peakX = fx * W / 2.0;
        double peakY = fy * H / 2.0;

        for (int y = 0; y < r.Height; y++)
        {
            double dy = (r.Top + y) - cy;
            double dy2 = dy * dy;
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                double dx = (r.Left + x) - cx;
                float v;
                switch (mode)
                {
                    case VipsMaskMode.Lowpass:
                        {
                            double d2 = dx * dx + dy2;
                            v = (float)Math.Exp(-d2 / sig2);
                            break;
                        }
                    case VipsMaskMode.Highpass:
                        {
                            double d2 = dx * dx + dy2;
                            v = (float)(1 - Math.Exp(-d2 / sig2));
                            break;
                        }
                    case VipsMaskMode.Ring:
                        {
                            double d = Math.Sqrt(dx * dx + dy2);
                            double dr = d - cutoff;
                            v = (float)Math.Exp(-dr * dr / sig2);
                            break;
                        }
                    default: // Band
                        {
                            double d1x = dx - peakX, d1y = dy - peakY;
                            double d2x = dx + peakX, d2y = dy + peakY;
                            double d1sq = d1x * d1x + d1y * d1y;
                            double d2sq = d2x * d2x + d2y * d2y;
                            v = (float)Math.Min(1.0,
                                Math.Exp(-d1sq / sig2) + Math.Exp(-d2sq / sig2));
                            break;
                        }
                }
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(x * 4, 4), v);
            }
        }
        return 0;
    }
}
