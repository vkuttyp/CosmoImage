using System;
using System.Collections.Generic;

namespace CosmoImage.Operations.Misc;

/// <summary>
/// Decorator IVipsQuantizer that adds Floyd-Steinberg error-diffused
/// dithering on top of any palette-reducing inner quantizer. Mirrors
/// ImageSharp's <c>QuantizerOptions { Dither = ErrorDiffusion }</c>
/// pattern.
///
/// <para>Algorithm: run the inner quantizer once to derive the palette
/// (the set of unique output colours), then re-process the input with
/// per-pixel error diffusion — for each pixel, accumulate the residual
/// from previously-quantized neighbours, find the nearest palette
/// entry, and distribute the new residual to the right (7/16),
/// below-left (3/16), below (5/16), below-right (1/16). The classic
/// Floyd-Steinberg kernel from 1976.</para>
///
/// <para>Composes with any inner quantizer:
/// <see cref="MagickQuantizer"/>, <see cref="VipsOctreeQuantizer"/>,
/// <see cref="VipsPaletteQuantizer"/>. Inner quantizers that already
/// dither internally (Magick with Dither=true) get dithered TWICE —
/// for clean composition use the no-dither version of the inner.</para>
/// </summary>
public sealed class VipsFloydSteinbergQuantizer : IVipsQuantizer
{
    /// <summary>The wrapped quantizer whose palette we'll dither against.</summary>
    public IVipsQuantizer Inner { get; }

    public VipsFloydSteinbergQuantizer(IVipsQuantizer inner)
    {
        Inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public VipsImage Apply(VipsImage input)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (input.Bands != 3 && input.Bands != 4)
            throw new ArgumentException("Floyd-Steinberg requires 3 or 4 band input", nameof(input));
        if (input.BandFormat != VipsBandFormat.UChar)
            throw new ArgumentException("Floyd-Steinberg requires UChar input", nameof(input));

        // Step 1: get the palette by running the inner quantizer.
        var quantized = Inner.Apply(input);
        var palette = ExtractPalette(quantized);
        if (palette.Length == 0) return quantized;

        int w = input.Width, h = input.Height, b = input.Bands;
        bool hasAlpha = b == 4;

        // Step 2: materialise input pixels.
        byte[] inputPixels;
        if (input.Pixels is { } existing) inputPixels = existing;
        else
        {
            var sink = new MemorySink(input);
            sink.RunAsync().GetAwaiter().GetResult();
            inputPixels = sink.Pixels;
        }

        // Step 3: Floyd-Steinberg pass with two sliding error rows
        // (current + next). Errors are floats to avoid integer accumulation
        // bias on large palettes.
        var outBuf = new byte[w * h * b];
        var errCurr = new float[w * 3];
        var errNext = new float[w * 3];

        for (int y = 0; y < h; y++)
        {
            Array.Clear(errNext, 0, errNext.Length);
            for (int x = 0; x < w; x++)
            {
                int srcOff = (y * w + x) * b;
                float r = inputPixels[srcOff + 0] + errCurr[x * 3 + 0];
                float g = inputPixels[srcOff + 1] + errCurr[x * 3 + 1];
                float bl = inputPixels[srcOff + 2] + errCurr[x * 3 + 2];

                var (qR, qG, qB) = FindNearest(palette, ClampByte(r), ClampByte(g), ClampByte(bl));
                outBuf[srcOff + 0] = qR;
                outBuf[srcOff + 1] = qG;
                outBuf[srcOff + 2] = qB;
                if (hasAlpha) outBuf[srcOff + 3] = inputPixels[srcOff + 3];

                float eR = r - qR;
                float eG = g - qG;
                float eB = bl - qB;

                // Distribute Floyd-Steinberg coefficients (× 1/16):
                //         X   7
                //     3   5   1
                if (x + 1 < w)
                {
                    errCurr[(x + 1) * 3 + 0] += eR * 7f / 16f;
                    errCurr[(x + 1) * 3 + 1] += eG * 7f / 16f;
                    errCurr[(x + 1) * 3 + 2] += eB * 7f / 16f;
                }
                if (y + 1 < h)
                {
                    if (x - 1 >= 0)
                    {
                        errNext[(x - 1) * 3 + 0] += eR * 3f / 16f;
                        errNext[(x - 1) * 3 + 1] += eG * 3f / 16f;
                        errNext[(x - 1) * 3 + 2] += eB * 3f / 16f;
                    }
                    errNext[x * 3 + 0] += eR * 5f / 16f;
                    errNext[x * 3 + 1] += eG * 5f / 16f;
                    errNext[x * 3 + 2] += eB * 5f / 16f;
                    if (x + 1 < w)
                    {
                        errNext[(x + 1) * 3 + 0] += eR * 1f / 16f;
                        errNext[(x + 1) * 3 + 1] += eG * 1f / 16f;
                        errNext[(x + 1) * 3 + 2] += eB * 1f / 16f;
                    }
                }
            }
            // Roll the buffers: this row's "next" becomes next row's "current".
            (errCurr, errNext) = (errNext, errCurr);
        }

        var output = new VipsImage
        {
            Width = w, Height = h, Bands = b,
            BandFormat = VipsBandFormat.UChar,
            Interpretation = input.Interpretation,
            Coding = input.Coding, XRes = input.XRes, YRes = input.YRes,
            PixelsLazy = new Lazy<byte[]>(() => outBuf),
        };
        output.CopyMetadataFrom(input);
        output.SetPipeline(VipsDemandStyle.Any, input);
        return output;
    }

    /// <summary>Extract unique RGB triples from a quantized image; returns flat byte[N*3].</summary>
    private static byte[] ExtractPalette(VipsImage img)
    {
        var seen = new HashSet<int>();
        var entries = new List<byte>();
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, img.Width, img.Height));
        for (int y = 0; y < img.Height; y++)
        {
            var addr = reg.GetAddress(0, y);
            for (int x = 0; x < img.Width; x++)
            {
                int idx = x * img.Bands;
                byte r = addr[idx + 0], g = addr[idx + 1], b = addr[idx + 2];
                int key = (r << 16) | (g << 8) | b;
                if (seen.Add(key))
                {
                    entries.Add(r); entries.Add(g); entries.Add(b);
                }
            }
        }
        return entries.ToArray();
    }

    private static (byte r, byte g, byte b) FindNearest(byte[] palette, byte r, byte g, byte b)
    {
        int bestIdx = 0, bestDistSq = int.MaxValue;
        int n = palette.Length / 3;
        for (int i = 0; i < n; i++)
        {
            int dr = r - palette[i * 3 + 0];
            int dg = g - palette[i * 3 + 1];
            int db = b - palette[i * 3 + 2];
            int d = dr * dr + dg * dg + db * db;
            if (d < bestDistSq) { bestDistSq = d; bestIdx = i; if (d == 0) break; }
        }
        return (palette[bestIdx * 3 + 0], palette[bestIdx * 3 + 1], palette[bestIdx * 3 + 2]);
    }

    private static byte ClampByte(float v)
        => v <= 0f ? (byte)0 : v >= 255f ? (byte)255 : (byte)v;
}
