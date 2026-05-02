using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Create;

/// <summary>
/// 2D Worley (cellular) noise. Tile space into integer cells, drop a
/// pseudo-random feature point in each cell, and at every pixel
/// return the (Euclidean) distance to the closest feature point —
/// the F1 distance pattern. Mirrors libvips <c>vips_worley</c>.
///
/// <para>The classic recipe for stone, leather, scales, voronoi-style
/// graphics, and as a sub-component of cloud / terrain shaders.
/// Output is Float, distances in pixels (so a small image with large
/// cells gives small output values; large image / small cell sizes
/// give visually busier patterns).</para>
/// </summary>
public class VipsWorley : VipsOperation
{
    public VipsImage? Out { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int CellSize { get; set; } = 256;
    public int Seed { get; set; } = 0;

    public override int Build()
    {
        if (Width < 1 || Height < 1 || CellSize < 1) return -1;

        Out = new VipsImage
        {
            Width = Width, Height = Height, Bands = 1, BandFormat = VipsBandFormat.Float,
            Interpretation = VipsInterpretation.BW,
            XRes = 1, YRes = 1,
            GenerateFn = Generate, ClientB = (CellSize, Seed),
        };
        Out.SetPipeline(VipsDemandStyle.Any);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Worley", Width, Height, CellSize, Seed);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var (cell, seed) = ((int, int))b!;
        VipsRect r = outRegion.Valid;

        for (int y = 0; y < r.Height; y++)
        {
            int gy = r.Top + y;
            int cy = gy / cell;
            var outAddr = outRegion.GetAddress(r.Left, gy);
            for (int x = 0; x < r.Width; x++)
            {
                int gx = r.Left + x;
                int cx = gx / cell;
                double best = double.MaxValue;
                // Walk the 9 surrounding cells; each cell hashes to a feature point.
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int ncx = cx + dx, ncy = cy + dy;
                    var (fx, fy) = FeaturePoint(ncx, ncy, cell, seed);
                    double ex = (gx - fx), ey = (gy - fy);
                    double d2 = ex * ex + ey * ey;
                    if (d2 < best) best = d2;
                }
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(x * 4, 4), (float)Math.Sqrt(best));
            }
        }
        return 0;
    }

    /// <summary>Hash (cellX, cellY, seed) → feature-point pixel coords inside the cell.</summary>
    private static (double, double) FeaturePoint(int cx, int cy, int cell, int seed)
    {
        // Wang-style hash; deterministic per-cell.
        uint h = (uint)(cx * 374761393 ^ cy * 668265263 ^ seed * 2147483647);
        h = (h ^ (h >> 13)) * 1274126177;
        h ^= h >> 16;
        double rx = (h & 0xFFFF) / 65535.0;
        double ry = ((h >> 16) & 0xFFFF) / 65535.0;
        return (cx * cell + rx * cell, cy * cell + ry * cell);
    }
}
