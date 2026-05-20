using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Loaders;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Tests for the pure-managed SVG <c>&lt;text&gt;</c> element via CosmoFonts.
/// Phase-1 scope: x/y/font-size/solid+gradient fill, single registered font,
/// LTR Latin. No tspan, no text-anchor non-default, no textPath.
/// </summary>
public class SvgTextTests
{
    private const string FontPath = "Assets/NotoSans-Regular.ttf";

    public SvgTextTests()
    {
        // Tests share static font-registry state; reset each construction so
        // the order of test execution doesn't matter.
        VipsSvgLoader.ClearRegisteredFonts();
        if (File.Exists(FontPath))
            VipsSvgLoader.RegisterFont("Noto Sans", File.ReadAllBytes(FontPath));
    }

    private static async Task<PipeVipsSource> SourceFromAsync(string svg)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(svg);
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(bytes);
        await pipe.Writer.CompleteAsync();
        return new PipeVipsSource(pipe.Reader);
    }

    [Fact]
    public async Task RendersText_ProducesOpaqueFillPixelsInExpectedRegion()
    {
        // Black text "Hi" rendered at (10, 40) with 32pt font on a 100×60
        // transparent canvas. The glyph fill area should contain at least
        // some opaque-black pixels in the upper-left quadrant.
        const string svg = @"<svg xmlns='http://www.w3.org/2000/svg' width='100' height='60'>
            <text x='10' y='40' font-family='Noto Sans' font-size='32' fill='#000000'>Hi</text>
        </svg>";

        await using var source = await SourceFromAsync(svg);
        var img = await VipsSvgLoader.LoadAsync(source);
        Assert.NotNull(img);
        Assert.Equal(4, img!.Bands);
        var pixels = img.Pixels ?? img.PixelsLazy!.Value;

        int opaqueBlack = 0;
        for (int y = 5; y < 50; y++)
            for (int x = 5; x < 80; x++)
            {
                int i = (y * img.Width + x) * 4;
                if (pixels[i + 3] > 200 && pixels[i + 0] < 32 && pixels[i + 1] < 32 && pixels[i + 2] < 32)
                    opaqueBlack++;
            }
        Assert.True(opaqueBlack > 20,
            $"expected >20 opaque-black pixels in the text region; got {opaqueBlack}");
    }

    [Fact]
    public async Task Text_RegionOutsideGlyphs_IsTransparent()
    {
        // The far-right pixel beyond where "X" can plausibly extend at 16pt
        // must remain fully transparent (no alpha smear from glyph fill).
        const string svg = @"<svg xmlns='http://www.w3.org/2000/svg' width='100' height='40'>
            <text x='5' y='20' font-family='Noto Sans' font-size='16' fill='#ff0000'>X</text>
        </svg>";

        await using var source = await SourceFromAsync(svg);
        var img = await VipsSvgLoader.LoadAsync(source);
        Assert.NotNull(img);
        var pixels = img!.Pixels ?? img.PixelsLazy!.Value;
        int farRight = (10 * img.Width + 90) * 4;
        Assert.Equal(0, pixels[farRight + 3]);
    }

    [Fact]
    public async Task Text_NoRegisteredFont_ThrowsClearMessage()
    {
        VipsSvgLoader.ClearRegisteredFonts();
        const string svg = @"<svg xmlns='http://www.w3.org/2000/svg' width='50' height='30'>
            <text x='2' y='20' font-size='12'>x</text>
        </svg>";

        await using var source = await SourceFromAsync(svg);
        var ex = await Assert.ThrowsAsync<System.NotSupportedException>(
            async () => await VipsSvgLoader.LoadAsync(source));
        Assert.Contains("RegisterFont", ex.Message);
    }

    [Fact]
    public async Task Text_EmptyTextContent_RendersNothingButDoesNotThrow()
    {
        const string svg = @"<svg xmlns='http://www.w3.org/2000/svg' width='40' height='30'>
            <text x='5' y='20' font-family='Noto Sans' font-size='16' fill='#000000'></text>
        </svg>";

        await using var source = await SourceFromAsync(svg);
        var img = await VipsSvgLoader.LoadAsync(source);
        Assert.NotNull(img);
        var pixels = img!.Pixels ?? img.PixelsLazy!.Value;
        for (int p = 0; p < pixels.Length; p += 4)
            Assert.Equal(0, pixels[p + 3]);
    }
}
