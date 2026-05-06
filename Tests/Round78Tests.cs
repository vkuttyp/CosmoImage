using System.Linq;
using CosmoImage.Operations.Drawing;
using CosmoFonts.Loader;
using Xunit;

namespace CosmoImage.Tests;

public class Round78Tests
{
    private static readonly SystemFontCollection s_fonts = SystemFontCollection.LoadDefault();

    private static string? PickFont()
    {
        foreach (var name in new[] { "Helvetica", "Arial", "DejaVu Sans", "Liberation Sans" })
            if (s_fonts.TryFind(name, out _)) return name;
        return s_fonts.Families.FirstOrDefault();
    }

    [Fact]
    public void MeasureText_Empty_ReturnsZeroSize()
    {
        var font = PickFont();
        if (font == null) return;
        var size = VipsImageOps.MeasureText(new VipsTextOptions {
            Text = "", FontFamily = font, FontSize = 24,
        });
        Assert.Equal(0, size.Width);
        Assert.Equal(0, size.Height);
    }

    [Fact]
    public void MeasureText_NonEmpty_HasPositiveExtents()
    {
        var font = PickFont();
        if (font == null) return;
        var size = VipsImageOps.MeasureText(new VipsTextOptions {
            Text = "Hello", FontFamily = font, FontSize = 24,
        });
        Assert.True(size.Width > 0, $"width should be > 0, got {size.Width}");
        Assert.True(size.Height > 0, $"height should be > 0, got {size.Height}");
    }

    [Fact]
    public void MeasureText_LongerStringWider()
    {
        var font = PickFont();
        if (font == null) return;
        var shortSize = VipsImageOps.MeasureText(new VipsTextOptions {
            Text = "Hi", FontFamily = font, FontSize = 24,
        });
        var longSize = VipsImageOps.MeasureText(new VipsTextOptions {
            Text = "Hello there world", FontFamily = font, FontSize = 24,
        });
        Assert.True(longSize.Width > shortSize.Width,
            $"longer text should be wider: {shortSize.Width} vs {longSize.Width}");
    }

    [Fact]
    public void MeasureText_LargerFontSizeBigger()
    {
        var font = PickFont();
        if (font == null) return;
        var smallSize = VipsImageOps.MeasureText(new VipsTextOptions {
            Text = "Hi", FontFamily = font, FontSize = 12,
        });
        var bigSize = VipsImageOps.MeasureText(new VipsTextOptions {
            Text = "Hi", FontFamily = font, FontSize = 48,
        });
        Assert.True(bigSize.Width > smallSize.Width * 2,
            $"4× size should be > 2× wider: {smallSize.Width} vs {bigSize.Width}");
    }

    [Fact]
    public void MeasureBounds_TighterThanLayoutSize()
    {
        // Bounds excludes sidebearings → width ≤ MeasureText width.
        var font = PickFont();
        if (font == null) return;
        var size = VipsImageOps.MeasureText(new VipsTextOptions {
            Text = "Hello", FontFamily = font, FontSize = 24,
        });
        var bounds = VipsImageOps.MeasureTextBounds(new VipsTextOptions {
            Text = "Hello", FontFamily = font, FontSize = 24,
        });
        Assert.True(bounds.Width <= size.Width,
            $"bounds should be ≤ layout size: bounds={bounds.Width:F1}, size={size.Width:F1}");
    }

    [Fact]
    public void CountLines_Empty_ReturnsZero()
    {
        var font = PickFont();
        if (font == null) return;
        Assert.Equal(0, VipsImageOps.CountTextLines(new VipsTextOptions {
            Text = "", FontFamily = font, FontSize = 12,
        }));
    }

    [Fact]
    public void CountLines_SingleLine_ReturnsOne()
    {
        var font = PickFont();
        if (font == null) return;
        Assert.Equal(1, VipsImageOps.CountTextLines(new VipsTextOptions {
            Text = "Hello", FontFamily = font, FontSize = 12,
        }));
    }

    [Fact]
    public void CountLines_ExplicitNewlines_ReturnsCount()
    {
        var font = PickFont();
        if (font == null) return;
        Assert.Equal(3, VipsImageOps.CountTextLines(new VipsTextOptions {
            Text = "one\ntwo\nthree", FontFamily = font, FontSize = 12,
        }));
    }

    [Fact]
    public void CountLines_Wrapping_CountsWrappedLines()
    {
        var font = PickFont();
        if (font == null) return;
        // Long string + narrow wrap = multiple lines.
        var lines = VipsImageOps.CountTextLines(new VipsTextOptions {
            Text = "the quick brown fox jumps over the lazy dog",
            FontFamily = font, FontSize = 16, WrappingLength = 80,
        });
        Assert.True(lines > 1, $"should wrap into multiple lines, got {lines}");
    }

    [Fact]
    public void MeasureText_MultiLine_HeightScalesWithLineCount()
    {
        var font = PickFont();
        if (font == null) return;
        var oneLine = VipsImageOps.MeasureText(new VipsTextOptions {
            Text = "one", FontFamily = font, FontSize = 16,
        });
        var threeLines = VipsImageOps.MeasureText(new VipsTextOptions {
            Text = "one\ntwo\nthree", FontFamily = font, FontSize = 16,
        });
        Assert.True(threeLines.Height > oneLine.Height * 2,
            $"3 lines should be > 2× taller than 1: {oneLine.Height} vs {threeLines.Height}");
    }
}
