using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Effects;

/// <summary>
/// Painting-style effect: replaces each pixel with a dominant quantized color
/// from its neighborhood, producing a smudgy oil-paint look.
/// </summary>
public class VipsOilPaint : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }

    /// <summary>Neighborhood radius in pixels. Larger = chunkier brushstrokes.</summary>
    public double Radius { get; set; } = 3.0;

    /// <summary>Edge softness within the neighborhood.</summary>
    public double Sigma { get; set; } = 1.0;

    public override int Build()
    {
        if (In == null || Radius <= 0 || Sigma <= 0) return -1;
        var src = In;
        int radius = Math.Max(1, (int)Math.Round(Radius));
        int levels = Math.Clamp((int)Math.Round(8.0 + Sigma * 4.0), 4, 32);
        Out = ArtisticEffectsSupport.BuildLike(src, () => ArtisticEffectsSupport.ApplyOilPaint(src, radius, levels));
        return 0;
    }

    public override int GetCacheKey() => HashCode.Combine("OilPaint", RuntimeHelpers.GetHashCode(In), Radius, Sigma);
}

/// <summary>
/// Charcoal-sketch effect: grayscale + blurred-edge extraction + invert for a
/// charcoal-on-paper look.
/// </summary>
public class VipsCharcoal : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }

    /// <summary>Stroke radius — bigger = bolder strokes.</summary>
    public double Radius { get; set; } = 1.0;

    /// <summary>Edge sigma. Larger = softer detail.</summary>
    public double Sigma { get; set; } = 0.5;

    public override int Build()
    {
        if (In == null || Radius <= 0 || Sigma <= 0) return -1;
        var src = In;
        int radius = Math.Max(1, (int)Math.Round(Radius));
        int blurRadius = Math.Max(radius, (int)Math.Ceiling(Sigma * 2.0));
        Out = ArtisticEffectsSupport.BuildLike(src, () => ArtisticEffectsSupport.ApplyCharcoal(src, blurRadius));
        return 0;
    }

    public override int GetCacheKey() => HashCode.Combine("Charcoal", RuntimeHelpers.GetHashCode(In), Radius, Sigma);
}

/// <summary>
/// Sketch / pencil-line effect: directional edge trace producing pencil-style
/// linework.
/// </summary>
public class VipsSketch : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }

    /// <summary>Stroke length.</summary>
    public double Radius { get; set; } = 1.0;

    /// <summary>Edge softness.</summary>
    public double Sigma { get; set; } = 0.5;

    /// <summary>Stroke direction in degrees (0 = horizontal).</summary>
    public double Angle { get; set; } = 0.0;

    public override int Build()
    {
        if (In == null || Radius <= 0 || Sigma <= 0) return -1;
        var src = In;
        int radius = Math.Max(1, (int)Math.Round(Radius));
        int blurRadius = Math.Max(radius, (int)Math.Ceiling(Sigma * 2.0));
        Out = ArtisticEffectsSupport.BuildLike(src, () => ArtisticEffectsSupport.ApplySketch(src, blurRadius, Angle));
        return 0;
    }

    public override int GetCacheKey() => HashCode.Combine("Sketch", RuntimeHelpers.GetHashCode(In), Radius, Sigma, Angle);
}

internal static class ArtisticEffectsSupport
{
    internal static VipsImage BuildLike(VipsImage source, Func<byte[]> producer)
    {
        var image = new VipsImage
        {
            Width = source.Width,
            Height = source.Height,
            Bands = source.Bands,
            BandFormat = source.BandFormat,
            Interpretation = source.Interpretation,
            Coding = source.Coding,
            XRes = source.XRes,
            YRes = source.YRes,
            PixelsLazy = new Lazy<byte[]>(producer),
        };
        image.CopyMetadataFrom(source);
        image.SetPipeline(VipsDemandStyle.Any, source);
        return image;
    }

    private static byte[] Materialize(VipsImage source)
    {
        if (source.Pixels is { } existing) return existing;
        var sink = new MemorySink(source);
        sink.RunAsync().GetAwaiter().GetResult();
        return sink.Pixels;
    }

    internal static byte[] ApplyOilPaint(VipsImage source, int radius, int levels)
    {
        byte[] input = Materialize(source);
        int width = source.Width;
        int height = source.Height;
        int bands = source.Bands;
        var output = new byte[input.Length];
        int[] counts = new int[levels * levels * levels];
        int[] sumR = new int[counts.Length];
        int[] sumG = new int[counts.Length];
        int[] sumB = new int[counts.Length];

        for (int y = 0; y < height; y++)
        {
            int minY = Math.Max(0, y - radius);
            int maxY = Math.Min(height - 1, y + radius);
            for (int x = 0; x < width; x++)
            {
                Array.Clear(counts, 0, counts.Length);
                Array.Clear(sumR, 0, sumR.Length);
                Array.Clear(sumG, 0, sumG.Length);
                Array.Clear(sumB, 0, sumB.Length);

                int minX = Math.Max(0, x - radius);
                int maxX = Math.Min(width - 1, x + radius);
                int bestBin = 0;
                int bestCount = -1;

                for (int ny = minY; ny <= maxY; ny++)
                {
                    for (int nx = minX; nx <= maxX; nx++)
                    {
                        int srcOff = (ny * width + nx) * bands;
                        int r, g, b;
                        if (bands == 1)
                        {
                            r = g = b = input[srcOff];
                        }
                        else
                        {
                            r = input[srcOff];
                            g = input[srcOff + 1];
                            b = input[srcOff + 2];
                        }

                        int qr = r * levels / 256;
                        int qg = g * levels / 256;
                        int qb = b * levels / 256;
                        int bin = (qr * levels + qg) * levels + qb;
                        int count = ++counts[bin];
                        sumR[bin] += r;
                        sumG[bin] += g;
                        sumB[bin] += b;
                        if (count > bestCount)
                        {
                            bestCount = count;
                            bestBin = bin;
                        }
                    }
                }

                int dstOff = (y * width + x) * bands;
                byte rr = (byte)(sumR[bestBin] / Math.Max(1, counts[bestBin]));
                byte gg = (byte)(sumG[bestBin] / Math.Max(1, counts[bestBin]));
                byte bb = (byte)(sumB[bestBin] / Math.Max(1, counts[bestBin]));
                if (bands == 1)
                {
                    output[dstOff] = rr;
                }
                else
                {
                    output[dstOff] = rr;
                    output[dstOff + 1] = gg;
                    output[dstOff + 2] = bb;
                    if (bands == 4) output[dstOff + 3] = input[dstOff + 3];
                }
            }
        }

        return output;
    }

    internal static byte[] ApplyCharcoal(VipsImage source, int blurRadius)
    {
        byte[] gray = ToGray(Materialize(source), source.Width, source.Height, source.Bands);
        byte[] blurred = BoxBlur(gray, source.Width, source.Height, blurRadius);
        var output = new byte[source.Width * source.Height * source.Bands];
        byte[] input = Materialize(source);

        for (int i = 0; i < gray.Length; i++)
        {
            int edge = Math.Abs(gray[i] - blurred[i]);
            byte v = (byte)Math.Clamp(255 - edge * 3, 0, 255);
            int dstOff = i * source.Bands;
            output[dstOff] = v;
            if (source.Bands >= 3)
            {
                output[dstOff + 1] = v;
                output[dstOff + 2] = v;
                if (source.Bands == 4) output[dstOff + 3] = input[dstOff + 3];
            }
        }

        return output;
    }

    internal static byte[] ApplySketch(VipsImage source, int blurRadius, double angleDegrees)
    {
        byte[] input = Materialize(source);
        byte[] gray = ToGray(input, source.Width, source.Height, source.Bands);
        byte[] blurred = BoxBlur(gray, source.Width, source.Height, blurRadius);
        var output = new byte[source.Width * source.Height * source.Bands];
        double angle = angleDegrees * Math.PI / 180.0;
        double dirX = Math.Cos(angle);
        double dirY = Math.Sin(angle);

        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                int gx = Sample(blurred, source.Width, source.Height, x + 1, y) - Sample(blurred, source.Width, source.Height, x - 1, y);
                int gy = Sample(blurred, source.Width, source.Height, x, y + 1) - Sample(blurred, source.Width, source.Height, x, y - 1);
                int edge = (int)Math.Abs(gx * dirX + gy * dirY);
                byte v = (byte)Math.Clamp(255 - edge * 2, 0, 255);
                int dstOff = (y * source.Width + x) * source.Bands;
                output[dstOff] = v;
                if (source.Bands >= 3)
                {
                    output[dstOff + 1] = v;
                    output[dstOff + 2] = v;
                    if (source.Bands == 4) output[dstOff + 3] = input[dstOff + 3];
                }
            }
        }

        return output;
    }

    private static byte[] ToGray(byte[] input, int width, int height, int bands)
    {
        var gray = new byte[width * height];
        for (int i = 0, p = 0; p < gray.Length; p++, i += bands)
        {
            gray[p] = bands == 1
                ? input[i]
                : (byte)Math.Clamp((int)Math.Round(input[i] * 0.2126 + input[i + 1] * 0.7152 + input[i + 2] * 0.0722), 0, 255);
        }
        return gray;
    }

    private static byte[] BoxBlur(byte[] input, int width, int height, int radius)
    {
        if (radius <= 0) return (byte[])input.Clone();
        var output = new byte[input.Length];
        int window = radius * 2 + 1;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int sum = 0;
                int count = 0;
                for (int ky = Math.Max(0, y - radius); ky <= Math.Min(height - 1, y + radius); ky++)
                {
                    for (int kx = Math.Max(0, x - radius); kx <= Math.Min(width - 1, x + radius); kx++)
                    {
                        sum += input[ky * width + kx];
                        count++;
                    }
                }
                output[y * width + x] = (byte)(sum / Math.Max(1, count));
            }
        }

        _ = window;
        return output;
    }

    private static int Sample(byte[] input, int width, int height, int x, int y)
    {
        x = Math.Clamp(x, 0, width - 1);
        y = Math.Clamp(y, 0, height - 1);
        return input[y * width + x];
    }
}
