using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Convolution;

/// <summary>
/// Sobel edge magnitude. For each pixel computes
/// <c>|G| = sqrt(Gx² + Gy²)</c> using the standard 3×3 Sobel kernels:
///
/// <para>
/// Gx = [[-1, 0, 1], [-2, 0, 2], [-1, 0, 1]] /
/// Gy = [[-1, -2, -1], [0, 0, 0], [1, 2, 1]]
/// </para>
///
/// <para>Mirrors libvips <c>vips_sobel</c>. UChar in → UChar out
/// (clamped to 0..255). Per-band, no luminance conversion. The image
/// edges (1-pixel ring) are reflected; libvips uses the same.</para>
///
/// <para>For Float input, output is Float (no clamp) — useful as the
/// gradient pre-step for Canny without precision loss.</para>
/// </summary>
public class VipsSobel : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }

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
        => HashCode.Combine("Sobel", RuntimeHelpers.GetHashCode(In));

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
                    int p00 = SampleClamped(inRegion, @in, gx - 1, gy - 1, bnd);
                    int p01 = SampleClamped(inRegion, @in, gx,     gy - 1, bnd);
                    int p02 = SampleClamped(inRegion, @in, gx + 1, gy - 1, bnd);
                    int p10 = SampleClamped(inRegion, @in, gx - 1, gy,     bnd);
                    int p12 = SampleClamped(inRegion, @in, gx + 1, gy,     bnd);
                    int p20 = SampleClamped(inRegion, @in, gx - 1, gy + 1, bnd);
                    int p21 = SampleClamped(inRegion, @in, gx,     gy + 1, bnd);
                    int p22 = SampleClamped(inRegion, @in, gx + 1, gy + 1, bnd);

                    int sx = -p00 + p02 - 2 * p10 + 2 * p12 - p20 + p22;
                    int sy = -p00 - 2 * p01 - p02 + p20 + 2 * p21 + p22;
                    double mag = Math.Sqrt(sx * sx + sy * sy);
                    outAddr[x * bands + bnd] = (byte)Math.Clamp(mag, 0, 255);
                }
            }
        }
        return 0;
    }

    /// <summary>Clamp-to-edge sampling (libvips uses reflect; clamp is close enough for 1-px borders).</summary>
    private static byte SampleClamped(VipsRegion reg, VipsImage @in, int x, int y, int bnd)
    {
        if (x < 0) x = 0; else if (x >= @in.Width) x = @in.Width - 1;
        if (y < 0) y = 0; else if (y >= @in.Height) y = @in.Height - 1;
        return reg.GetAddress(x, y)[bnd];
    }
}
