using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Convolution;

/// <summary>
/// N-pass box blur via running-sum. Each pass is O(W·H) regardless
/// of <see cref="Radius"/> — dramatically faster than direct
/// Gaussian convolution for large radii. Three or more passes
/// approximate a Gaussian closely (central-limit theorem).
///
/// <para>For a target Gaussian sigma σ with N passes, the equivalent
/// box radius is roughly <c>r ≈ √(12σ² / N)</c>. For
/// <c>BoxBlur(radius=3, passes=3)</c> the effective sigma is about
/// 3.0, comparable to <c>GaussBlur(sigma=3)</c> at a fraction of the
/// cost.</para>
///
/// <para>Materialises the input — running-sum needs strict
/// row-by-row access. UChar / Float branches; per-band averaging
/// preserves brightness.</para>
/// </summary>
public class VipsBoxBlur : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public int Radius { get; set; } = 3;
    public int Passes { get; set; } = 3;

    public override int Build()
    {
        if (In == null) return -1;
        if (Radius < 1 || Passes < 1) return -1;
        if (In.BandFormat != VipsBandFormat.UChar && In.BandFormat != VipsBandFormat.Float) return -1;

        // Materialise.
        byte[] inPixels;
        if (In.Pixels is { } existing) inPixels = existing;
        else
        {
            var sink = new MemorySink(In);
            sink.RunAsync().GetAwaiter().GetResult();
            inPixels = sink.Pixels;
        }

        int W = In.Width, H = In.Height, bands = In.Bands;
        bool isFloat = In.BandFormat == VipsBandFormat.Float;
        int sampleSize = isFloat ? 4 : 1;

        // Work in Float internally so successive passes don't lose
        // precision; cast back at the end if input was UChar.
        var work = new float[W * H * bands];
        if (isFloat)
        {
            for (int i = 0; i < work.Length; i++)
                work[i] = BinaryPrimitives.ReadSingleLittleEndian(inPixels.AsSpan(i * 4, 4));
        }
        else
        {
            for (int i = 0; i < work.Length; i++) work[i] = inPixels[i];
        }

        var scratch = new float[W * H * bands];
        for (int p = 0; p < Passes; p++)
        {
            BoxPass1D(work, scratch, W, H, bands, Radius, horizontal: true);
            BoxPass1D(scratch, work, W, H, bands, Radius, horizontal: false);
        }

        // Pack into output buffer.
        var outBytes = new byte[W * H * bands * sampleSize];
        if (isFloat)
        {
            for (int i = 0; i < work.Length; i++)
                BinaryPrimitives.WriteSingleLittleEndian(outBytes.AsSpan(i * 4, 4), work[i]);
        }
        else
        {
            for (int i = 0; i < work.Length; i++)
                outBytes[i] = (byte)Math.Clamp(Math.Round(work[i]), 0, 255);
        }

        Out = new VipsImage
        {
            Width = W, Height = H, Bands = bands, BandFormat = In.BandFormat,
            Interpretation = In.Interpretation,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In, ClientB = outBytes,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("BoxBlur", RuntimeHelpers.GetHashCode(In), Radius, Passes);

    /// <summary>
    /// Single-axis box pass via running-sum. O(N) per row regardless
    /// of radius. Edges clamp-to-edge.
    /// </summary>
    private static void BoxPass1D(float[] src, float[] dst, int W, int H, int bands,
        int r, bool horizontal)
    {
        double divisor = 2.0 * r + 1;
        if (horizontal)
        {
            for (int y = 0; y < H; y++)
            {
                int rowBase = y * W * bands;
                for (int bnd = 0; bnd < bands; bnd++)
                {
                    // Initialise running sum over the first window centred on x=0.
                    double sum = 0;
                    for (int k = -r; k <= r; k++)
                    {
                        int sx = Math.Clamp(k, 0, W - 1);
                        sum += src[rowBase + sx * bands + bnd];
                    }
                    dst[rowBase + 0 * bands + bnd] = (float)(sum / divisor);

                    for (int x = 1; x < W; x++)
                    {
                        int oldX = Math.Clamp(x - r - 1, 0, W - 1);
                        int newX = Math.Clamp(x + r, 0, W - 1);
                        sum += src[rowBase + newX * bands + bnd];
                        sum -= src[rowBase + oldX * bands + bnd];
                        dst[rowBase + x * bands + bnd] = (float)(sum / divisor);
                    }
                }
            }
        }
        else
        {
            for (int x = 0; x < W; x++)
            {
                for (int bnd = 0; bnd < bands; bnd++)
                {
                    double sum = 0;
                    for (int k = -r; k <= r; k++)
                    {
                        int sy = Math.Clamp(k, 0, H - 1);
                        sum += src[(sy * W + x) * bands + bnd];
                    }
                    dst[(0 * W + x) * bands + bnd] = (float)(sum / divisor);

                    for (int y = 1; y < H; y++)
                    {
                        int oldY = Math.Clamp(y - r - 1, 0, H - 1);
                        int newY = Math.Clamp(y + r, 0, H - 1);
                        sum += src[(newY * W + x) * bands + bnd];
                        sum -= src[(oldY * W + x) * bands + bnd];
                        dst[(y * W + x) * bands + bnd] = (float)(sum / divisor);
                    }
                }
            }
        }
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var buf = (byte[])b!;
        VipsImage @out = outRegion.Image;
        VipsRect r = outRegion.Valid;
        int W = @out.Width;
        int pelSize = @out.SizeOfPel;
        for (int y = 0; y < r.Height; y++)
        {
            var addr = outRegion.GetAddress(r.Left, r.Top + y);
            int srcOff = ((r.Top + y) * W + r.Left) * pelSize;
            buf.AsSpan(srcOff, r.Width * pelSize).CopyTo(addr);
        }
        return 0;
    }
}
