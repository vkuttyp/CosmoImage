using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Create;

/// <summary>
/// Synthesise a Laplacian-of-Gaussian (LoG) kernel matrix. The LoG is
/// the standard rotation-invariant edge-detector mask:
/// <c>LoG(x, y) = (x²+y²−2σ²) / σ⁴ · exp(−(x²+y²)/(2σ²))</c>
/// — a centre-symmetric ring of negative weights around a positive
/// peak (or vice versa, depending on the sign convention).
///
/// <para>Mirrors libvips <c>vips_logmat</c>. Output is a Float matrix
/// auto-sized so the absolute weight at the edge is below
/// <see cref="MinAmpl"/> × the peak. Pair with
/// <c>Conv</c> for blob detection or scale-space edge extraction.</para>
/// </summary>
public class VipsLogmat : VipsOperation
{
    public VipsImage? Out { get; set; }
    public double Sigma { get; set; } = 1.0;
    public double MinAmpl { get; set; } = 0.1;

    public override int Build()
    {
        if (Sigma <= 0 || Sigma > 100) return -1;
        if (MinAmpl <= 0 || MinAmpl >= 1) return -1;

        // The LoG envelope is bounded by the Gaussian factor. Find r where
        // the Gaussian falls below MinAmpl — same logic as Gaussmat.
        double r2 = -2 * Sigma * Sigma * Math.Log(MinAmpl);
        int radius = (int)Math.Max(1, Math.Ceiling(Math.Sqrt(r2)));
        int size = 2 * radius + 1;

        var buf = new byte[size * size * 4];
        double s2 = Sigma * Sigma, s4 = s2 * s2;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            double dx = x - radius, dy = y - radius;
            double r2_ = dx * dx + dy * dy;
            float v = (float)((r2_ - 2 * s2) / s4 * Math.Exp(-r2_ / (2 * s2)));
            BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan((y * size + x) * 4, 4), v);
        }

        Out = new VipsImage
        {
            Width = size, Height = size, Bands = 1, BandFormat = VipsBandFormat.Float,
            Interpretation = VipsInterpretation.Matrix,
            XRes = 1, YRes = 1,
            GenerateFn = Generate, ClientB = buf,
        };
        Out.SetPipeline(VipsDemandStyle.Any);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Logmat", Sigma, MinAmpl);

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
