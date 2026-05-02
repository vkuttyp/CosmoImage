using System;
using System.Buffers.Binary;

namespace CosmoImage.Operations.Color;

/// <summary>
/// Add an opaque alpha channel to an image that doesn't have one.
/// 1-band → 2-band (grey + alpha); 3-band → 4-band (RGB + alpha).
/// Inputs that already have alpha (2-band or 4-band) pass through.
///
/// <para>Implementation: synthesize a constant-fill alpha plane and
/// `Bandjoin` it onto the input. Cheaper to express as a dedicated op
/// than to require callers to spell out the constant-image scaffolding
/// every time.</para>
///
/// <para>Mirrors libvips <c>vips_addalpha</c>.</para>
/// </summary>
public static class VipsAddAlpha
{
    /// <summary>
    /// Append an alpha channel of value <paramref name="alpha"/>
    /// (default = fully opaque). UChar inputs use 0..255; Float inputs
    /// use the given value directly (typically 1.0 for opaque).
    /// </summary>
    public static VipsImage Apply(VipsImage input, double alpha = 255.0)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        // Already has alpha — pass through.
        if (input.Bands == 2 || input.Bands == 4) return input;

        bool isFloat = input.BandFormat == VipsBandFormat.Float;
        int sampleBytes = isFloat ? 4 : 1;
        int W = input.Width, H = input.Height;

        // Build a memory-backed 1-band constant image. Cheap — single
        // byte/float per pixel; allocator path skipped because the buffer
        // outlives any single tile.
        var alphaPixels = new byte[W * H * sampleBytes];
        if (isFloat)
        {
            float a = (float)alpha;
            for (int i = 0; i < W * H; i++)
                BinaryPrimitives.WriteSingleLittleEndian(alphaPixels.AsSpan(i * 4, 4), a);
        }
        else
        {
            byte a = (byte)Math.Clamp(alpha, 0, 255);
            Array.Fill(alphaPixels, a);
        }

        var alphaImg = new VipsImage
        {
            Width = W, Height = H, Bands = 1,
            BandFormat = input.BandFormat,
            Interpretation = VipsInterpretation.BW,
            Coding = VipsCoding.None, XRes = 1.0, YRes = 1.0,
            PixelsLazy = new Lazy<byte[]>(() => alphaPixels),
        };

        return CosmoImage.Core.VipsImageOps.Bandjoin(input, alphaImg);
    }
}
