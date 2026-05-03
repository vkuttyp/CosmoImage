using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Create;

/// <summary>
/// Synthesise a 1/fᵅ frequency-domain mask. Used as the spectral
/// shape for fractal-noise synthesis: multiply white noise's spectrum
/// by this mask, inverse-FFT, and the result has the target fractal
/// dimension. Mirrors libvips <c>vips_mask_fractal</c>.
///
/// <para>The response is <c>H(d) = 1 / d^(α/2)</c> where d is
/// distance from the centre and α is <see cref="FractalDimension"/>;
/// the DC bin (d = 0) is set to zero (the spatial mean is
/// arbitrary). Mask is centred; FFT-shift before
/// <see cref="Operations.Analysis.VipsFreqmult"/>.</para>
///
/// <para>Pair with <c>Gaussnoise + Freqmult</c> for spectrally-shaped
/// noise — an alternative to summed-octave Perlin
/// (<see cref="VipsFractsurf"/>) that gives more precise control of
/// the spectral slope.</para>
/// </summary>
public class VipsMaskFractal : VipsOperation
{
    public VipsImage? Out { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>α in 1/fᵅ; 2.5 is "textbook fractal", 2.0 is brown noise, 1.0 is pink noise.</summary>
    public double FractalDimension { get; set; } = 2.5;

    public override int Build()
    {
        if (Width < 1 || Height < 1) return -1;
        if (FractalDimension <= 0 || FractalDimension > 4) return -1;

        Out = new VipsImage
        {
            Width = Width, Height = Height, Bands = 1, BandFormat = VipsBandFormat.Float,
            Interpretation = VipsInterpretation.Fourier,
            XRes = 1, YRes = 1,
            GenerateFn = Generate,
            ClientB = (Width, Height, FractalDimension),
        };
        Out.SetPipeline(VipsDemandStyle.Any);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("MaskFractal", Width, Height, FractalDimension);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var (W, H, dim) = ((int, int, double))b!;
        VipsRect r = outRegion.Valid;
        double cx = W / 2.0, cy = H / 2.0;
        double exponent = dim / 2.0;

        for (int y = 0; y < r.Height; y++)
        {
            double dy = (r.Top + y) - cy;
            double dy2 = dy * dy;
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                double dx = (r.Left + x) - cx;
                double d = Math.Sqrt(dx * dx + dy2);
                float v = d == 0 ? 0f : (float)(1.0 / Math.Pow(d, exponent));
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(x * 4, 4), v);
            }
        }
        return 0;
    }
}
