using System;
using System.Collections.Generic;
using System.Linq;
using CosmoImage.Operations.Metadata;
using Xunit;

namespace CosmoImage.Tests;

public class Round98Tests
{
    private static VipsImage NewImage() => new VipsImage { Width = 4, Height = 4, Bands = 3 };

    // ---- PNG text chunks ----

    [Fact]
    public void PngText_RoundTripSingleKeyword()
    {
        var img = NewImage();
        img.SetPngText("Author", "Alice");
        Assert.Equal("Alice", img.GetPngText("Author"));
    }

    [Fact]
    public void PngText_MultipleKeywordsCoexist()
    {
        var img = NewImage();
        img.SetPngText("Title", "My photo");
        img.SetPngText("Author", "Alice");
        img.SetPngText("Copyright", "(c) 2026");
        Assert.Equal("My photo", img.GetPngText("Title"));
        Assert.Equal("Alice", img.GetPngText("Author"));
        Assert.Equal("(c) 2026", img.GetPngText("Copyright"));
    }

    [Fact]
    public void PngText_StandardKeywordsConstants()
    {
        var img = NewImage();
        img.SetPngText(VipsFormatMetadataExtensions.PngTextKeywords.Title, "X");
        img.SetPngText(VipsFormatMetadataExtensions.PngTextKeywords.Author, "Y");
        img.SetPngText(VipsFormatMetadataExtensions.PngTextKeywords.CreationTime, "2026-05-04T12:00:00Z");
        Assert.Equal("X", img.GetPngText("Title"));
        Assert.Equal("Y", img.GetPngText("Author"));
        Assert.Equal("2026-05-04T12:00:00Z", img.GetPngText("Creation Time"));
    }

    [Fact]
    public void PngText_GetAllReturnsAllKeywords()
    {
        var img = NewImage();
        img.SetPngText("Title", "T");
        img.SetPngText("Author", "A");
        img.SetPngText("Copyright", "C");
        var all = img.GetAllPngText();
        Assert.Equal(3, all.Count);
        Assert.Equal("T", all["Title"]);
        Assert.Equal("A", all["Author"]);
        Assert.Equal("C", all["Copyright"]);
    }

    [Fact]
    public void PngText_GetAllEmpty_WhenNothingSet()
    {
        var img = NewImage();
        Assert.Empty(img.GetAllPngText());
    }

    [Fact]
    public void PngText_RemoveDropsKeyword()
    {
        var img = NewImage();
        img.SetPngText("Title", "X");
        Assert.True(img.RemovePngText("Title"));
        Assert.Null(img.GetPngText("Title"));
        Assert.False(img.RemovePngText("Title"));
    }

    [Fact]
    public void PngText_SetAllReplacesEverything()
    {
        var img = NewImage();
        img.SetPngText("Old", "value");
        img.SetAllPngText(new Dictionary<string, string> {
            { "New1", "v1" },
            { "New2", "v2" },
        });
        Assert.Null(img.GetPngText("Old"));
        Assert.Equal("v1", img.GetPngText("New1"));
        Assert.Equal("v2", img.GetPngText("New2"));
    }

    [Fact]
    public void PngText_UnicodeRoundTrip()
    {
        var img = NewImage();
        img.SetPngText("Description", "日本語の説明 — émoji 🎨");
        Assert.Equal("日本語の説明 — émoji 🎨", img.GetPngText("Description"));
    }

    [Fact]
    public void PngText_EmptyKeywordRejected()
    {
        var img = NewImage();
        Assert.Throws<ArgumentException>(() => img.SetPngText("", "value"));
    }

    [Fact]
    public void PngText_DoesntCollideWithOtherMetadata()
    {
        var img = NewImage();
        img.Metadata["orientation"] = "1";
        img.Metadata["custom-key"] = "value";
        img.SetPngText("Title", "T");
        // GetAllPngText should only return PNG text entries.
        var all = img.GetAllPngText();
        Assert.Single(all);
        Assert.Equal("T", all["Title"]);
        // Other metadata still present.
        Assert.Equal("1", img.Metadata["orientation"]);
        Assert.Equal("value", img.Metadata["custom-key"]);
    }

    // ---- JPEG comment ----

    [Fact]
    public void JpegComment_RoundTrip()
    {
        var img = NewImage();
        img.SetJpegComment("Captured with CosmoImage");
        Assert.Equal("Captured with CosmoImage", img.GetJpegComment());
    }

    [Fact]
    public void JpegComment_MissingReturnsNull()
    {
        var img = NewImage();
        Assert.Null(img.GetJpegComment());
    }

    [Fact]
    public void JpegComment_RemoveDropsValue()
    {
        var img = NewImage();
        img.SetJpegComment("X");
        Assert.True(img.RemoveJpegComment());
        Assert.Null(img.GetJpegComment());
        Assert.False(img.RemoveJpegComment());
    }

    // ---- GIF comment ----

    [Fact]
    public void GifComment_RoundTrip()
    {
        var img = NewImage();
        img.SetGifComment("Animated test");
        Assert.Equal("Animated test", img.GetGifComment());
    }

    [Fact]
    public void GifComment_RemoveDropsValue()
    {
        var img = NewImage();
        img.SetGifComment("X");
        Assert.True(img.RemoveGifComment());
        Assert.Null(img.GetGifComment());
    }

    // ---- Independence ----

    [Fact]
    public void DifferentFormatComments_DontInterfere()
    {
        var img = NewImage();
        img.SetJpegComment("jpeg-comment");
        img.SetGifComment("gif-comment");
        img.SetPngText("Title", "png-title");
        Assert.Equal("jpeg-comment", img.GetJpegComment());
        Assert.Equal("gif-comment", img.GetGifComment());
        Assert.Equal("png-title", img.GetPngText("Title"));
    }
}
