using System.Linq;
using CosmoImage.Operations.Drawing;
using SixLabors.Fonts;
using Xunit;

namespace CosmoImage.Tests;

public class Round75Tests
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

    private static int CountSubpaths(VipsPath p)
        => p.Segments.Count(s => s.Kind == VipsPathSegmentKind.MoveTo);

    private static int CountPaintedPixels(VipsImage img, byte threshold = 100)
    {
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, img.Width, img.Height));
        int n = 0;
        for (int y = 0; y < img.Height; y++)
            for (int x = 0; x < img.Width; x++)
                if (reg.GetAddress(x, y)[0] >= threshold) n++;
        return n;
    }

    // ---- Decorations ----

    [Fact]
    public void Underline_AddsAdditionalSubpaths()
    {
        // SL.Fonts emits SetDecoration once per shaping run; the
        // underline shows up as one or more additional rectangle
        // sub-paths beyond the plain glyphs.
        var font = PickFont();
        if (font == null) return;
        var plain = VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "Hello", FontFamily = font, FontSize = 24,
        });
        var underlined = VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "Hello", FontFamily = font, FontSize = 24,
            Decorations = VipsTextDecoration.Underline,
        });
        Assert.True(CountSubpaths(underlined) > CountSubpaths(plain),
            $"underline should add sub-paths: plain={CountSubpaths(plain)}, " +
            $"underlined={CountSubpaths(underlined)}");
    }

    [Fact]
    public void Underline_PlacedBelowGlyphBaseline()
    {
        var font = PickFont();
        if (font == null) return;
        // The underline rectangle should sit at y > baseline (descender region).
        var plain = VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "M", FontFamily = font, FontSize = 32, Y = 50,
        });
        var underlined = VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "M", FontFamily = font, FontSize = 32, Y = 50,
            Decorations = VipsTextDecoration.Underline,
        });
        var (_, _, _, plainMaxY) = Bounds(plain);
        var (_, _, _, underMaxY) = Bounds(underlined);
        // Underlined should reach further down than plain text.
        Assert.True(underMaxY > plainMaxY,
            $"underline should sit below glyph: plain={plainMaxY:F1}, underlined={underMaxY:F1}");
    }

    [Fact]
    public void Overline_PlacedAboveGlyphTop()
    {
        var font = PickFont();
        if (font == null) return;
        var plain = VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "M", FontFamily = font, FontSize = 32, Y = 50,
        });
        var overlined = VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "M", FontFamily = font, FontSize = 32, Y = 50,
            Decorations = VipsTextDecoration.Overline,
        });
        var (_, _, plainMinY, _) = Bounds(plain);
        var (_, _, overMinY, _) = Bounds(overlined);
        Assert.True(overMinY < plainMinY,
            $"overline should sit above glyph: plain={plainMinY:F1}, overlined={overMinY:F1}");
    }

    [Fact]
    public void CombinedDecorations_AddMoreSubpathsThanSingleDecoration()
    {
        var font = PickFont();
        if (font == null) return;
        var single = VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "Hi", FontFamily = font, FontSize = 24,
            Decorations = VipsTextDecoration.Underline,
        });
        var both = VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "Hi", FontFamily = font, FontSize = 24,
            Decorations = VipsTextDecoration.Underline | VipsTextDecoration.Strikeout,
        });
        Assert.True(CountSubpaths(both) > CountSubpaths(single),
            $"combined decorations > single: single={CountSubpaths(single)}, both={CountSubpaths(both)}");
    }

    [Fact]
    public void DrawText_WithUnderline_PaintsMorePixelsThanWithout()
    {
        var font = PickFont();
        if (font == null) return;
        var bg = RgbSolid(200, 60, 0, 0, 0);
        var plain = VipsImageOps.DrawText(bg, new VipsTextOptions {
            Text = "Underlined", FontFamily = font, FontSize = 18,
            Color = new byte[] { 255, 255, 255 }, X = 10, Y = 10,
        });
        var underlined = VipsImageOps.DrawText(bg, new VipsTextOptions {
            Text = "Underlined", FontFamily = font, FontSize = 18,
            Color = new byte[] { 255, 255, 255 }, X = 10, Y = 10,
            Decorations = VipsTextDecoration.Underline,
        });
        int plainCnt = CountPaintedPixels(plain);
        int underCnt = CountPaintedPixels(underlined);
        Assert.True(underCnt > plainCnt + 30,
            $"underline should add visible pixels: plain={plainCnt}, underlined={underCnt}");
    }

    // ---- Vertical layout ----

    [Fact]
    public void VerticalLayout_TallerThanWide()
    {
        var font = PickFont();
        if (font == null) return;
        var horizontal = VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "Hi", FontFamily = font, FontSize = 24,
            LayoutMode = VipsTextLayoutMode.HorizontalTopBottom,
        });
        var vertical = VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "Hi", FontFamily = font, FontSize = 24,
            LayoutMode = VipsTextLayoutMode.VerticalLeftRight,
        });
        var (hMinX, hMaxX, hMinY, hMaxY) = Bounds(horizontal);
        var (vMinX, vMaxX, vMinY, vMaxY) = Bounds(vertical);
        double hRatio = (hMaxX - hMinX) / (hMaxY - hMinY);
        double vRatio = (vMaxX - vMinX) / (vMaxY - vMinY);
        // Horizontal text: wider than tall. Vertical text: taller than wide.
        Assert.True(hRatio > 1, $"horizontal should be wider than tall, ratio={hRatio:F2}");
        Assert.True(vRatio < 1, $"vertical should be taller than wide, ratio={vRatio:F2}");
    }

    // ---- TextDirection ----

    [Fact]
    public void TextDirection_RtlAndLtr_ProduceDifferentLayouts()
    {
        var font = PickFont();
        if (font == null) return;
        var ltr = VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "AB", FontFamily = font, FontSize = 24,
            TextDirection = VipsTextDirection.LeftToRight,
        });
        var rtl = VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "AB", FontFamily = font, FontSize = 24,
            TextDirection = VipsTextDirection.RightToLeft,
        });
        // Both produce paths, but vertex coordinates should differ
        // (RTL reverses glyph order so the same letters land at different X).
        bool anyDiff = false;
        for (int i = 0; i < System.Math.Min(ltr.Segments.Count, rtl.Segments.Count); i++)
        {
            if (System.Math.Abs(ltr.Segments[i].X1 - rtl.Segments[i].X1) > 0.1)
            {
                anyDiff = true;
                break;
            }
        }
        Assert.True(anyDiff, "RTL should produce different glyph positions than LTR");
    }
}
