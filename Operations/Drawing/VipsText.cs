using System;
using System.IO;
using System.Linq;
using System.Numerics;
using SixLabors.Fonts;

namespace CosmoImage.Operations.Drawing;

/// <summary>
/// Layout + style options for shaped text rendering. Mirrors the
/// per-call shape of ImageSharp's <c>RichTextOptions</c> / Cairo's
/// <c>cairo_text_extents_t</c>.
/// </summary>
public sealed class VipsTextOptions
{
    /// <summary>Text to render.</summary>
    public string Text { get; init; } = "";
    /// <summary>System font family name (e.g., "Helvetica"). Ignored if <see cref="FontFile"/> set.</summary>
    public string FontFamily { get; init; } = "";
    /// <summary>Optional explicit font file path (.ttf / .otf). Takes priority over <see cref="FontFamily"/>.</summary>
    public string? FontFile { get; init; }
    /// <summary>Em size in pixels.</summary>
    public double FontSize { get; init; } = 12;
    /// <summary>Per-band fill colour. Defaults to black (1 band).</summary>
    public byte[] Color { get; init; } = new byte[] { 0 };
    /// <summary>Layout origin x — left edge of the text bounding box in destination coords.</summary>
    public double X { get; init; }
    /// <summary>Layout origin y — top edge of the text bounding box in destination coords.</summary>
    public double Y { get; init; }
}

/// <summary>
/// Shaped text rendering via <c>SixLabors.Fonts</c>. Pipeline:
/// load font → shape glyphs (kerning + ligatures + basic OpenType
/// features handled by SixLabors) → emit each glyph's outline as
/// <see cref="VipsPath"/> segments → fill with the existing scanline
/// rasteriser.
///
/// <para>Gives proper per-glyph positioning (not just monospace
/// stamping) — a substantial step up from the legacy
/// <c>VipsImageOps.Text</c> which delegated to Magick.NET's
/// <c>label:</c> reader.</para>
///
/// <para>Multi-line layout, alignment, text-on-path, and advanced
/// OpenType features (stylistic sets, alt forms) are deferred to
/// later rounds.</para>
/// </summary>
public static class VipsTextOps
{
    /// <summary>
    /// Draw shaped text onto <paramref name="canvas"/>. Equivalent to
    /// <c>FillPath(canvas, TextToPath(opts), SolidBrush(opts.Color))</c>.
    /// </summary>
    public static VipsImage DrawText(VipsImage canvas, VipsTextOptions opts, bool aa = true)
    {
        if (canvas == null) throw new ArgumentNullException(nameof(canvas));
        if (opts == null) throw new ArgumentNullException(nameof(opts));
        var path = TextToPath(opts);
        if (path.Segments.Count == 0) return canvas;
        var brush = new VipsSolidBrush(opts.Color);
        return VipsImageOps.FillPath(canvas, path, brush, aa);
    }

    /// <summary>
    /// Shape <paramref name="opts"/>'s text into a fillable
    /// <see cref="VipsPath"/>. Useful when you want to combine the
    /// glyph outline with another path operation (boolean, transform,
    /// outline expansion) before rasterising.
    /// </summary>
    public static VipsPath TextToPath(VipsTextOptions opts)
    {
        if (opts == null) throw new ArgumentNullException(nameof(opts));
        if (string.IsNullOrEmpty(opts.Text)) return new VipsPath();
        var font = LoadFont(opts);
        var renderer = new VipsGlyphRenderer();
        var textOptions = new TextOptions(font)
        {
            Origin = new Vector2((float)opts.X, (float)opts.Y),
        };
        TextRenderer.RenderTextTo(renderer, opts.Text, textOptions);
        return renderer.Path;
    }

    private static Font LoadFont(VipsTextOptions opts)
    {
        if (!string.IsNullOrEmpty(opts.FontFile))
        {
            if (!File.Exists(opts.FontFile))
                throw new FileNotFoundException("Font file not found", opts.FontFile);
            var coll = new FontCollection();
            var family = coll.Add(opts.FontFile);
            return family.CreateFont((float)opts.FontSize);
        }
        if (!string.IsNullOrEmpty(opts.FontFamily))
            return SystemFonts.CreateFont(opts.FontFamily, (float)opts.FontSize);
        // Fallback: first available system font.
        var first = SystemFonts.Collection.Families.FirstOrDefault();
        if (first.Name == null)
            throw new InvalidOperationException(
                "No system fonts available — set VipsTextOptions.FontFamily or .FontFile");
        return first.CreateFont((float)opts.FontSize);
    }
}

/// <summary>
/// Glyph-outline renderer that translates SixLabors.Fonts'
/// <see cref="IGlyphRenderer"/> callbacks into <see cref="VipsPath"/>
/// segments. Each glyph's contour becomes a closed sub-path; compound
/// glyphs (e.g., 'O', 'g') emit multiple closed sub-paths whose holes
/// are resolved by the rasteriser's even-odd fill rule.
/// </summary>
internal sealed class VipsGlyphRenderer : IGlyphRenderer
{
    public VipsPath Path { get; } = new VipsPath();

    public void BeginText(in FontRectangle bounds) { }
    public void EndText() { }
    public bool BeginGlyph(in FontRectangle bounds, in GlyphRendererParameters parameters) => true;
    public void EndGlyph() { }

    public void BeginFigure() { }
    public void EndFigure() => Path.Close();

    public void MoveTo(Vector2 point) => Path.MoveTo(point.X, point.Y);
    public void LineTo(Vector2 point) => Path.LineTo(point.X, point.Y);
    public void QuadraticBezierTo(Vector2 c, Vector2 point)
        => Path.QuadraticTo(c.X, c.Y, point.X, point.Y);
    public void CubicBezierTo(Vector2 c1, Vector2 c2, Vector2 point)
        => Path.CubicTo(c1.X, c1.Y, c2.X, c2.Y, point.X, point.Y);

    public TextDecorations EnabledDecorations() => TextDecorations.None;
    public void SetDecoration(TextDecorations d, Vector2 s, Vector2 e, float thickness) { }
}
