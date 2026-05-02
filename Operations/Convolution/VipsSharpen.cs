using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Convolution;

/// <summary>
/// Tone-aware unsharp masking. Computes the unsharp signal
/// <c>diff = original − blurred</c> on a luminance band, scales it
/// with separately-controlled gains for shadows and highlights, then
/// adds the result back to all bands of the original (preserves
/// chroma).
///
/// <para>This is a simplified port of libvips' <c>vips_sharpen</c>.
/// libvips' version operates in 16-bit Lab and exposes a five-segment
/// tone curve via <c>m1/m2/x1/y2/y3</c>; here we stay in RGB/UChar and
/// expose just the three knobs that matter most: <c>Sigma</c> (the
/// blur radius), <c>M1</c> (highlight strength) and <c>M2</c> (shadow
/// strength). Both default to 1.0.</para>
///
/// <para>UChar input only. Single-band collapses to a plain unsharp;
/// 3-band input uses BT.601 luminance weights for the unsharp band.</para>
/// </summary>
public class VipsSharpen : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public double Sigma { get; set; } = 1.0;
    /// <summary>Highlight gain (positive diff). 1.0 leaves diff unchanged.</summary>
    public double M1 { get; set; } = 1.0;
    /// <summary>Shadow gain (negative diff). 1.0 leaves diff unchanged.</summary>
    public double M2 { get; set; } = 1.0;
    /// <summary>Threshold below which diff is dropped (suppresses noise sharpening).</summary>
    public int X1 { get; set; } = 2;

    public override int Build()
    {
        if (In == null) return -1;
        if (In.BandFormat != VipsBandFormat.UChar) return -1;
        if (Sigma <= 0 || Sigma > 100) return -1;
        if (In.Bands != 1 && In.Bands != 3) return -1;

        // Materialise input + compute the blurred luminance once.
        byte[] pixels;
        if (In.Pixels is { } existing) pixels = existing;
        else
        {
            var sink = new MemorySink(In);
            sink.RunAsync().GetAwaiter().GetResult();
            pixels = sink.Pixels;
        }
        var lum = ComputeLuminance(pixels, In.Width, In.Height, In.Bands);
        var blurredLum = GaussianBlur1D(lum, In.Width, In.Height, Sigma);

        Out = new VipsImage
        {
            Width = In.Width, Height = In.Height,
            Bands = In.Bands, BandFormat = VipsBandFormat.UChar,
            Interpretation = In.Interpretation,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In, ClientB = (lum, blurredLum, M1, M2, X1),
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Sharpen", RuntimeHelpers.GetHashCode(In), Sigma, M1, M2, X1);

    private static double[] ComputeLuminance(byte[] pixels, int W, int H, int bands)
    {
        var lum = new double[W * H];
        if (bands == 1)
        {
            for (int i = 0; i < W * H; i++) lum[i] = pixels[i];
            return lum;
        }
        // BT.601: Y = 0.299R + 0.587G + 0.114B.
        for (int i = 0; i < W * H; i++)
        {
            int b = i * bands;
            lum[i] = 0.299 * pixels[b] + 0.587 * pixels[b + 1] + 0.114 * pixels[b + 2];
        }
        return lum;
    }

    private static double[] GaussianBlur1D(double[] src, int W, int H, double sigma)
    {
        int radius = (int)Math.Max(1, Math.Ceiling(sigma * 3));
        var kernel = new double[2 * radius + 1];
        double norm = 0;
        for (int i = 0; i <= 2 * radius; i++)
        {
            double x = i - radius;
            kernel[i] = Math.Exp(-(x * x) / (2 * sigma * sigma));
            norm += kernel[i];
        }
        for (int i = 0; i < kernel.Length; i++) kernel[i] /= norm;

        var hor = new double[W * H];
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            double s = 0;
            for (int k = 0; k < kernel.Length; k++)
            {
                int sx = Math.Clamp(x + k - radius, 0, W - 1);
                s += src[y * W + sx] * kernel[k];
            }
            hor[y * W + x] = s;
        }
        var dst = new double[W * H];
        for (int x = 0; x < W; x++)
        for (int y = 0; y < H; y++)
        {
            double s = 0;
            for (int k = 0; k < kernel.Length; k++)
            {
                int sy = Math.Clamp(y + k - radius, 0, H - 1);
                s += hor[sy * W + x] * kernel[k];
            }
            dst[y * W + x] = s;
        }
        return dst;
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        var (lum, blurredLum, m1, m2, x1) = ((double[], double[], double, double, int))b!;
        VipsRect r = outRegion.Valid;

        if (inRegion.Prepare(r) != 0) return -1;

        int W = @in.Width;
        int bands = @in.Bands;
        for (int y = 0; y < r.Height; y++)
        {
            int gy = r.Top + y;
            var inAddr = inRegion.GetAddress(r.Left, gy);
            var outAddr = outRegion.GetAddress(r.Left, gy);
            for (int x = 0; x < r.Width; x++)
            {
                int gx = r.Left + x;
                int li = gy * W + gx;
                double diff = lum[li] - blurredLum[li];
                // Dead-band, then per-side gain.
                double gain = diff > 0 ? m1 : m2;
                double scaled = Math.Abs(diff) <= x1 ? 0 : (diff - Math.Sign(diff) * x1) * gain;
                int pelOff = x * bands;
                for (int bnd = 0; bnd < bands; bnd++)
                    outAddr[pelOff + bnd] = (byte)Math.Clamp(inAddr[pelOff + bnd] + scaled, 0, 255);
            }
        }
        return 0;
    }
}
