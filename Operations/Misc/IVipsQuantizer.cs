using System;
using System.IO;
using ImageMagick;

namespace CosmoImage.Operations.Misc;

/// <summary>
/// Quantizer plugin — reduces an image's palette to a target number
/// of colors. Mirrors ImageSharp's <c>IQuantizer</c>.
///
/// <para>Implementations expose their tuning knobs (color count,
/// dithering, etc.) as their own configuration; the interface itself
/// only requires <see cref="Apply"/>. Users can plug custom
/// quantizers (Octree, Wu, Werner palette, custom voronoi-region
/// algorithms) into pipelines that previously hardcoded the
/// <see cref="MagickQuantizer"/> default.</para>
/// </summary>
public interface IVipsQuantizer
{
    /// <summary>
    /// Reduce <paramref name="input"/>'s palette. Output has the same
    /// dimensions and band count as input; only the unique-color
    /// count drops.
    /// </summary>
    VipsImage Apply(VipsImage input);
}

/// <summary>
/// Default quantizer — routes through Magick.NET's Wu / median-cut
/// implementation with optional Floyd-Steinberg dithering. Same
/// algorithm <see cref="VipsQuantize"/> uses internally.
/// </summary>
public sealed class MagickQuantizer : IVipsQuantizer
{
    /// <summary>Maximum number of unique colors in the output. Range 2..256.</summary>
    public int Colors { get; init; } = 256;

    /// <summary>Apply Floyd-Steinberg error diffusion. False for crisp banded output.</summary>
    public bool Dither { get; init; } = true;

    public VipsImage Apply(VipsImage input)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (Colors < 2 || Colors > 256)
            throw new ArgumentOutOfRangeException(nameof(Colors), "Colors must be in 2..256");
        if (input.Bands != 1 && input.Bands != 3 && input.Bands != 4)
            throw new ArgumentException("MagickQuantizer requires 1, 3, or 4 band input", nameof(input));

        int width = input.Width, height = input.Height, bands = input.Bands;

        // Materialise input pixels.
        byte[] inputPixels;
        if (input.Pixels is { } existing) inputPixels = existing;
        else
        {
            var sink = new MemorySink(input);
            sink.RunAsync().GetAwaiter().GetResult();
            inputPixels = sink.Pixels;
        }

        var rawFormat = bands switch
        {
            1 => MagickFormat.Gray,
            3 => MagickFormat.Rgb,
            _ => MagickFormat.Rgba,
        };

        var settings = new MagickReadSettings
        {
            Width = (uint)width, Height = (uint)height,
            Format = rawFormat, Depth = 8,
        };

        using var img = new MagickImage();
        img.Read(inputPixels, settings);
        img.Quantize(new QuantizeSettings
        {
            Colors = (uint)Colors,
            DitherMethod = Dither ? DitherMethod.FloydSteinberg : DitherMethod.No,
        });

        // Quantize switches the image to palette-indexed storage, which
        // makes GetPixels.GetArea return palette indices instead of
        // RGB(A) triples. Round-trip through raw output to coerce back
        // to direct-color bytes of the right shape.
        img.Settings.Format = rawFormat;
        img.Settings.Depth = 8;
        var outBuf = img.ToByteArray(rawFormat);

        var output = new VipsImage
        {
            Width = width, Height = height, Bands = bands,
            BandFormat = input.BandFormat, Interpretation = input.Interpretation,
            Coding = input.Coding, XRes = input.XRes, YRes = input.YRes,
            PixelsLazy = new Lazy<byte[]>(() => outBuf),
        };
        output.CopyMetadataFrom(input);
        output.SetPipeline(VipsDemandStyle.Any, input);
        return output;
    }
}
