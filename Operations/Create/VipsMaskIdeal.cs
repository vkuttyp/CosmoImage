using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Create;

/// <summary>
/// Synthesise an ideal-lowpass frequency-domain mask. Mirrors libvips
/// <c>vips_mask_ideal</c>. Float single-band; 1 inside the disc of
/// radius <c>FrequencyCutoff · min(W, H)</c>, 0 outside. The disc is
/// centred at the image centre, so the mask must be FFT-shifted (or
/// the FFT output centred) before multiplying with
/// <see cref="Operations.Analysis.VipsFreqmult"/>.
///
/// <para>Ideal-edge filters cause spatial-domain ringing (Gibbs
/// phenomenon). For practical filtering use Gaussian or Butterworth
/// masks instead — they trade frequency-domain sharpness for spatial
/// smoothness.</para>
/// </summary>
public class VipsMaskIdealLowpass : VipsOperation
{
    public VipsImage? Out { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>Cutoff frequency as fraction of <c>min(W, H)/2</c>; 0..1.</summary>
    public double FrequencyCutoff { get; set; } = 0.5;
    /// <summary>If true, output is the high-pass complement (1 outside, 0 inside).</summary>
    public bool Reject { get; set; } = false;

    public override int Build()
    {
        if (Width < 1 || Height < 1) return -1;
        if (FrequencyCutoff < 0 || FrequencyCutoff > 1) return -1;

        Out = new VipsImage
        {
            Width = Width, Height = Height, Bands = 1, BandFormat = VipsBandFormat.Float,
            Interpretation = VipsInterpretation.Fourier,
            XRes = 1, YRes = 1,
            GenerateFn = Generate, ClientB = (Width, Height, FrequencyCutoff, Reject),
        };
        Out.SetPipeline(VipsDemandStyle.Any);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("MaskIdeal", Width, Height, FrequencyCutoff, Reject);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var (W, H, fc, reject) = ((int, int, double, bool))b!;
        VipsRect r = outRegion.Valid;
        double cx = W / 2.0, cy = H / 2.0;
        double rCutoff = fc * Math.Min(W, H) / 2.0;
        double rCutoff2 = rCutoff * rCutoff;

        for (int y = 0; y < r.Height; y++)
        {
            double dy = (r.Top + y) - cy;
            double dy2 = dy * dy;
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                double dx = (r.Left + x) - cx;
                bool inside = dx * dx + dy2 <= rCutoff2;
                float v = (inside ^ reject) ? 1f : 0f;
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(x * 4, 4), v);
            }
        }
        return 0;
    }
}

/// <summary>
/// Ideal-highpass mask — convenience wrapper that sets
/// <see cref="VipsMaskIdealLowpass.Reject"/> = true.
/// </summary>
public class VipsMaskIdealHighpass : VipsOperation
{
    public VipsImage? Out { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double FrequencyCutoff { get; set; } = 0.5;

    public override int Build()
    {
        var lp = new VipsMaskIdealLowpass {
            Width = Width, Height = Height, FrequencyCutoff = FrequencyCutoff, Reject = true,
        };
        if (lp.Build() != 0) return -1;
        Out = lp.Out;
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("MaskIdealHi", Width, Height, FrequencyCutoff);
}

/// <summary>
/// Ideal-band directional mask. Float single-band; 1 inside two
/// symmetric discs of radius <c>RingWidth · min(W, H)/2</c> centred at
/// <c>(±FrequencyX·W/2, ±FrequencyY·H/2)</c>, 0 elsewhere. The two
/// discs preserve real-FFT conjugate symmetry so the mask multiplies
/// cleanly with FFTs of real-valued images. Mirrors libvips
/// <c>vips_mask_ideal_band</c>.
/// </summary>
public class VipsMaskIdealBand : VipsOperation
{
    public VipsImage? Out { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>Peak X coordinate as fraction of <c>W/2</c> (signed).</summary>
    public double FrequencyX { get; set; } = 0.0;
    /// <summary>Peak Y coordinate as fraction of <c>H/2</c> (signed).</summary>
    public double FrequencyY { get; set; } = 0.0;
    /// <summary>Disc radius around each peak as fraction of <c>min(W, H)/2</c>.</summary>
    public double RingWidth { get; set; } = 0.1;
    /// <summary>If true, output is the band-reject complement.</summary>
    public bool Reject { get; set; } = false;

    public override int Build()
    {
        if (Width < 1 || Height < 1) return -1;
        if (RingWidth < 0 || RingWidth > 1) return -1;

        Out = new VipsImage
        {
            Width = Width, Height = Height, Bands = 1, BandFormat = VipsBandFormat.Float,
            Interpretation = VipsInterpretation.Fourier,
            XRes = 1, YRes = 1,
            GenerateFn = Generate,
            ClientB = (Width, Height, FrequencyX, FrequencyY, RingWidth, Reject),
        };
        Out.SetPipeline(VipsDemandStyle.Any);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("MaskIdealBand", Width, Height, FrequencyX, FrequencyY, RingWidth, Reject);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var (W, H, fx, fy, rw, reject) = ((int, int, double, double, double, bool))b!;
        VipsRect r = outRegion.Valid;
        double cx = W / 2.0, cy = H / 2.0;
        double half = Math.Min(W, H) / 2.0;
        double radius = rw * half;
        double radius2 = radius * radius;
        double peakX = fx * W / 2.0;
        double peakY = fy * H / 2.0;

        for (int y = 0; y < r.Height; y++)
        {
            double dy = (r.Top + y) - cy;
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                double dx = (r.Left + x) - cx;
                double d1x = dx - peakX, d1y = dy - peakY;
                double d2x = dx + peakX, d2y = dy + peakY;
                bool inside = (d1x * d1x + d1y * d1y) <= radius2
                           || (d2x * d2x + d2y * d2y) <= radius2;
                float v = (inside ^ reject) ? 1f : 0f;
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(x * 4, 4), v);
            }
        }
        return 0;
    }
}
