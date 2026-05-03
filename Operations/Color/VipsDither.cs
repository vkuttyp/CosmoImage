using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Color;

public enum VipsDitherMethod
{
    /// <summary>Floyd-Steinberg 4-cell error diffusion.</summary>
    FloydSteinberg = 0,
    /// <summary>Atkinson 6-cell error diffusion (lighter, more contrast).</summary>
    Atkinson = 1,
    /// <summary>Burkes 7-cell error diffusion (smoother than FS).</summary>
    Burkes = 2,
    /// <summary>Stevenson-Arce 12-cell error diffusion (rotational symmetry).</summary>
    StevensonArce = 3,
    /// <summary>Sierra 10-cell error diffusion.</summary>
    Sierra = 4,
    /// <summary>4×4 Bayer ordered dither (no error diffusion — streamable).</summary>
    Bayer4x4 = 5,
    /// <summary>8×8 Bayer ordered dither.</summary>
    Bayer8x8 = 6,
}

/// <summary>
/// Quantise to <see cref="Levels"/> evenly-spaced levels per band
/// using the chosen dither method. Mirrors ImageSharp's
/// <c>Dither(IDither, levels)</c> processor.
///
/// <para>Error-diffusion methods (Floyd-Steinberg, Atkinson, Burkes,
/// Stevenson-Arce, Sierra) materialise the input and walk pixels in
/// scan order; Bayer ordered methods are streamable. UChar 1-band or
/// 3-band input only.</para>
///
/// <para><see cref="Levels"/> = 2 (default) gives a 1-bit-per-band
/// look (the standard "newsprint" dither). Higher values reduce posterisation
/// while still smoothing tonal banding via the dither pattern.</para>
/// </summary>
public class VipsDither : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public VipsDitherMethod Method { get; set; } = VipsDitherMethod.FloydSteinberg;
    public int Levels { get; set; } = 2;

    public override int Build()
    {
        if (In == null) return -1;
        if (In.BandFormat != VipsBandFormat.UChar) return -1;
        if (Levels < 2 || Levels > 256) return -1;

        // Materialise input — error diffusion is fundamentally serial.
        byte[] inPixels;
        if (In.Pixels is { } existing) inPixels = existing;
        else
        {
            var sink = new MemorySink(In);
            sink.RunAsync().GetAwaiter().GetResult();
            inPixels = sink.Pixels;
        }
        int W = In.Width, H = In.Height, bands = In.Bands;
        var output = new byte[W * H * bands];

        switch (Method)
        {
            case VipsDitherMethod.FloydSteinberg:
                ErrorDiffuse(inPixels, output, W, H, bands, Levels, FloydSteinbergKernel);
                break;
            case VipsDitherMethod.Atkinson:
                ErrorDiffuse(inPixels, output, W, H, bands, Levels, AtkinsonKernel);
                break;
            case VipsDitherMethod.Burkes:
                ErrorDiffuse(inPixels, output, W, H, bands, Levels, BurkesKernel);
                break;
            case VipsDitherMethod.StevensonArce:
                ErrorDiffuse(inPixels, output, W, H, bands, Levels, StevensonArceKernel);
                break;
            case VipsDitherMethod.Sierra:
                ErrorDiffuse(inPixels, output, W, H, bands, Levels, SierraKernel);
                break;
            case VipsDitherMethod.Bayer4x4:
                Ordered(inPixels, output, W, H, bands, Levels, Bayer4x4Matrix, 4);
                break;
            case VipsDitherMethod.Bayer8x8:
                Ordered(inPixels, output, W, H, bands, Levels, Bayer8x8Matrix, 8);
                break;
        }

        Out = new VipsImage
        {
            Width = W, Height = H, Bands = bands, BandFormat = VipsBandFormat.UChar,
            Interpretation = In.Interpretation,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In, ClientB = output,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Dither", RuntimeHelpers.GetHashCode(In), Method, Levels);

    /// <summary>Quantise <paramref name="v"/> to the nearest of <paramref name="levels"/> evenly-spaced points in [0, 255].</summary>
    private static byte Quantise(double v, int levels)
    {
        if (v < 0) v = 0; else if (v > 255) v = 255;
        // levels = 2 → outputs 0 or 255; levels = 4 → outputs 0/85/170/255 etc.
        int step = (int)Math.Round(v * (levels - 1) / 255.0);
        return (byte)(step * 255 / (levels - 1));
    }

    /// <summary>
    /// Error-diffusion driver. Walks pixels left-to-right, top-to-
    /// bottom. <paramref name="kernel"/> is a list of (dx, dy, weight)
    /// triples that distribute the quantisation error to following
    /// pixels; weights sum to 1.
    /// </summary>
    private static void ErrorDiffuse(byte[] src, byte[] dst, int W, int H, int bands, int levels,
        (int dx, int dy, double w)[] kernel)
    {
        // Work in float so accumulated error doesn't clip per-pixel.
        var work = new float[W * H * bands];
        for (int i = 0; i < work.Length; i++) work[i] = src[i];

        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                for (int b = 0; b < bands; b++)
                {
                    int idx = (y * W + x) * bands + b;
                    double v = work[idx];
                    byte q = Quantise(v, levels);
                    dst[idx] = q;
                    double err = v - q;
                    foreach (var (dx, dy, w) in kernel)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || nx >= W || ny < 0 || ny >= H) continue;
                        work[(ny * W + nx) * bands + b] += (float)(err * w);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Ordered dither using a tile threshold matrix. Adds a small
    /// per-position bias before quantising — no error propagation, so
    /// streamable and parallelisable.
    /// </summary>
    private static void Ordered(byte[] src, byte[] dst, int W, int H, int bands, int levels,
        int[,] matrix, int n)
    {
        // Normalise matrix to roughly ±step/2 about zero.
        double step = 255.0 / (levels - 1);
        int matrixMax = n * n;
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                double bias = (matrix[y % n, x % n] / (double)matrixMax - 0.5) * step;
                for (int b = 0; b < bands; b++)
                {
                    int idx = (y * W + x) * bands + b;
                    dst[idx] = Quantise(src[idx] + bias, levels);
                }
            }
        }
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var output = (byte[])b!;
        VipsImage @out = outRegion.Image;
        VipsRect r = outRegion.Valid;
        int W = @out.Width;
        int pelSize = @out.SizeOfPel;
        for (int y = 0; y < r.Height; y++)
        {
            var addr = outRegion.GetAddress(r.Left, r.Top + y);
            int srcOff = ((r.Top + y) * W + r.Left) * pelSize;
            output.AsSpan(srcOff, r.Width * pelSize).CopyTo(addr);
        }
        return 0;
    }

    // ---- Error-diffusion kernels ----
    private static readonly (int dx, int dy, double w)[] FloydSteinbergKernel =
    {
        (1, 0, 7.0 / 16), (-1, 1, 3.0 / 16), (0, 1, 5.0 / 16), (1, 1, 1.0 / 16),
    };
    private static readonly (int dx, int dy, double w)[] AtkinsonKernel =
    {
        (1, 0, 1.0 / 8), (2, 0, 1.0 / 8),
        (-1, 1, 1.0 / 8), (0, 1, 1.0 / 8), (1, 1, 1.0 / 8),
        (0, 2, 1.0 / 8),
        // Atkinson distributes only 6/8 of the error — the remaining
        // 2/8 is dropped, which produces the trademark contrasty look.
    };
    private static readonly (int dx, int dy, double w)[] BurkesKernel =
    {
        (1, 0, 8.0 / 32), (2, 0, 4.0 / 32),
        (-2, 1, 2.0 / 32), (-1, 1, 4.0 / 32), (0, 1, 8.0 / 32),
        (1, 1, 4.0 / 32), (2, 1, 2.0 / 32),
    };
    private static readonly (int dx, int dy, double w)[] StevensonArceKernel =
    {
        (2, 0, 32.0 / 200),
        (-3, 1, 12.0 / 200), (-1, 1, 26.0 / 200), (1, 1, 30.0 / 200), (3, 1, 16.0 / 200),
        (-2, 2, 12.0 / 200), (0, 2, 26.0 / 200), (2, 2, 12.0 / 200),
        (-3, 3, 5.0 / 200), (-1, 3, 12.0 / 200), (1, 3, 12.0 / 200), (3, 3, 5.0 / 200),
    };
    private static readonly (int dx, int dy, double w)[] SierraKernel =
    {
        (1, 0, 5.0 / 32), (2, 0, 3.0 / 32),
        (-2, 1, 2.0 / 32), (-1, 1, 4.0 / 32), (0, 1, 5.0 / 32), (1, 1, 4.0 / 32), (2, 1, 2.0 / 32),
        (-1, 2, 2.0 / 32), (0, 2, 3.0 / 32), (1, 2, 2.0 / 32),
    };

    // ---- Bayer threshold matrices ----
    private static readonly int[,] Bayer4x4Matrix = new int[,]
    {
        {  0,  8,  2, 10 },
        { 12,  4, 14,  6 },
        {  3, 11,  1,  9 },
        { 15,  7, 13,  5 },
    };
    private static readonly int[,] Bayer8x8Matrix = new int[,]
    {
        {  0, 32,  8, 40,  2, 34, 10, 42 },
        { 48, 16, 56, 24, 50, 18, 58, 26 },
        { 12, 44,  4, 36, 14, 46,  6, 38 },
        { 60, 28, 52, 20, 62, 30, 54, 22 },
        {  3, 35, 11, 43,  1, 33,  9, 41 },
        { 51, 19, 59, 27, 49, 17, 57, 25 },
        { 15, 47,  7, 39, 13, 45,  5, 37 },
        { 63, 31, 55, 23, 61, 29, 53, 21 },
    };
}
