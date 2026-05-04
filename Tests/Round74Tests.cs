using System;
using System.Linq;
using CosmoImage.Operations.Drawing;
using SixLabors.Fonts;
using Xunit;

namespace CosmoImage.Tests;

public class Round74Tests
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

    private static string? PickFont()
    {
        foreach (var name in new[] { "Helvetica", "Arial", "DejaVu Sans", "Liberation Sans" })
            if (SystemFonts.Collection.TryGet(name, out _)) return name;
        return SystemFonts.Collection.Families.FirstOrDefault().Name;
    }

    private static (double minX, double maxX, double minY, double maxY) Bounds(VipsPath p)
    {
        double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
        double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
        foreach (var s in p.Segments)
        {
            void U(double x, double y) {
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
            }
            switch (s.Kind)
            {
                case VipsPathSegmentKind.MoveTo:
                case VipsPathSegmentKind.LineTo:
                    U(s.X1, s.Y1); break;
                case VipsPathSegmentKind.QuadraticTo:
                    U(s.X1, s.Y1); U(s.X2, s.Y2); break;
                case VipsPathSegmentKind.CubicTo:
                    U(s.X1, s.Y1); U(s.X2, s.Y2); U(s.X3, s.Y3); break;
            }
        }
        return (minX, maxX, minY, maxY);
    }

    // ---- Identity / sanity ----

    [Fact]
    public void TextOnPath_HorizontalLine_MatchesShapeAtBaseline()
    {
        var font = PickFont();
        if (font == null) return;
        // A long horizontal target so glyphs don't hit the end-clamp.
        var target = new VipsPath().MoveTo(0, 50).LineTo(1000, 50);
        var direct = VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "Hi", FontFamily = font, FontSize = 24, X = 0, Y = 50,
        });
        var onPath = VipsImageOps.TextOnPath(new VipsTextOptions {
            Text = "Hi", FontFamily = font, FontSize = 24,
        }, target);
        var (_, dMaxX, dMinY, _) = Bounds(direct);
        var (_, oMaxX, oMinY, _) = Bounds(onPath);
        // X-extent matches within a couple pixels (shaping vs polyline
        // flattening can introduce tiny jitter at glyph boundaries).
        Assert.True(Math.Abs(oMaxX - dMaxX) < 3,
            $"max X mismatch: direct={dMaxX:F1}, onPath={oMaxX:F1}");
        Assert.True(Math.Abs(oMinY - dMinY) < 3,
            $"min Y mismatch: direct={dMinY:F1}, onPath={oMinY:F1}");
    }

    // ---- Direction / rotation ----

    [Fact]
    public void TextOnPath_VerticalLine_RotatesTextNinetyDegrees()
    {
        var font = PickFont();
        if (font == null) return;
        // Vertical target. With tangent (0, 1) and right-hand perp (-1, 0),
        // a horizontal "Hi" warps so its x-axis runs down the path.
        var target = new VipsPath().MoveTo(50, 0).LineTo(50, 500);
        var path = VipsImageOps.TextOnPath(new VipsTextOptions {
            Text = "Hi", FontFamily = font, FontSize = 24,
        }, target);
        var (minX, maxX, minY, maxY) = Bounds(path);
        // After 90° rotation, vertical extent (along path) should
        // dominate horizontal extent (perpendicular to path).
        Assert.True(maxY - minY > maxX - minX,
            $"rotated text should be taller than wide: x={maxX - minX:F1}, y={maxY - minY:F1}");
    }

    [Fact]
    public void TextOnPath_DiagonalLine_TextRotates()
    {
        var font = PickFont();
        if (font == null) return;
        // 45° diagonal target.
        var target = new VipsPath().MoveTo(0, 0).LineTo(500, 500);
        var path = VipsImageOps.TextOnPath(new VipsTextOptions {
            Text = "AB", FontFamily = font, FontSize = 20,
        }, target);
        var (minX, maxX, minY, maxY) = Bounds(path);
        // Horizontal text would have small Y span; rotated to 45° gives
        // comparable X and Y spans.
        double xSpan = maxX - minX, ySpan = maxY - minY;
        Assert.True(ySpan > 5, $"rotated text should have meaningful vertical extent, got {ySpan:F1}");
        Assert.True(xSpan > 5, $"rotated text should have meaningful horizontal extent, got {xSpan:F1}");
    }

    // ---- Offset ----

    [Fact]
    public void TextOnPath_PositiveOffset_ShiftsBelowPath()
    {
        var font = PickFont();
        if (font == null) return;
        var target = new VipsPath().MoveTo(0, 50).LineTo(500, 50);
        var atPath = VipsImageOps.TextOnPath(new VipsTextOptions {
            Text = "X", FontFamily = font, FontSize = 16,
        }, target, offset: 0);
        var below = VipsImageOps.TextOnPath(new VipsTextOptions {
            Text = "X", FontFamily = font, FontSize = 16,
        }, target, offset: 20);
        var (_, _, aMinY, aMaxY) = Bounds(atPath);
        var (_, _, bMinY, bMaxY) = Bounds(below);
        // Offset 20 should push everything down by ~20 pixels.
        Assert.True(bMinY > aMinY + 15,
            $"positive offset should move text down: at={aMinY:F1}, below={bMinY:F1}");
    }

    [Fact]
    public void TextOnPath_NegativeOffset_ShiftsAbovePath()
    {
        var font = PickFont();
        if (font == null) return;
        var target = new VipsPath().MoveTo(0, 100).LineTo(500, 100);
        var atPath = VipsImageOps.TextOnPath(new VipsTextOptions {
            Text = "X", FontFamily = font, FontSize = 16,
        }, target, offset: 0);
        var above = VipsImageOps.TextOnPath(new VipsTextOptions {
            Text = "X", FontFamily = font, FontSize = 16,
        }, target, offset: -20);
        var (_, _, aMinY, _) = Bounds(atPath);
        var (_, _, vMinY, _) = Bounds(above);
        Assert.True(vMinY < aMinY - 15,
            $"negative offset should move text up: at={aMinY:F1}, above={vMinY:F1}");
    }

    // ---- Curved path ----

    [Fact]
    public void TextOnPath_CurvedPath_FollowsCurvature()
    {
        var font = PickFont();
        if (font == null) return;
        // Quarter-circle arc as the target. Glyphs should curve along it.
        var target = new VipsPath()
            .MoveTo(50, 100)
            .ArcTo(50, 50, 0, false, true, 100, 50);
        var path = VipsImageOps.TextOnPath(new VipsTextOptions {
            Text = "ABC", FontFamily = font, FontSize = 12,
        }, target);
        var (minX, maxX, minY, maxY) = Bounds(path);
        // Text should occupy a 2D region (curving), not a thin horizontal strip.
        double xSpan = maxX - minX, ySpan = maxY - minY;
        Assert.True(xSpan > 10 && ySpan > 10,
            $"curved-text should span both axes: x={xSpan:F1}, y={ySpan:F1}");
    }

    // ---- Rendered ----

    [Fact]
    public void DrawTextOnPath_PaintsAlongPath()
    {
        var font = PickFont();
        if (font == null) return;
        var bg = RgbSolid(200, 80, 0, 0, 0);
        // Diagonal target sized so the text fully traverses it.
        var target = new VipsPath().MoveTo(10, 65).LineTo(190, 15);
        var textPath = VipsImageOps.TextOnPath(new VipsTextOptions {
            Text = "Hello there world", FontFamily = font, FontSize = 14,
        }, target);
        var painted = VipsImageOps.FillPath(bg, textPath,
            new VipsSolidBrush(255, 255, 255), aa: false);
        // Painted pixels should distribute across many y-rows (proving
        // the text follows the diagonal, not just sits horizontally).
        using var reg = new VipsRegion(painted);
        reg.Prepare(new VipsRect(0, 0, 200, 80));
        var paintedRows = new System.Collections.Generic.HashSet<int>();
        int totalPainted = 0;
        for (int y = 0; y < 80; y++)
            for (int x = 0; x < 200; x++)
                if (reg.GetAddress(x, y)[0] >= 100)
                {
                    paintedRows.Add(y);
                    totalPainted++;
                }
        Assert.True(totalPainted > 50, $"expected painted text, got {totalPainted}");
        // For a 50-px tall diagonal, painted text should span > 20 rows.
        Assert.True(paintedRows.Count > 20,
            $"diagonal text should span many rows, got {paintedRows.Count}");
    }

    // ---- Validation ----

    [Fact]
    public void TextOnPath_EmptyText_ReturnsEmptyPath()
    {
        var font = PickFont();
        if (font == null) return;
        var target = new VipsPath().MoveTo(0, 0).LineTo(100, 0);
        var result = VipsImageOps.TextOnPath(new VipsTextOptions {
            Text = "", FontFamily = font, FontSize = 12,
        }, target);
        Assert.Empty(result.Segments);
    }

    [Fact]
    public void TextOnPath_MultiSubpathTarget_Throws()
    {
        var font = PickFont();
        if (font == null) return;
        var multi = new VipsPath()
            .MoveTo(0, 0).LineTo(50, 0).Close()
            .MoveTo(60, 0).LineTo(110, 0).Close();
        Assert.Throws<ArgumentException>(() => VipsImageOps.TextOnPath(new VipsTextOptions {
            Text = "Hi", FontFamily = font, FontSize = 12,
        }, multi));
    }
}
