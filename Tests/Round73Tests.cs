using System.Linq;
using CosmoImage.Operations.Drawing;
using CosmoFonts.Loader;
using Xunit;

namespace CosmoImage.Tests;

public class Round73Tests
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

    private static readonly SystemFontCollection s_fonts = SystemFontCollection.LoadDefault();

    private static string? PickFont()
    {
        foreach (var name in new[] { "Helvetica", "Arial", "DejaVu Sans", "Liberation Sans" })
            if (s_fonts.TryFind(name, out _)) return name;
        return s_fonts.Families.FirstOrDefault();
    }

    private static (double minY, double maxY, double minX, double maxX) Bounds(VipsPath p)
    {
        double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
        double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
        foreach (var s in p.Segments)
        {
            // Inspect all populated point-coords on the segment.
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
        return (minY, maxY, minX, maxX);
    }

    // ---- Explicit line breaks ----

    [Fact]
    public void Newline_ProducesGlyphsOnTwoBaselines()
    {
        var font = PickFont();
        if (font == null) return;
        var path = VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "Hi\nBye", FontFamily = font, FontSize = 24,
        });
        var (minY, maxY, _, _) = Bounds(path);
        // Two-line text spans more vertical extent than single line — by
        // at least one line height (~24px).
        Assert.True(maxY - minY > 24, $"two-line text should span > 24px, got {maxY - minY:F1}");
    }

    // ---- Wrapping ----

    [Fact]
    public void Wrap_NarrowWidth_BreaksLines()
    {
        var font = PickFont();
        if (font == null) return;
        // Long sentence with WrappingLength = 80px → multiple lines.
        var unwrapped = VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "the quick brown fox", FontFamily = font, FontSize = 16,
        });
        var wrapped = VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "the quick brown fox", FontFamily = font, FontSize = 16,
            WrappingLength = 80,
        });
        var (uMinY, uMaxY, _, _) = Bounds(unwrapped);
        var (wMinY, wMaxY, _, _) = Bounds(wrapped);
        // Wrapped version is shorter horizontally and taller vertically.
        Assert.True(wMaxY - wMinY > uMaxY - uMinY,
            $"wrapped should be taller: unwrapped={uMaxY - uMinY:F1}, wrapped={wMaxY - wMinY:F1}");
    }

    // ---- Alignment ----

    [Fact]
    public void HAlign_Right_ShiftsTextToRightEdge()
    {
        var font = PickFont();
        if (font == null) return;
        // Wrap box of 200px wide. With "Hi" (~24px) at right-align, it
        // should sit near x=200 instead of x=0.
        var left = VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "Hi", FontFamily = font, FontSize = 16, X = 0,
            WrappingLength = 200, HAlign = VipsTextHAlign.Left,
        });
        var right = VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "Hi", FontFamily = font, FontSize = 16, X = 0,
            WrappingLength = 200, HAlign = VipsTextHAlign.Right,
        });
        var (_, _, lMinX, _) = Bounds(left);
        var (_, _, rMinX, _) = Bounds(right);
        Assert.True(rMinX > lMinX + 50,
            $"right-aligned should push text right: left={lMinX:F1}, right={rMinX:F1}");
    }

    [Fact]
    public void HAlign_Center_FallsBetweenLeftAndRight()
    {
        var font = PickFont();
        if (font == null) return;
        var left = VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "Hi", FontFamily = font, FontSize = 16, X = 0,
            WrappingLength = 200, HAlign = VipsTextHAlign.Left,
        });
        var center = VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "Hi", FontFamily = font, FontSize = 16, X = 0,
            WrappingLength = 200, HAlign = VipsTextHAlign.Center,
        });
        var right = VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "Hi", FontFamily = font, FontSize = 16, X = 0,
            WrappingLength = 200, HAlign = VipsTextHAlign.Right,
        });
        var (_, _, lMinX, _) = Bounds(left);
        var (_, _, cMinX, _) = Bounds(center);
        var (_, _, rMinX, _) = Bounds(right);
        Assert.True(cMinX > lMinX && cMinX < rMinX,
            $"center should be between left and right: L={lMinX:F1} C={cMinX:F1} R={rMinX:F1}");
    }

    // ---- Line spacing ----

    [Fact]
    public void LineSpacing_Doubled_SpreadsLinesFurther()
    {
        var font = PickFont();
        if (font == null) return;
        var single = VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "Ab\nCd", FontFamily = font, FontSize = 16, LineSpacing = 1.0,
        });
        var doubled = VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "Ab\nCd", FontFamily = font, FontSize = 16, LineSpacing = 2.0,
        });
        var (sMinY, sMaxY, _, _) = Bounds(single);
        var (dMinY, dMaxY, _, _) = Bounds(doubled);
        // Doubled spacing → vertical extent grows.
        Assert.True(dMaxY - dMinY > sMaxY - sMinY,
            $"2× line spacing should widen vertical extent: 1×={sMaxY - sMinY:F1}, 2×={dMaxY - dMinY:F1}");
    }

    // ---- Word break ----

    [Fact]
    public void WordBreak_BreakAll_AllowsMidWordWrapInTightBox()
    {
        var font = PickFont();
        if (font == null) return;
        // A single long token that won't fit. Standard breaking would
        // overflow horizontally; BreakAll should fold mid-word.
        var standard = VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "Supercalifragilistic", FontFamily = font, FontSize = 16,
            WrappingLength = 60, WordBreak = VipsTextWordBreak.Standard,
        });
        var breakAll = VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "Supercalifragilistic", FontFamily = font, FontSize = 16,
            WrappingLength = 60, WordBreak = VipsTextWordBreak.BreakAll,
        });
        var (_, _, _, sMaxX) = Bounds(standard);
        var (_, _, _, bMaxX) = Bounds(breakAll);
        // BreakAll wraps within the box → narrower horizontal extent.
        Assert.True(bMaxX < sMaxX,
            $"BreakAll should fold within wrap box: standard={sMaxX:F1}, breakAll={bMaxX:F1}");
    }

    // ---- Justify ----

    [Fact]
    public void Justify_InterWord_SpreadsToFillWrapWidth()
    {
        var font = PickFont();
        if (font == null) return;
        // Same wrap width, justify vs none — justify should push glyphs to fill.
        var noJustify = VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "the quick brown", FontFamily = font, FontSize = 16,
            WrappingLength = 200, Justify = VipsTextJustify.None,
        });
        var justified = VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "the quick brown", FontFamily = font, FontSize = 16,
            WrappingLength = 200, Justify = VipsTextJustify.InterWord,
        });
        var (_, _, _, nMaxX) = Bounds(noJustify);
        var (_, _, _, jMaxX) = Bounds(justified);
        // Justified version reaches further right (fills wrap width).
        Assert.True(jMaxX >= nMaxX,
            $"justified should fill wrap width: nojustify={nMaxX:F1}, justified={jMaxX:F1}");
    }

    // ---- Rendered correctness ----

    [Fact]
    public void DrawText_MultiLineWrap_RendersMultipleLinesOfPixels()
    {
        var font = PickFont();
        if (font == null) return;
        var bg = RgbSolid(120, 120, 0, 0, 0);
        var painted = VipsImageOps.DrawText(bg, new VipsTextOptions {
            Text = "the quick brown fox jumps", FontFamily = font, FontSize = 14,
            WrappingLength = 100, Color = new byte[] { 255, 255, 255 },
            X = 5, Y = 5,
        });
        // Find painted-row y positions.
        using var reg = new VipsRegion(painted);
        reg.Prepare(new VipsRect(0, 0, 120, 120));
        int paintedRows = 0;
        for (int y = 0; y < 120; y++)
        {
            bool any = false;
            for (int x = 0; x < 120 && !any; x++)
                if (reg.GetAddress(x, y)[0] >= 100) any = true;
            if (any) paintedRows++;
        }
        // Multiple lines → many painted rows (more than one line height).
        Assert.True(paintedRows > 14, $"wrapped text should paint multiple lines: {paintedRows} rows");
    }
}
