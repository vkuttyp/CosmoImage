using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Create;

/// <summary>
/// 2D Perlin noise (Ken Perlin, "Improving Noise" 2002). Smooth
/// gradient-based procedural texture in <c>[-1, 1]</c>; the building
/// block of clouds, terrain, marble, and most "natural-looking"
/// procedural patterns. Mirrors libvips <c>vips_perlin</c>.
///
/// <para><see cref="CellSize"/> sets the size of one Perlin cell — at
/// the default 256, an image of 256×256 contains exactly one
/// fundamental period. Larger images repeat the pattern; smaller
/// images sample inside one period. <see cref="Seed"/> selects which
/// permutation table is used; <c>0</c> uses Perlin's reference
/// table.</para>
/// </summary>
public class VipsPerlin : VipsOperation
{
    public VipsImage? Out { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int CellSize { get; set; } = 256;
    public int Seed { get; set; } = 0;

    public override int Build()
    {
        if (Width < 1 || Height < 1 || CellSize < 1) return -1;

        var perm = BuildPermutation(Seed);
        Out = new VipsImage
        {
            Width = Width, Height = Height, Bands = 1, BandFormat = VipsBandFormat.Float,
            Interpretation = VipsInterpretation.BW,
            XRes = 1, YRes = 1,
            GenerateFn = Generate, ClientB = (perm, CellSize),
        };
        Out.SetPipeline(VipsDemandStyle.Any);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Perlin", Width, Height, CellSize, Seed);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var (perm, cell) = ((int[], int))b!;
        VipsRect r = outRegion.Valid;
        for (int y = 0; y < r.Height; y++)
        {
            double yc = (r.Top + y) / (double)cell;
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                double xc = (r.Left + x) / (double)cell;
                float v = (float)Noise2D(xc, yc, perm);
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(x * 4, 4), v);
            }
        }
        return 0;
    }

    /// <summary>Perlin's improved fade curve: 6t⁵ − 15t⁴ + 10t³.</summary>
    private static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);

    private static double Lerp(double t, double a, double b) => a + t * (b - a);

    /// <summary>Project gradient hash onto direction (rx, ry).</summary>
    private static double Grad(int hash, double rx, double ry)
    {
        // 8 unit-length 2D gradients, indexed by low 3 bits of hash.
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

    /// <summary>Build the 512-entry permutation table from a seed.</summary>
    private static int[] BuildPermutation(int seed)
    {
        var p = new int[256];
        for (int i = 0; i < 256; i++) p[i] = i;
        var rng = seed != 0 ? new Random(seed) : new Random(42);
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
