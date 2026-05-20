using System;
using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Loaders;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Tests for SVG <c>&lt;mask&gt;</c> support — luminance-modulated alpha.
/// Phase-1 scope: <c>maskContentUnits="userSpaceOnUse"</c> + default
/// <c>maskUnits</c>. Mask region rendered over the full canvas; per-pixel
/// mask value = SVG-1.1 luminance × alpha.
/// </summary>
public class SvgMaskTests
{
    private static async ValueTask<VipsImage> LoadSvg(string svg)
    {
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(System.Text.Encoding.UTF8.GetBytes(svg));
        await pipe.Writer.CompleteAsync();
        await using var source = new PipeVipsSource(pipe.Reader);
        var img = await VipsSvgLoader.LoadAsync(source);
        Assert.NotNull(img);
        return img!;
    }

    [Fact]
    public async Task WhiteMask_PassesThroughFully()
    {
        // Mask filled with opaque white covers the whole canvas →
        // luminance = 1, α = 1, mask value = 255 → masked element renders
        // unchanged.
        const string svg =
            "<svg width=\"20\" height=\"20\">" +
            "<defs><mask id=\"m\"><rect width=\"20\" height=\"20\" fill=\"white\"/></mask></defs>" +
            "<rect width=\"20\" height=\"20\" fill=\"#FF0000\" mask=\"url(#m)\"/></svg>";
        var img = await LoadSvg(svg);
        var px = img.PixelsLazy!.Value;
        int i = (10 * 20 + 10) * 4;
        Assert.Equal(255, px[i + 0]);
        Assert.Equal(0,   px[i + 1]);
        Assert.Equal(0,   px[i + 2]);
        Assert.Equal(255, px[i + 3]);
    }

    [Fact]
    public async Task BlackMask_BlocksFully()
    {
        // Black-filled mask → luminance = 0 → mask value = 0 → masked
        // element completely hidden.
        const string svg =
            "<svg width=\"20\" height=\"20\">" +
            "<defs><mask id=\"m\"><rect width=\"20\" height=\"20\" fill=\"black\"/></mask></defs>" +
            "<rect width=\"20\" height=\"20\" fill=\"#FF0000\" mask=\"url(#m)\"/></svg>";
        var img = await LoadSvg(svg);
        var px = img.PixelsLazy!.Value;
        // Canvas should remain transparent — nothing painted.
        for (int p = 0; p < px.Length; p += 4)
            Assert.Equal(0, px[p + 3]);
    }

    [Fact]
    public async Task PartialMask_ShowsMaskedElementOnlyWhereMaskIsBright()
    {
        // Mask: left half white, right half black. Element: 20×20 red fill.
        // Expect left half opaque red, right half transparent.
        const string svg =
            "<svg width=\"20\" height=\"20\">" +
            "<defs><mask id=\"m\">" +
            "<rect x=\"0\"  y=\"0\" width=\"10\" height=\"20\" fill=\"white\"/>" +
            "<rect x=\"10\" y=\"0\" width=\"10\" height=\"20\" fill=\"black\"/>" +
            "</mask></defs>" +
            "<rect width=\"20\" height=\"20\" fill=\"#FF0000\" mask=\"url(#m)\"/></svg>";
        var img = await LoadSvg(svg);
        var px = img.PixelsLazy!.Value;
        // Left side (5, 10): full red.
        int iLeft = (10 * 20 + 5) * 4;
        Assert.True(px[iLeft + 0] > 240, $"left R: {px[iLeft + 0]}");
        Assert.Equal(255, px[iLeft + 3]);
        // Right side (15, 10): transparent.
        int iRight = (10 * 20 + 15) * 4;
        Assert.Equal(0, px[iRight + 3]);
    }

    [Fact]
    public async Task GrayMask_ModulatesAlphaIntoMiddleValues()
    {
        // 50%-gray mask → luminance ≈ 0.5 × 255 → mask value ≈ 128. Painted
        // alpha is multiplied by ~128/255, giving partial transparency.
        const string svg =
            "<svg width=\"20\" height=\"20\">" +
            "<defs><mask id=\"m\"><rect width=\"20\" height=\"20\" fill=\"#808080\"/></mask></defs>" +
            "<rect width=\"20\" height=\"20\" fill=\"#FF0000\" mask=\"url(#m)\"/></svg>";
        var img = await LoadSvg(svg);
        var px = img.PixelsLazy!.Value;
        int i = (10 * 20 + 10) * 4;
        // Expected output alpha (over transparent background):
        // outAlpha = 1 - (1 - 128/255) = 128/255 ≈ 0.502 → byte ≈ 128.
        // R is straight (src.A=128, dst transparent) → 255.
        Assert.InRange(px[i + 3], 100, 156);
        Assert.True(px[i + 0] > 200, $"R fell off: {px[i + 0]}");
    }

    [Fact]
    public async Task ClipPathAndMask_StackMultiplicatively()
    {
        // Combine a clip-path (binary alpha) with a mask (soft alpha). The
        // intersected mask should respect BOTH: clipped to the left half
        // AND further modulated by the mask's gray value.
        const string svg =
            "<svg width=\"20\" height=\"20\">" +
            "<defs>" +
            "<clipPath id=\"c\"><rect x=\"0\" y=\"0\" width=\"10\" height=\"20\"/></clipPath>" +
            "<mask id=\"m\"><rect width=\"20\" height=\"20\" fill=\"#808080\"/></mask>" +
            "</defs>" +
            "<rect width=\"20\" height=\"20\" fill=\"#FF0000\" clip-path=\"url(#c)\" mask=\"url(#m)\"/>" +
            "</svg>";
        var img = await LoadSvg(svg);
        var px = img.PixelsLazy!.Value;
        // Left side (5, 10): clip pass, mask gray → partial alpha.
        int iLeft = (10 * 20 + 5) * 4;
        Assert.InRange(px[iLeft + 3], 80, 180);
        // Right side (15, 10): clipped out → transparent.
        int iRight = (10 * 20 + 15) * 4;
        Assert.Equal(0, px[iRight + 3]);
    }
}
