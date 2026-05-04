using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using SixLabors.Fonts;
using SixLabors.Fonts.Tables.AdvancedTypographic;

namespace CosmoImage.Operations.Drawing;

/// <summary>Horizontal alignment within the wrap box.</summary>
public enum VipsTextHAlign
{
    Left = 0,
    Center = 1,
    Right = 2,
}

/// <summary>Where to break lines when text overflows the wrap width.</summary>
public enum VipsTextWordBreak
{
    /// <summary>Standard Unicode line-breaking (UAX #14).</summary>
    Standard = 0,
    /// <summary>Break between any two characters.</summary>
    BreakAll = 1,
    /// <summary>Disallow breaks within words.</summary>
    KeepAll = 2,
    /// <summary>Standard rules; otherwise break mid-word as a last resort.</summary>
    BreakWord = 3,
}

/// <summary>Inter-word / inter-character spacing strategy for justified text.</summary>
public enum VipsTextJustify
{
    None = 0,
    InterWord = 1,
    InterCharacter = 2,
}

/// <summary>
/// Text decorations — underline / strikeout / overline. Combined as
/// flags; matches CSS's <c>text-decoration</c> property.
/// </summary>
[Flags]
public enum VipsTextDecoration
{
    None = 0,
    Underline = 1,
    Strikeout = 2,
    Overline = 4,
}

/// <summary>
/// Text layout mode — horizontal vs vertical writing direction.
/// Matches CSS's <c>writing-mode</c> values.
/// </summary>
public enum VipsTextLayoutMode
{
    HorizontalTopBottom = 0,
    HorizontalBottomTop = 1,
    VerticalLeftRight = 2,
    VerticalRightLeft = 4,
    VerticalMixedLeftRight = 8,
    VerticalMixedRightLeft = 16,
}

/// <summary>
/// Reading direction for the shaping engine. <c>Auto</c> uses Unicode
/// BiDi rules to detect direction from the text itself.
/// </summary>
public enum VipsTextDirection
{
    LeftToRight = 0,
    RightToLeft = 1,
    Auto = 2,
}

/// <summary>
/// Layout + style options for shaped text rendering. Mirrors the
/// per-call shape of ImageSharp's <c>RichTextOptions</c> / Cairo's
/// <c>cairo_text_extents_t</c>.
/// </summary>
public sealed class VipsTextOptions
{
    /// <summary>Text to render. May contain <c>\n</c> for explicit line breaks.</summary>
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

    // ---- Round 73: multi-line layout ----

    /// <summary>
    /// Maximum line width in pixels before wrapping. <c>null</c> = no
    /// wrapping (text only breaks on explicit <c>\n</c>).
    /// </summary>
    public double? WrappingLength { get; init; }
    /// <summary>Line height multiplier. 1.0 = font's default; 2.0 = double-spaced.</summary>
    public double LineSpacing { get; init; } = 1.0;
    /// <summary>How wrapped lines align horizontally within the wrap box.</summary>
    public VipsTextHAlign HAlign { get; init; } = VipsTextHAlign.Left;
    /// <summary>Word-breaking rule for line wrapping.</summary>
    public VipsTextWordBreak WordBreak { get; init; } = VipsTextWordBreak.Standard;
    /// <summary>Justification — distribute extra space across each line to fill the wrap width.</summary>
    public VipsTextJustify Justify { get; init; } = VipsTextJustify.None;

    // ---- Round 75: decorations + writing mode ----

    /// <summary>Combined text decorations (underline / strikeout / overline).</summary>
    public VipsTextDecoration Decorations { get; init; } = VipsTextDecoration.None;

    /// <summary>Writing mode — horizontal (default) or vertical.</summary>
    public VipsTextLayoutMode LayoutMode { get; init; } = VipsTextLayoutMode.HorizontalTopBottom;

    /// <summary>Reading direction — LTR (default), RTL, or Auto (BiDi).</summary>
    public VipsTextDirection TextDirection { get; init; } = VipsTextDirection.LeftToRight;

    // ---- Round 77: OpenType features ----

    /// <summary>
    /// Additional OpenType feature tags to enable, each a 4-char
    /// string (e.g., <c>"smcp"</c> for small caps, <c>"ss01"</c> for
    /// stylistic set 1, <c>"frac"</c> for fractions, <c>"dlig"</c>
    /// for discretionary ligatures). The font must include the
    /// corresponding GSUB / GPOS tables for the feature to take
    /// effect — most system fonts only ship a small subset.
    /// Default features (<c>kern</c>, <c>liga</c>, etc.) remain on
    /// regardless.
    /// </summary>
    public IReadOnlyList<string>? OpenTypeFeatures { get; init; }
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
        var renderer = new VipsGlyphRenderer((TextDecorations)opts.Decorations);
        // Note: SL.Fonts' HorizontalAlignment enum order differs from
        // the natural Left/Center/Right — explicit map.
        var hAlign = opts.HAlign switch
        {
            VipsTextHAlign.Left => HorizontalAlignment.Left,
            VipsTextHAlign.Center => HorizontalAlignment.Center,
            VipsTextHAlign.Right => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Left,
        };
        // SL.Fonts' HorizontalAlignment is anchor-style (positions text
        // relative to Origin). Users with a wrap box typically expect
        // "align within box" — shift the effective origin so opts.X
        // always means the wrap box's LEFT edge regardless of HAlign.
        double effectiveX = opts.X;
        if (opts.WrappingLength is double wrap)
        {
            effectiveX += opts.HAlign switch
            {
                VipsTextHAlign.Center => wrap / 2,
                VipsTextHAlign.Right => wrap,
                _ => 0,
            };
        }
        var textOptions = new TextOptions(font)
        {
            Origin = new Vector2((float)effectiveX, (float)opts.Y),
            LineSpacing = (float)opts.LineSpacing,
            HorizontalAlignment = hAlign,
            WordBreaking = (WordBreaking)opts.WordBreak,
            TextJustification = (TextJustification)opts.Justify,
            LayoutMode = (LayoutMode)opts.LayoutMode,
            TextDirection = (TextDirection)opts.TextDirection,
        };
        if (opts.WrappingLength is double w)
            textOptions.WrappingLength = (float)w;
        if (opts.OpenTypeFeatures != null && opts.OpenTypeFeatures.Count > 0)
        {
            var tags = new List<Tag>(opts.OpenTypeFeatures.Count);
            foreach (var s in opts.OpenTypeFeatures)
            {
                if (s == null || s.Length != 4)
                    throw new ArgumentException(
                        $"OpenType feature tags must be exactly 4 characters; got '{s}'",
                        nameof(opts));
                tags.Add(Tag.Parse(s));
            }
            textOptions.FeatureTags = tags;
        }
        TextRenderer.RenderTextTo(renderer, opts.Text, textOptions);
        return renderer.Path;
    }

    /// <summary>
    /// Lay shaped text along <paramref name="targetPath"/>. Glyph
    /// outlines are first shaped horizontally at the origin, then
    /// each polyline point is warped onto the target path: the
    /// glyph's x-coordinate becomes arc-length along the path, and
    /// its y-coordinate becomes perpendicular offset from the path
    /// tangent. Mirrors SVG's <c>&lt;textPath&gt;</c>.
    ///
    /// <para>The target path must be a single sub-path (open or closed).
    /// <paramref name="opts"/>.X and .Y are ignored — placement comes
    /// entirely from the target path. <paramref name="offset"/> shifts
    /// the entire text perpendicular to the path; positive moves it
    /// "outward" (below the path in screen y-down).</para>
    ///
    /// <para>Bezier curves in glyph outlines are flattened to polyline
    /// before warping so the result follows the target's curvature
    /// faithfully even on tightly curved paths.</para>
    /// </summary>
    public static VipsPath TextOnPath(VipsTextOptions opts, VipsPath targetPath, double offset = 0)
    {
        if (opts == null) throw new ArgumentNullException(nameof(opts));
        if (targetPath == null) throw new ArgumentNullException(nameof(targetPath));
        if (string.IsNullOrEmpty(opts.Text)) return new VipsPath();

        // Shape the text at origin (0, 0). opts.X / opts.Y are ignored.
        var horizontalText = TextToPath(new VipsTextOptions
        {
            Text = opts.Text,
            FontFamily = opts.FontFamily,
            FontFile = opts.FontFile,
            FontSize = opts.FontSize,
            Color = opts.Color,
            X = 0, Y = 0,
            WrappingLength = null,  // wrap doesn't make sense on a path
            LineSpacing = opts.LineSpacing,
            HAlign = VipsTextHAlign.Left,
            WordBreak = opts.WordBreak,
            Justify = opts.Justify,
        });

        // Convert to polylines (Beziers flattened) for accurate warp.
        var flat = horizontalText.Simplify(0);
        var sampler = new ArcLengthSampler(targetPath);

        var result = new VipsPath();
        double cx = 0, cy = 0; bool started = false;
        foreach (var seg in flat.Segments)
        {
            switch (seg.Kind)
            {
                case VipsPathSegmentKind.MoveTo:
                {
                    cx = seg.X1; cy = seg.Y1;
                    var (wx, wy) = sampler.MapPoint(cx, cy + offset);
                    result.MoveTo(wx, wy);
                    started = true;
                    break;
                }
                case VipsPathSegmentKind.LineTo:
                {
                    cx = seg.X1; cy = seg.Y1;
                    var (wx, wy) = sampler.MapPoint(cx, cy + offset);
                    result.LineTo(wx, wy);
                    break;
                }
                case VipsPathSegmentKind.Close:
                    if (started) result.Close();
                    started = false;
                    break;
            }
        }
        return result;
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
    private readonly TextDecorations _enabled;
    public VipsPath Path { get; } = new VipsPath();

    public VipsGlyphRenderer(TextDecorations enabled = TextDecorations.None) { _enabled = enabled; }

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

    public TextDecorations EnabledDecorations() => _enabled;

    /// <summary>
    /// Append the decoration line as a thin filled rectangle to the
    /// same path. Width is <paramref name="thickness"/>; the rectangle
    /// is built by offsetting <paramref name="s"/>..<paramref name="e"/>
    /// perpendicular to its direction by ±thickness/2.
    /// </summary>
    public void SetDecoration(TextDecorations d, Vector2 s, Vector2 e, float thickness)
    {
        if (thickness <= 0) return;
        Vector2 along = e - s;
        float len = along.Length();
        if (len < 1e-6f) return;
        Vector2 unit = along / len;
        // Right-hand perpendicular, scaled to half-thickness.
        Vector2 perp = new Vector2(-unit.Y, unit.X) * (thickness * 0.5f);
        Path.MoveTo(s.X + perp.X, s.Y + perp.Y);
        Path.LineTo(e.X + perp.X, e.Y + perp.Y);
        Path.LineTo(e.X - perp.X, e.Y - perp.Y);
        Path.LineTo(s.X - perp.X, s.Y - perp.Y);
        Path.Close();
    }
}

/// <summary>
/// Arc-length parameterisation of a <see cref="VipsPath"/> for
/// text-on-path warping. Flattens curves via <c>Simplify(0)</c>,
/// accumulates segment lengths, then maps a query
/// <c>(textX, perpY)</c> onto a world position by interpolating
/// position + tangent at arc-length <c>textX</c> and offsetting
/// <c>perpY</c> in the perpendicular direction.
/// </summary>
internal sealed class ArcLengthSampler
{
    private readonly List<(double s, double x, double y, double tx, double ty)> _points = new();
    private readonly double _totalLength;

    public ArcLengthSampler(VipsPath target)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        // Flatten target to polyline via Simplify(0); single sub-path only.
        var flat = target.Simplify(0);
        double s = 0;
        double lastX = 0, lastY = 0;
        bool firstMove = true;
        int subpathStarts = 0;
        foreach (var seg in flat.Segments)
        {
            switch (seg.Kind)
            {
                case VipsPathSegmentKind.MoveTo:
                    subpathStarts++;
                    if (subpathStarts > 1)
                        throw new ArgumentException(
                            "TextOnPath target must be a single sub-path", nameof(target));
                    lastX = seg.X1; lastY = seg.Y1;
                    _points.Add((s, lastX, lastY, 0, 0));
                    firstMove = false;
                    break;
                case VipsPathSegmentKind.LineTo:
                    {
                        double dx = seg.X1 - lastX, dy = seg.Y1 - lastY;
                        double len = Math.Sqrt(dx * dx + dy * dy);
                        s += len;
                        lastX = seg.X1; lastY = seg.Y1;
                        _points.Add((s, lastX, lastY, 0, 0));
                    }
                    break;
                case VipsPathSegmentKind.Close:
                    // Treat closing edge as part of the path for arc length.
                    if (_points.Count > 0)
                    {
                        var first = _points[0];
                        double dx = first.x - lastX, dy = first.y - lastY;
                        double len = Math.Sqrt(dx * dx + dy * dy);
                        if (len > 1e-9)
                        {
                            s += len;
                            _points.Add((s, first.x, first.y, 0, 0));
                            lastX = first.x; lastY = first.y;
                        }
                    }
                    break;
            }
        }
        _ = firstMove;
        _totalLength = s;

        // Compute per-vertex unit tangent as the average of incoming +
        // outgoing chord directions (gives a smooth tangent field).
        for (int i = 0; i < _points.Count; i++)
        {
            int prev = Math.Max(0, i - 1);
            int next = Math.Min(_points.Count - 1, i + 1);
            double dx = _points[next].x - _points[prev].x;
            double dy = _points[next].y - _points[prev].y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            double tx = len > 1e-9 ? dx / len : 1;
            double ty = len > 1e-9 ? dy / len : 0;
            var p = _points[i];
            _points[i] = (p.s, p.x, p.y, tx, ty);
        }
    }

    public double TotalLength => _totalLength;

    public (double x, double y) MapPoint(double textX, double perp)
    {
        if (_points.Count == 0) return (0, 0);
        // Clamp textX to the path's arc-length range — glyphs past
        // the end pile up at the path's terminus rather than wrapping.
        if (textX <= 0) textX = 0;
        else if (textX >= _totalLength) textX = _totalLength;

        // Binary search for the segment whose arc-length range
        // [points[lo].s, points[hi].s] contains textX.
        int lo = 0, hi = _points.Count - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (_points[mid].s <= textX) lo = mid; else hi = mid;
        }
        var p0 = _points[lo];
        var p1 = _points[hi];
        double dt = p1.s - p0.s;
        double t = dt > 1e-9 ? (textX - p0.s) / dt : 0;

        double x = p0.x + t * (p1.x - p0.x);
        double y = p0.y + t * (p1.y - p0.y);
        double tx = p0.tx + t * (p1.tx - p0.tx);
        double ty = p0.ty + t * (p1.ty - p0.ty);
        double tlen = Math.Sqrt(tx * tx + ty * ty);
        if (tlen > 1e-9) { tx /= tlen; ty /= tlen; }
        else { tx = 1; ty = 0; }
        // Perpendicular: rotate tangent 90° clockwise (right-hand
        // perpendicular). With y-down, positive perp points "below"
        // the path — matches the convention that descenders (y > 0
        // in font coords) hang below the baseline.
        double px = -ty, py = tx;
        return (x + perp * px, y + perp * py);
    }
}
