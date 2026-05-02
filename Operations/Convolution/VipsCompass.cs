using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Convolution;

/// <summary>
/// Compass-pattern edge response. Applies eight rotations of the
/// Kirsch operator (3×3, oriented N, NE, E, SE, S, SW, W, NW) and
/// returns the absolute maximum response per pixel — a simple
/// directional edge detector with stronger orientation discrimination
/// than Sobel.
///
/// <para>Mirrors libvips <c>vips_compass</c>. UChar in → UChar out
/// (clamped to 0..255). Per-band.</para>
/// </summary>
public class VipsCompassEdge : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }

    /// <summary>Eight Kirsch kernels (N, NE, E, SE, S, SW, W, NW).</summary>
    private static readonly int[][,] Kernels =
    {
        new int[3, 3] { { -3, -3, +5 }, { -3, 0, +5 }, { -3, -3, +5 } }, // E
        new int[3, 3] { { -3, +5, +5 }, { -3, 0, +5 }, { -3, -3, -3 } }, // NE
        new int[3, 3] { { +5, +5, +5 }, { -3, 0, -3 }, { -3, -3, -3 } }, // N
        new int[3, 3] { { +5, +5, -3 }, { +5, 0, -3 }, { -3, -3, -3 } }, // NW
        new int[3, 3] { { +5, -3, -3 }, { +5, 0, -3 }, { +5, -3, -3 } }, // W
        new int[3, 3] { { -3, -3, -3 }, { +5, 0, -3 }, { +5, +5, -3 } }, // SW
        new int[3, 3] { { -3, -3, -3 }, { -3, 0, -3 }, { +5, +5, +5 } }, // S
        new int[3, 3] { { -3, -3, -3 }, { -3, 0, +5 }, { -3, +5, +5 } }, // SE
    };

    public override int Build()
    {
        if (In == null) return -1;
        if (In.BandFormat != VipsBandFormat.UChar) return -1;

        Out = new VipsImage
        {
            Width = In.Width, Height = In.Height,
            Bands = In.Bands, BandFormat = VipsBandFormat.UChar,
            Interpretation = In.Interpretation,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.FatStrip, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Compass", RuntimeHelpers.GetHashCode(In));

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        VipsRect r = outRegion.Valid;

        var inRect = new VipsRect(r.Left - 1, r.Top - 1, r.Width + 2, r.Height + 2);
        var clipped = VipsRect.Intersect(inRect, new VipsRect(0, 0, @in.Width, @in.Height));
        if (inRegion.Prepare(clipped) != 0) return -1;

        int bands = @in.Bands;

        for (int y = 0; y < r.Height; y++)
        {
            int gy = r.Top + y;
            var outAddr = outRegion.GetAddress(r.Left, gy);
            for (int x = 0; x < r.Width; x++)
            {
                int gx = r.Left + x;
                for (int bnd = 0; bnd < bands; bnd++)
                {
                    int best = 0;
                    foreach (var k in Kernels)
                    {
                        int sum = 0;
                        for (int my = 0; my < 3; my++)
                            for (int mx = 0; mx < 3; mx++)
                            {
                                int sx = gx + mx - 1;
                                int sy = gy + my - 1;
                                if (sx < 0) sx = 0; else if (sx >= @in.Width) sx = @in.Width - 1;
                                if (sy < 0) sy = 0; else if (sy >= @in.Height) sy = @in.Height - 1;
                                sum += inRegion.GetAddress(sx, sy)[bnd] * k[my, mx];
                            }
                        int abs = Math.Abs(sum);
                        if (abs > best) best = abs;
                    }
                    // Kirsch responses can run up to 15 * 255; rescale.
                    outAddr[x * bands + bnd] = (byte)Math.Clamp(best / 15, 0, 255);
                }
            }
        }
        return 0;
    }
}
