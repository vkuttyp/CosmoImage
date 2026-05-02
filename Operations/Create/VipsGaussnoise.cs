using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Create;

/// <summary>
/// Synthesise an image of Gaussian-distributed random pixels with
/// given <see cref="Mean"/> and <see cref="Sigma"/>. Mirrors libvips
/// <c>vips_gaussnoise</c>. Output is Float (preserves the tail beyond
/// UChar range); cast to UChar afterwards if needed.
///
/// <para>Box-Muller transform; deterministic when <see cref="Seed"/>
/// is set. Common uses: synthesise sensor noise to validate
/// denoisers, augment training data, dither hard tonal posterisation,
/// stress-test analytic ops.</para>
/// </summary>
public class VipsGaussnoise : VipsOperation
{
    public VipsImage? Out { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double Mean { get; set; } = 128;
    public double Sigma { get; set; } = 30;
    /// <summary>RNG seed; 0 = derive from clock.</summary>
    public int Seed { get; set; } = 0;

    public override int Build()
    {
        if (Width < 1 || Height < 1) return -1;
        if (Sigma < 0) return -1;

        // Materialise the noise once at Build so repeated reads are
        // deterministic for a given seed (random ops shouldn't redraw).
        int W = Width, H = Height;
        var buf = new byte[W * H * 4];
        var rng = Seed != 0 ? new Random(Seed) : new Random();
        for (int i = 0; i < W * H; i++)
        {
            double u1 = 1.0 - rng.NextDouble();
            double u2 = 1.0 - rng.NextDouble();
            double z = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
            float v = (float)(Mean + Sigma * z);
            BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(i * 4, 4), v);
        }

        Out = new VipsImage
        {
            Width = W, Height = H, Bands = 1, BandFormat = VipsBandFormat.Float,
            Interpretation = VipsInterpretation.BW,
            XRes = 1, YRes = 1,
            GenerateFn = Generate, ClientB = buf,
        };
        Out.SetPipeline(VipsDemandStyle.Any);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Gaussnoise", Width, Height, Mean, Sigma, Seed);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var buf = (byte[])b!;
        VipsRect r = outRegion.Valid;
        int W = outRegion.Image.Width;
        for (int y = 0; y < r.Height; y++)
        {
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            int srcOff = ((r.Top + y) * W + r.Left) * 4;
            buf.AsSpan(srcOff, r.Width * 4).CopyTo(outAddr);
        }
        return 0;
    }
}
