using System;
using System.IO;
using System.Runtime.CompilerServices;
using ImageMagick;

namespace CosmoImage.Operations.Misc;

/// <summary>
/// Reduce the image to at most <see cref="Colors"/> distinct colors.
/// Implementation routes through Magick.NET (Wu / median-cut quantizer with
/// optional Floyd-Steinberg dithering — same algorithms ImageSharp uses
/// internally). Output stays in the input's pixel format (RGB stays RGB)
/// but with reduced unique colors. Useful for visual effects, GIF frame
/// preparation, and palette-PNG export pipelines.
/// </summary>
public class VipsQuantize : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }

    /// <summary>Maximum number of unique colors in the output. Range 2..256.</summary>
    public int Colors { get; set; } = 256;

    /// <summary>Apply Floyd-Steinberg error diffusion — better visual quality
    /// at the cost of some noise. False for crisp banded output.</summary>
    public bool Dither { get; set; } = true;

    public override int Build()
    {
        if (In == null) return -1;
        if (Colors < 2 || Colors > 256) return -1;
        if (In.Bands != 1 && In.Bands != 3 && In.Bands != 4) return -1;

        var capturedIn = In;
        int colors = Colors;
        bool dither = Dither;

        Out = new VipsImage
        {
            Width = In.Width,
            Height = In.Height,
            Bands = In.Bands,
            BandFormat = In.BandFormat,
            Interpretation = In.Interpretation,
            Coding = In.Coding,
            XRes = In.XRes,
            YRes = In.YRes,
            // No GenerateFn — pure memory-backed via PixelsLazy. Each downstream
            // Prepare aliases this buffer with no per-tile work.
            PixelsLazy = new Lazy<byte[]>(() =>
            {
                int width = capturedIn.Width;
                int height = capturedIn.Height;
                int bands = capturedIn.Bands;

                // Materialize input pixels.
                byte[] inputPixels;
                if (capturedIn.Pixels is { } existing)
                {
                    inputPixels = existing;
                }
                else
                {
                    var sink = new MemorySink(capturedIn);
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

                img.Quantize(new QuantizeSettings
                {
                    Colors = (uint)colors,
                    DitherMethod = dither ? DitherMethod.FloydSteinberg : DitherMethod.No,
                });

                int stride = width * bands;
                var outBuf = new byte[stride * height];
                using var pixels = img.GetPixels();
                for (int y = 0; y < height; y++)
                {
                    var row = pixels.GetArea(0, y, (uint)width, 1)
                        ?? throw new InvalidOperationException($"Quantize: pixel row {y} returned null");
                    Array.Copy(row, 0, outBuf, y * stride, stride);
                }
                return outBuf;
            })
        };

        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Quantize", RuntimeHelpers.GetHashCode(In), Colors, Dither);
}
