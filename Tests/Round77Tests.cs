using System;
using System.Linq;
using CosmoImage.Operations.Drawing;
using SixLabors.Fonts;
using Xunit;

namespace CosmoImage.Tests;

public class Round77Tests
{
    private static string? PickFont()
    {
        foreach (var name in new[] { "Helvetica", "Arial", "DejaVu Sans", "Liberation Sans" })
            if (SystemFonts.Collection.TryGet(name, out _)) return name;
        return SystemFonts.Collection.Families.FirstOrDefault().Name;
    }

    // ---- Plumbing ----

    [Fact]
    public void OpenTypeFeatures_NullOrEmpty_HasNoEffect()
    {
        var font = PickFont();
        if (font == null) return;
        var noFeatures = VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "Hello", FontFamily = font, FontSize = 24,
        });
        var emptyFeatures = VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "Hello", FontFamily = font, FontSize = 24,
            OpenTypeFeatures = Array.Empty<string>(),
        });
        Assert.Equal(noFeatures.Segments.Count, emptyFeatures.Segments.Count);
    }

    [Fact]
    public void OpenTypeFeatures_ValidTags_DoesNotCrash()
    {
        var font = PickFont();
        if (font == null) return;
        var path = VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "Hi 1/2", FontFamily = font, FontSize = 24,
            OpenTypeFeatures = new[] { "kern", "liga", "frac", "ss01" },
        });
        // Path should still produce glyphs even if the font lacks any of these features.
        Assert.NotEmpty(path.Segments);
    }

    [Fact]
    public void OpenTypeFeatures_MultipleTags_RenderConsistently()
    {
        var font = PickFont();
        if (font == null) return;
        // Same text, same tags in different orders → should render same.
        var a = VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "Test", FontFamily = font, FontSize = 24,
            OpenTypeFeatures = new[] { "kern", "liga" },
        });
        var b = VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "Test", FontFamily = font, FontSize = 24,
            OpenTypeFeatures = new[] { "liga", "kern" },
        });
        Assert.Equal(a.Segments.Count, b.Segments.Count);
    }

    // ---- Validation ----

    [Fact]
    public void OpenTypeFeatures_TooShortTag_Throws()
    {
        var font = PickFont();
        if (font == null) return;
        Assert.Throws<ArgumentException>(() => VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "Hi", FontFamily = font, FontSize = 12,
            OpenTypeFeatures = new[] { "abc" },  // 3 chars
        }));
    }

    [Fact]
    public void OpenTypeFeatures_TooLongTag_Throws()
    {
        var font = PickFont();
        if (font == null) return;
        Assert.Throws<ArgumentException>(() => VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "Hi", FontFamily = font, FontSize = 12,
            OpenTypeFeatures = new[] { "abcde" },  // 5 chars
        }));
    }

    [Fact]
    public void OpenTypeFeatures_NullStringEntry_Throws()
    {
        var font = PickFont();
        if (font == null) return;
        Assert.Throws<ArgumentException>(() => VipsImageOps.TextToPath(new VipsTextOptions {
            Text = "Hi", FontFamily = font, FontSize = 12,
            OpenTypeFeatures = new string[] { null! },
        }));
    }
}
