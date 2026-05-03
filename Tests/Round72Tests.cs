using System;
using System.Linq;
using CosmoImage.Operations.Drawing;
using SixLabors.Fonts;
using Xunit;

namespace CosmoImage.Tests;

public class Round72Tests
{
    private static VipsImage RgbSolid(int w, int h, byte r, byte g, byte b)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 3, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? aa, object? bb, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        addr[x * 3 + 0] = r;
                        addr[x * 3 + 1] = g;
                        addr[x * 3 + 2] = b;
                    }
                }
                return 0;
            }
        };

    private static byte[] ReadPel(VipsImage img, int x, int y)
    {
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, img.Width, img.Height));
        return reg.GetAddress(x, y).Slice(0, img.Bands).ToArray();
    }

    /// <summary>First system font name that's reliably available, or null if none.</summary>
    private static string? PickFont()
    {
        foreach (var name in new[] { "Helvetica", "Arial", "DejaVu Sans", "Liberation Sans" })
            if (SystemFonts.Collection.TryGet(name, out _)) return name;
        return SystemFonts.Collection.Families.FirstOrDefault().Name;
    }

    private static int CountPainted(VipsImage img, byte threshold = 100)
    {
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, img.Width, img.Height));
        int n = 0;
        for (int y = 0; y < img.Height; y++)
            for (int x = 0; x < img.Width; x++)
                if (reg.GetAddress(x, y)[0] >= threshold) n++;
        return n;
    }

    // ---- Path generation ----

    [Fact]
    public void TextToPath_NonEmptyText_ProducesPathSegments()
    {
        var font = PickFont();
        if (font == null) return; // no fonts available — skip
        var path = VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "Hi",
            FontFamily = font,
            FontSize = 24,
            X = 0, Y = 0,
        });
        Assert.NotEmpty(path.Segments);
        // At least one MoveTo (each glyph starts with a MoveTo for each contour).
        Assert.Contains(path.Segments, s => s.Kind == VipsPathSegmentKind.MoveTo);
    }

    [Fact]
    public void TextToPath_EmptyText_ProducesEmptyPath()
    {
        var font = PickFont();
        if (font == null) return;
        var path = VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "", FontFamily = font, FontSize = 24,
        });
        Assert.Empty(path.Segments);
    }

    [Fact]
    public void TextToPath_LargerSize_ProducesLargerExtents()
    {
        var font = PickFont();
        if (font == null) return;
        var small = VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "M", FontFamily = font, FontSize = 12,
        });
        var large = VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "M", FontFamily = font, FontSize = 48,
        });
        // Compare bounding-box widths (max X across all segment endpoints).
        double smallMaxX = small.Segments.Max(s => Math.Max(s.X1, Math.Max(s.X2, s.X3)));
        double largeMaxX = large.Segments.Max(s => Math.Max(s.X1, Math.Max(s.X2, s.X3)));
        Assert.True(largeMaxX > smallMaxX * 2,
            $"48pt should be >2× 12pt: small={smallMaxX:F1}, large={largeMaxX:F1}");
    }

    [Fact]
    public void TextToPath_ContainsCurves_FontGlyphsHaveBeziers()
    {
        var font = PickFont();
        if (font == null) return;
        // 'O' is a smooth round glyph — guaranteed to flatten to curve segments
        // (quadratic for TrueType, cubic for CFF).
        var path = VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "O", FontFamily = font, FontSize = 48,
        });
        bool hasCurves = path.Segments.Any(s =>
            s.Kind == VipsPathSegmentKind.CubicTo || s.Kind == VipsPathSegmentKind.QuadraticTo);
        Assert.True(hasCurves, "'O' should produce Bezier segments");
    }

    [Fact]
    public void TextToPath_Origin_TranslatesEntirePath()
    {
        var font = PickFont();
        if (font == null) return;
        var atZero = VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "M", FontFamily = font, FontSize = 24, X = 0, Y = 0,
        });
        var shifted = VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "M", FontFamily = font, FontSize = 24, X = 100, Y = 50,
        });
        // Same vertex count, different positions.
        Assert.Equal(atZero.Segments.Count, shifted.Segments.Count);
        // First MoveTo of each should differ by ~(100, 50).
        var z0 = atZero.Segments.First(s => s.Kind == VipsPathSegmentKind.MoveTo);
        var s0 = shifted.Segments.First(s => s.Kind == VipsPathSegmentKind.MoveTo);
        Assert.Equal(100, s0.X1 - z0.X1, 1);
        Assert.Equal(50, s0.Y1 - z0.Y1, 1);
    }

    // ---- Rendering ----

    [Fact]
    public void DrawText_PaintsSomePixels()
    {
        var font = PickFont();
        if (font == null) return;
        var bg = RgbSolid(200, 80, 0, 0, 0);
        var painted = VipsImageOps.DrawText(bg, new VipsTextOptions {
            Text = "Hello",
            FontFamily = font,
            FontSize = 32,
            Color = new byte[] { 255, 255, 255 },
            X = 5, Y = 5,
        });
        // Some pixels should be painted (text rendered).
        int cnt = CountPainted(painted);
        Assert.True(cnt > 50, $"expected painted text pixels, got {cnt}");
    }

    [Fact]
    public void DrawText_EmptyText_ReturnsCanvasUnchanged()
    {
        var font = PickFont();
        if (font == null) return;
        var bg = RgbSolid(100, 50, 50, 50, 50);
        var painted = VipsImageOps.DrawText(bg, new VipsTextOptions {
            Text = "", FontFamily = font, FontSize = 24,
            Color = new byte[] { 255, 255, 255 },
        });
        Assert.Equal(50, ReadPel(painted, 10, 10)[0]);
    }

    [Fact]
    public void DrawText_PositionShiftsPaintedRegion()
    {
        var font = PickFont();
        if (font == null) return;
        var bg = RgbSolid(200, 60, 0, 0, 0);
        var leftPos = VipsImageOps.DrawText(bg, new VipsTextOptions {
            Text = "X", FontFamily = font, FontSize = 32,
            Color = new byte[] { 255, 255, 255 }, X = 5, Y = 5,
        });
        var rightPos = VipsImageOps.DrawText(bg, new VipsTextOptions {
            Text = "X", FontFamily = font, FontSize = 32,
            Color = new byte[] { 255, 255, 255 }, X = 150, Y = 5,
        });
        // Left version: pixels in left half painted, right half not.
        int leftLeftCnt = 0, leftRightCnt = 0;
        using (var r = new VipsRegion(leftPos))
        {
            r.Prepare(new VipsRect(0, 0, 200, 60));
            for (int y = 0; y < 60; y++)
                for (int x = 0; x < 200; x++)
                    if (r.GetAddress(x, y)[0] >= 100)
                        if (x < 100) leftLeftCnt++; else leftRightCnt++;
        }
        Assert.True(leftLeftCnt > leftRightCnt,
            $"left-positioned 'X' should paint left side: L={leftLeftCnt}, R={leftRightCnt}");
        // Right version: opposite.
        int rL = 0, rR = 0;
        using (var r = new VipsRegion(rightPos))
        {
            r.Prepare(new VipsRect(0, 0, 200, 60));
            for (int y = 0; y < 60; y++)
                for (int x = 0; x < 200; x++)
                    if (r.GetAddress(x, y)[0] >= 100)
                        if (x < 100) rL++; else rR++;
        }
        Assert.True(rR > rL,
            $"right-positioned 'X' should paint right side: L={rL}, R={rR}");
    }

    [Fact]
    public void DrawText_KerningProducesPositionedGlyphs()
    {
        // "AVA" has tight kerning between the A-V pairs in Helvetica/Arial.
        // Two glyphs vs three should still differ in width predictably.
        var font = PickFont();
        if (font == null) return;
        var p1 = VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "A", FontFamily = font, FontSize = 32,
        });
        var p3 = VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "AVA", FontFamily = font, FontSize = 32,
        });
        double oneWidth = p1.Segments.Max(s => Math.Max(s.X1, Math.Max(s.X2, s.X3)));
        double threeWidth = p3.Segments.Max(s => Math.Max(s.X1, Math.Max(s.X2, s.X3)));
        // 3 chars wider than 1 char (with shaping).
        Assert.True(threeWidth > oneWidth,
            $"AVA wider than A: {threeWidth:F1} vs {oneWidth:F1}");
    }

    // ---- Composition with existing path ops ----

    [Fact]
    public void TextToPath_Translate_Works()
    {
        var font = PickFont();
        if (font == null) return;
        var path = VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "T", FontFamily = font, FontSize = 24, X = 0, Y = 0,
        });
        var moved = path.Translate(100, 50);
        // Same segment count.
        Assert.Equal(path.Segments.Count, moved.Segments.Count);
        // First MoveTo translated.
        var orig = path.Segments.First(s => s.Kind == VipsPathSegmentKind.MoveTo);
        var trn = moved.Segments.First(s => s.Kind == VipsPathSegmentKind.MoveTo);
        Assert.Equal(100, trn.X1 - orig.X1, 6);
        Assert.Equal(50, trn.Y1 - orig.Y1, 6);
    }

    [Fact]
    public void TextToPath_Outline_ProducesStrokedText()
    {
        var font = PickFont();
        if (font == null) return;
        var bg = RgbSolid(150, 60, 0, 0, 0);
        var textPath = VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "Hi", FontFamily = font, FontSize = 32, X = 5, Y = 5,
        });
        var outlined = textPath.Outline(2);
        var painted = VipsImageOps.FillPath(bg,
            outlined, new VipsSolidBrush(255, 255, 255), aa: false);
        // Outlined text fills less area than solid, but still > 0.
        int cnt = CountPainted(painted);
        Assert.True(cnt > 20, $"outlined text should produce visible strokes: {cnt}");
    }

    // ---- Validation ----

    [Fact]
    public void DrawText_MissingFontFile_Throws()
    {
        var bg = RgbSolid(50, 50, 0, 0, 0);
        Assert.ThrowsAny<Exception>(() => VipsImageOps.DrawText(bg, new VipsTextOptions {
            Text = "Hi", FontFile = "/nonexistent/font.ttf", FontSize = 12,
        }));
    }
}
