using System;
using System.Runtime.CompilerServices;
using ImageMagick;

namespace CosmoImage.Operations.Effects;

/// <summary>
/// Add a Polaroid-style white border and rotate the image. Output dimensions
/// expand to fit the rotated, framed result, and an alpha channel is added
/// for the transparent corners around the photo. Eager-applied during
/// <see cref="Build"/> because Magick computes the post-rotation bounding
/// box internally — we need it to size the output VipsImage. Wraps
/// Magick.NET's <c>Polaroid()</c>.
/// </summary>
public class VipsPolaroid : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }

    /// <summary>Rotation in degrees. Negative tilts left, positive right.</summary>
    public double Angle { get; set; } = -5.0;

    public override int Build()
    {
        if (In == null) return -1;
        int srcBands = In.Bands;
        if (srcBands != 1 && srcBands != 3 && srcBands != 4) return -1;

        // Materialize input.
        byte[] inputPixels;
        if (In.Pixels is { } existing)
        {
            inputPixels = existing;
        }
        else
        {
            var sink = new MemorySink(In);
            sink.RunAsync().GetAwaiter().GetResult();
            inputPixels = sink.Pixels;
        }

        var rawFormat = srcBands switch
        {
            1 => MagickFormat.Gray,
            3 => MagickFormat.Rgb,
            4 => MagickFormat.Rgba,
            _ => throw new InvalidOperationException()
        };

        var settings = new MagickReadSettings
        {
            Width = (uint)In.Width,
            Height = (uint)In.Height,
            Format = rawFormat,
            Depth = 8,
        };

        using var img = new MagickImage();
        img.Read(inputPixels, settings);

        // Magick mutates the image: adds white frame, rotates by Angle, fills
        // corners with the background color. Empty caption keeps it photo-only.
        img.Polaroid("", Angle, PixelInterpolateMethod.Bilinear);

        // Polaroid output always has alpha (transparent corners around the
        // rotated rect). Force RGBA so we have predictable band layout.
        if (!img.HasAlpha) img.Alpha(AlphaOption.On);

        int outW = (int)img.Width;
        int outH = (int)img.Height;
        const int outBands = 4;
        int stride = outW * outBands;
        var outBuf = new byte[stride * outH];

        using (var pixels = img.GetPixels())
        {
            for (int y = 0; y < outH; y++)
            {
                var row = pixels.GetArea(0, y, (uint)outW, 1)
                    ?? throw new InvalidOperationException($"Polaroid: pixel row {y} returned null");
                Array.Copy(row, 0, outBuf, y * stride, stride);
            }
        }

        Out = new VipsImage
        {
            Width = outW,
            Height = outH,
            Bands = outBands,
            BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            Coding = VipsCoding.None,
            XRes = In.XRes,
            YRes = In.YRes,
            // Pre-computed; PixelsLazy just returns the buffer.
            PixelsLazy = new Lazy<byte[]>(() => outBuf),
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey() => HashCode.Combine("Polaroid", RuntimeHelpers.GetHashCode(In), Angle);
}
