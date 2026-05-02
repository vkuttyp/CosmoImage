using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Create;

/// <summary>
/// Synthesise a 2D Gaussian kernel image for use with <c>Conv</c>.
/// Mirrors libvips <c>vips_gaussmat</c>. The kernel size is determined
/// by where the Gaussian falls below
/// <c>MinAmpl × peak</c>; with default <see cref="MinAmpl"/> = 0.1 and
/// <see cref="Sigma"/> = 1, that yields a ~5×5 mask.
///
/// <para>The output is a Float matrix-typed image. With
/// <see cref="Separable"/> = true the kernel is 1D
/// (<c>(2r+1, 1)</c>); with the default the full 2D kernel is
/// produced — appropriate for direct <c>Conv</c> use, while the 1D
/// form pairs with <c>Conv1D</c> for separable two-pass filtering.</para>
/// </summary>
public class VipsGaussmat : VipsOperation
{
    public VipsImage? Out { get; set; }
    public double Sigma { get; set; } = 1.0;
    /// <summary>Tail-cut threshold relative to the kernel peak (0..1).</summary>
    public double MinAmpl { get; set; } = 0.1;
    /// <summary>If true, returns a 1D kernel (height = 1) for two-pass separable use.</summary>
    public bool Separable { get; set; } = false;

    public override int Build()
    {
        if (Sigma <= 0 || Sigma > 100) return -1;
        if (MinAmpl <= 0 || MinAmpl >= 1) return -1;

        // Find the radius where exp(-r²/(2σ²)) drops below MinAmpl.
        double r2 = -2 * Sigma * Sigma * Math.Log(MinAmpl);
        int radius = (int)Math.Max(1, Math.Ceiling(Math.Sqrt(r2)));
        int size = 2 * radius + 1;

        int width = size;
        int height = Separable ? 1 : size;

        // Compute kernel weights in a buffer.
        var buf = new byte[width * height * 4];
        if (Separable)
        {
            for (int x = 0; x < width; x++)
            {
                double dx = x - radius;
                float v = (float)Math.Exp(-(dx * dx) / (2 * Sigma * Sigma));
                BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(x * 4, 4), v);
            }
        }
        else
        {
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                double dx = x - radius, dy = y - radius;
                float v = (float)Math.Exp(-(dx * dx + dy * dy) / (2 * Sigma * Sigma));
                BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan((y * width + x) * 4, 4), v);
            }
        }

        Out = new VipsImage
        {
            Width = width, Height = height, Bands = 1, BandFormat = VipsBandFormat.Float,
            Interpretation = VipsInterpretation.Matrix,
            XRes = 1, YRes = 1,
            GenerateFn = Generate, ClientB = buf,
        };
        Out.SetPipeline(VipsDemandStyle.Any);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Gaussmat", Sigma, MinAmpl, Separable);

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
