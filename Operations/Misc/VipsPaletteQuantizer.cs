using System;
using System.Collections.Generic;

namespace CosmoImage.Operations.Misc;

/// <summary>
/// Quantize each pixel to its nearest entry in a fixed user-supplied
/// palette. Mirrors ImageSharp's <c>PaletteQuantizer</c> /
/// <c>WernerPaletteQuantizer</c>.
///
/// <para>Distance metric is squared Euclidean over the colour
/// channels (alpha excluded — alpha is preserved unmodified). For
/// large palettes the lookup is O(palette · pixels); for the typical
/// 16-256 colour case this runs comfortably on multi-megapixel
/// images.</para>
///
/// <para>Use <see cref="WebSafe"/> for the classic 216-colour 6×6×6
/// RGB cube ("web-safe"); construct with your own palette for brand
/// matching, sprite sheet locking, NES / Game Boy emulation, etc.</para>
/// </summary>
public sealed class VipsPaletteQuantizer : IVipsQuantizer
{
    /// <summary>Per-entry RGB colours; each <c>byte[]</c> is 3 elements (R, G, B).</summary>
    public IReadOnlyList<byte[]> Palette { get; }

    public VipsPaletteQuantizer(IReadOnlyList<byte[]> palette)
    {
        if (palette == null) throw new ArgumentNullException(nameof(palette));
        if (palette.Count == 0) throw new ArgumentException("Palette must not be empty", nameof(palette));
        if (palette.Count > 256) throw new ArgumentException("Palette must be ≤ 256 entries", nameof(palette));
        for (int i = 0; i < palette.Count; i++)
        {
            if (palette[i] == null || palette[i].Length != 3)
                throw new ArgumentException($"Palette entry {i} must be a 3-byte RGB triple", nameof(palette));
        }
        Palette = palette;
    }

    /// <summary>
    /// 216-colour "web-safe" palette: the 6×6×6 RGB cube with channels
    /// at {0, 51, 102, 153, 204, 255}. Historically guaranteed to
    /// render identically across early Web browsers without dithering.
    /// </summary>
    public static VipsPaletteQuantizer WebSafe
    {
        get
        {
            var p = new List<byte[]>(216);
            byte[] levels = { 0, 51, 102, 153, 204, 255 };
            for (int r = 0; r < 6; r++)
                for (int g = 0; g < 6; g++)
                    for (int bb = 0; bb < 6; bb++)
                        p.Add(new[] { levels[r], levels[g], levels[bb] });
            return new VipsPaletteQuantizer(p);
        }
    }

    public VipsImage Apply(VipsImage input)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (input.Bands != 3 && input.Bands != 4)
            throw new ArgumentException("PaletteQuantizer requires 3 or 4 band input", nameof(input));
        if (input.BandFormat != VipsBandFormat.UChar)
            throw new ArgumentException("PaletteQuantizer requires UChar input", nameof(input));

        int w = input.Width, h = input.Height, b = input.Bands;
        bool hasAlpha = b == 4;

        byte[] inputPixels;
        if (input.Pixels is { } existing) inputPixels = existing;
        else
        {
            var sink = new MemorySink(input);
            sink.RunAsync().GetAwaiter().GetResult();
            inputPixels = sink.Pixels;
        }

        // Snapshot palette into a flat byte array for tighter inner-loop access.
        int paletteCount = Palette.Count;
        var pal = new byte[paletteCount * 3];
        for (int i = 0; i < paletteCount; i++)
        {
            pal[i * 3 + 0] = Palette[i][0];
            pal[i * 3 + 1] = Palette[i][1];
            pal[i * 3 + 2] = Palette[i][2];
        }

        var outBuf = new byte[w * h * b];
        for (int i = 0; i < w * h; i++)
        {
            int r = inputPixels[i * b + 0];
            int g = inputPixels[i * b + 1];
            int bl = inputPixels[i * b + 2];
            int bestIdx = 0;
            int bestDistSq = int.MaxValue;
            for (int p = 0; p < paletteCount; p++)
            {
                int dr = r - pal[p * 3 + 0];
                int dg = g - pal[p * 3 + 1];
                int db = bl - pal[p * 3 + 2];
                int d = dr * dr + dg * dg + db * db;
                if (d < bestDistSq) { bestDistSq = d; bestIdx = p; if (d == 0) break; }
            }
            outBuf[i * b + 0] = pal[bestIdx * 3 + 0];
            outBuf[i * b + 1] = pal[bestIdx * 3 + 1];
            outBuf[i * b + 2] = pal[bestIdx * 3 + 2];
            if (hasAlpha) outBuf[i * b + 3] = inputPixels[i * b + 3];
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
}
