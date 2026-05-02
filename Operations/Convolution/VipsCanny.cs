using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Convolution;

/// <summary>
/// Canny edge detector. Five stages:
/// <list type="number">
///   <item>Optional Gaussian blur (sigma) to denoise.</item>
///   <item>Sobel gradients Gx, Gy → magnitude + orientation.</item>
///   <item>Non-maximum suppression — thin edges to single-pixel ridges
///     by keeping each pixel only if it is the local max along its
///     gradient direction.</item>
///   <item>Double threshold — strong (≥ <c>HighThreshold</c>),
///     weak (≥ <c>LowThreshold</c>), background.</item>
///   <item>Hysteresis — promote weak pixels that are 8-connected to
///     a strong one (transitively).</item>
/// </list>
///
/// <para>Mirrors libvips <c>vips_canny</c>. UChar in → UChar out (binary
/// 0 / 255 edge map). 1-band only.</para>
///
/// <para>Canny needs whole-image access for hysteresis; we materialize
/// once at <c>Build</c> time and stream the result through
/// <c>Generate</c>.</para>
/// </summary>
public class VipsCanny : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public double Sigma { get; set; } = 1.4;
    public int LowThreshold { get; set; } = 20;
    public int HighThreshold { get; set; } = 60;

    public override int Build()
    {
        if (In == null) return -1;
        if (In.BandFormat != VipsBandFormat.UChar) return -1;
        if (In.Bands != 1) return -1;
        if (LowThreshold < 0 || HighThreshold < LowThreshold) return -1;

        // Materialise input (Canny is fundamentally global).
        byte[] pixels;
        if (In.Pixels is { } existing) pixels = existing;
        else
        {
            var sink = new MemorySink(In);
            sink.RunAsync().GetAwaiter().GetResult();
            pixels = sink.Pixels;
        }

        var edge = ComputeEdgeMap(pixels, In.Width, In.Height, Sigma, LowThreshold, HighThreshold);

        Out = new VipsImage
        {
            Width = In.Width, Height = In.Height, Bands = 1,
            BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In, ClientB = edge,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Canny", RuntimeHelpers.GetHashCode(In), Sigma, LowThreshold, HighThreshold);

    private static byte[] ComputeEdgeMap(byte[] pixels, int W, int H, double sigma, int lo, int hi)
    {
        // 1) Gaussian blur via separable 1D pass. Build kernel.
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
        {
            for (int x = 0; x < W; x++)
            {
                double s = 0;
                for (int k = 0; k < kernel.Length; k++)
                {
                    int sx = Math.Clamp(x + k - radius, 0, W - 1);
                    s += pixels[y * W + sx] * kernel[k];
                }
                hor[y * W + x] = s;
            }
        }
        var blur = new double[W * H];
        for (int x = 0; x < W; x++)
        {
            for (int y = 0; y < H; y++)
            {
                double s = 0;
                for (int k = 0; k < kernel.Length; k++)
                {
                    int sy = Math.Clamp(y + k - radius, 0, H - 1);
                    s += hor[sy * W + x] * kernel[k];
                }
                blur[y * W + x] = s;
            }
        }

        // 2) Sobel gradients.
        var mag = new double[W * H];
        var ang = new double[W * H];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                double Gx = -B(blur, W, H, x - 1, y - 1) + B(blur, W, H, x + 1, y - 1)
                          - 2 * B(blur, W, H, x - 1, y) + 2 * B(blur, W, H, x + 1, y)
                          - B(blur, W, H, x - 1, y + 1) + B(blur, W, H, x + 1, y + 1);
                double Gy = -B(blur, W, H, x - 1, y - 1) - 2 * B(blur, W, H, x, y - 1) - B(blur, W, H, x + 1, y - 1)
                          + B(blur, W, H, x - 1, y + 1) + 2 * B(blur, W, H, x, y + 1) + B(blur, W, H, x + 1, y + 1);
                mag[y * W + x] = Math.Sqrt(Gx * Gx + Gy * Gy);
                ang[y * W + x] = Math.Atan2(Gy, Gx) * 180 / Math.PI;
            }
        }

        // 3) Non-maximum suppression — quantise direction to 0/45/90/135.
        var nms = new double[W * H];
        for (int y = 1; y < H - 1; y++)
        {
            for (int x = 1; x < W - 1; x++)
            {
                double a = ang[y * W + x];
                if (a < 0) a += 180;
                int dir = a switch
                {
                    < 22.5 => 0,
                    < 67.5 => 1,
                    < 112.5 => 2,
                    < 157.5 => 3,
                    _ => 0,
                };
                double m = mag[y * W + x];
                double n1, n2;
                switch (dir)
                {
                    case 0: n1 = mag[y * W + (x - 1)]; n2 = mag[y * W + (x + 1)]; break;
                    case 1: n1 = mag[(y - 1) * W + (x + 1)]; n2 = mag[(y + 1) * W + (x - 1)]; break;
                    case 2: n1 = mag[(y - 1) * W + x]; n2 = mag[(y + 1) * W + x]; break;
                    default: n1 = mag[(y - 1) * W + (x - 1)]; n2 = mag[(y + 1) * W + (x + 1)]; break;
                }
                if (m >= n1 && m >= n2) nms[y * W + x] = m;
            }
        }

        // 4) Double-threshold + 5) hysteresis. Mark strong = 255, weak
        // = 128 first; then BFS-promote any weak adjacent to strong.
        var edge = new byte[W * H];
        var stack = new Stack<int>();
        for (int i = 0; i < W * H; i++)
        {
            if (nms[i] >= hi) { edge[i] = 255; stack.Push(i); }
            else if (nms[i] >= lo) edge[i] = 128;
        }
        int[] dxs = { -1, 0, 1, -1, 1, -1, 0, 1 };
        int[] dys = { -1, -1, -1, 0, 0, 1, 1, 1 };
        while (stack.Count > 0)
        {
            int i = stack.Pop();
            int x = i % W, y = i / W;
            for (int n = 0; n < 8; n++)
            {
                int nx = x + dxs[n], ny = y + dys[n];
                if (nx < 0 || nx >= W || ny < 0 || ny >= H) continue;
                int j = ny * W + nx;
                if (edge[j] == 128) { edge[j] = 255; stack.Push(j); }
            }
        }
        // Drop unconnected weak pixels.
        for (int i = 0; i < edge.Length; i++) if (edge[i] != 255) edge[i] = 0;
        return edge;
    }

    private static double B(double[] buf, int W, int H, int x, int y)
    {
        if (x < 0) x = 0; else if (x >= W) x = W - 1;
        if (y < 0) y = 0; else if (y >= H) y = H - 1;
        return buf[y * W + x];
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        var edge = (byte[])b!;
        VipsImage @out = outRegion.Image;
        VipsRect r = outRegion.Valid;
        int W = @out.Width;

        for (int y = 0; y < r.Height; y++)
        {
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            int srcRow = (r.Top + y) * W + r.Left;
            edge.AsSpan(srcRow, r.Width).CopyTo(outAddr);
        }
        return 0;
    }
}
