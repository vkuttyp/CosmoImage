using System;
using ImageMagick;

namespace CosmoImage.Core;

/// <summary>
/// Shared plumbing for ops that delegate to Magick.NET for the actual pixel
/// work (effects, color conversions, etc.). Materializes the source image,
/// pushes pixels into a <see cref="MagickImage"/> in raw RGB/RGBA/Gray
/// format, runs the caller's effect lambda, and extracts the result back to
/// a byte buffer. Keeps the boilerplate in one place across all the
/// Magick-backed effect ops.
/// </summary>
internal static class VipsMagickHelper
{
    /// <summary>
    /// Apply <paramref name="effect"/> to <paramref name="source"/>'s pixels
    /// via Magick. Returns a fresh byte buffer in the same dimensions and
    /// band layout as the input. Throws for unsupported band counts.
    /// </summary>
    public static byte[] ApplyEffect(VipsImage source, Action<MagickImage> effect)
    {
        int width = source.Width;
        int height = source.Height;
        int bands = source.Bands;
        if (bands != 1 && bands != 3 && bands != 4)
            throw new NotSupportedException($"Magick effects need 1, 3, or 4 bands; got {bands}");

        // Get source pixels — use the memory-image fast path when we can.
        byte[] inputPixels;
        if (source.Pixels is { } existing)
        {
            inputPixels = existing;
        }
        else
        {
            var sink = new MemorySink(source);
            sink.RunAsync().GetAwaiter().GetResult();
            inputPixels = sink.Pixels;
        }

        var rawFormat = bands switch
        {
            1 => MagickFormat.Gray,
            3 => MagickFormat.Rgb,
            4 => MagickFormat.Rgba,
            _ => throw new InvalidOperationException()
        };

        var settings = new MagickReadSettings
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = rawFormat,
            Depth = 8,
        };

        using var img = new MagickImage();
        img.Read(inputPixels, settings);
        effect(img);

        int stride = width * bands;
        var outBuf = new byte[stride * height];
        using var pixels = img.GetPixels();
        for (int y = 0; y < height; y++)
        {
            var row = pixels.GetArea(0, y, (uint)width, 1)
                ?? throw new InvalidOperationException($"Magick effect: pixel row {y} returned null");
            Array.Copy(row, 0, outBuf, y * stride, stride);
        }
        return outBuf;
    }

    /// <summary>
    /// Build a memory-backed VipsImage that mirrors <paramref name="source"/>'s
    /// header (dims, bands, format, metadata) and lazy-materializes pixels via
    /// <paramref name="producer"/>. Used by Magick-effect ops where the output
    /// dimensions match the input.
    /// </summary>
    public static VipsImage NewLikeWithLazyPixels(VipsImage source, Func<byte[]> producer)
    {
        var img = new VipsImage
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
        img.CopyMetadataFrom(source);
        img.SetPipeline(VipsDemandStyle.Any, source);
        return img;
    }
}
