using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Create;

/// <summary>
/// Fractal-surface noise — sum of <see cref="Octaves"/> Perlin layers
/// at successive frequencies and amplitudes (each layer halves
/// amplitude and halves cell size). Mirrors libvips
/// <c>vips_fractsurf</c>. Float single-band output.
///
/// <para>The classic 1/f / "pink" noise pattern that produces
/// natural-looking heightmaps, cloud textures, terrain, and
/// "marbled" surface fills. <see cref="FractalDimension"/>
/// controls the smoothness — values near 2.0 give textbook
/// rough-fractal output; 2.5 is smoother, 1.5 is rougher / spikier.</para>
/// </summary>
public class VipsFractsurf : VipsOperation
{
    public VipsImage? Out { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Octaves { get; set; } = 6;
    /// <summary>Largest cell size of the base octave; subsequent octaves halve.</summary>
    public int BaseCellSize { get; set; } = 256;
    /// <summary>Fractal dimension; 2.0 is "textbook 1/f". Larger = smoother.</summary>
    public double FractalDimension { get; set; } = 2.5;
    public int Seed { get; set; } = 0;

    public override int Build()
    {
        if (Width < 1 || Height < 1) return -1;
        if (Octaves < 1 || Octaves > 16) return -1;
        if (BaseCellSize < 1) return -1;

        // Pre-compute the summed surface once at Build — fractal noise
        // is fundamentally global and we want determinism per-seed.
        var buf = new byte[Width * Height * 4];
        // Each octave: amplitude factor decays as (1 / 2^(d-1)) per
        // doubling of frequency, where d is the fractal dimension.
        double ampDecay = Math.Pow(0.5, 3 - FractalDimension);
        double amp = 1.0;
        int cell = BaseCellSize;

        for (int oct = 0; oct < Octaves; oct++)
        {
            int cellSize = Math.Max(1, cell);
            int seed = Seed != 0 ? Seed + oct : oct + 1;
            var perm = BuildPermutation(seed);
            for (int y = 0; y < Height; y++)
            {
                double yc = y / (double)cellSize;
                for (int x = 0; x < Width; x++)
                {
                    double xc = x / (double)cellSize;
                    int off = (y * Width + x) * 4;
                    float prev = oct == 0 ? 0
                        : BinaryPrimitives.ReadSingleLittleEndian(buf.AsSpan(off, 4));
                    float v = (float)(prev + amp * Noise2D(xc, yc, perm));
                    BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(off, 4), v);
                }
            }
            amp *= ampDecay;
            cell = Math.Max(1, cell / 2);
        }

        Out = new VipsImage
        {
            Width = Width, Height = Height, Bands = 1, BandFormat = VipsBandFormat.Float,
            Interpretation = VipsInterpretation.BW,
            XRes = 1, YRes = 1,
            GenerateFn = Generate, ClientB = buf,
        };
        Out.SetPipeline(VipsDemandStyle.Any);
        return 0;
    }

    public override int GetCacheKey()
    {
        var h = new HashCode();
        h.Add("Fractsurf"); h.Add(Width); h.Add(Height);
        h.Add(Octaves); h.Add(BaseCellSize); h.Add(FractalDimension); h.Add(Seed);
        return h.ToHashCode();
    }

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

    // Same Perlin noise primitives as VipsPerlin — duplicated locally to
    // keep the surface free of cross-file private dependencies.
    private static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);
    private static double Lerp(double t, double a, double b) => a + t * (b - a);
    private static double Grad(int hash, double rx, double ry)
    {
        int h = hash & 7;
        double u = h < 4 ? rx : ry;
        double v = h < 4 ? ry : rx;
        return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
    }
    private static double Noise2D(double x, double y, int[] perm)
    {
        int X = (int)Math.Floor(x) & 255;
        int Y = (int)Math.Floor(y) & 255;
        x -= Math.Floor(x);
        y -= Math.Floor(y);
        double u = Fade(x), v = Fade(y);
        int A = perm[X] + Y, B = perm[X + 1] + Y;
        return Lerp(v,
            Lerp(u, Grad(perm[A], x, y), Grad(perm[B], x - 1, y)),
            Lerp(u, Grad(perm[A + 1], x, y - 1), Grad(perm[B + 1], x - 1, y - 1)));
    }
    private static int[] BuildPermutation(int seed)
    {
        var p = new int[256];
        for (int i = 0; i < 256; i++) p[i] = i;
        var rng = new Random(seed);
        for (int i = 255; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (p[i], p[j]) = (p[j], p[i]);
        }
        var perm = new int[512];
        for (int i = 0; i < 512; i++) perm[i] = p[i & 255];
        return perm;
    }
}
