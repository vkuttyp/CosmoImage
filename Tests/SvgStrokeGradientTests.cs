using System;
using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Loaders;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Tests for SVG stroke painted with a paint-server (linear / radial
/// gradient) rather than a solid colour. Stroke geometry is the same
/// per-segment quad envelope used for solid strokes; the only new
/// behaviour is dispatching through the gradient sampler.
/// </summary>
public class SvgStrokeGradientTests
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
    public async Task Rect_StrokedWithHorizontalLinearGradient_LeftRedRightBlue()
    {
        // 40-wide rect with a thick stroke that occupies the top and bottom
        // strips. Horizontal gradient (red → blue) along x = 0..40. Sample
        // the top stroke at x≈2 (red) and x≈37 (blue).
        const string svg =
            "<svg width=\"40\" height=\"20\">" +
            "<defs><linearGradient id=\"g\" gradientUnits=\"userSpaceOnUse\" x1=\"0\" y1=\"0\" x2=\"40\" y2=\"0\">" +
            "<stop offset=\"0\" stop-color=\"red\"/>" +
            "<stop offset=\"1\" stop-color=\"blue\"/>" +
            "</linearGradient></defs>" +
            "<rect x=\"2\" y=\"2\" width=\"36\" height=\"16\" fill=\"none\" " +
            "stroke=\"url(#g)\" stroke-width=\"4\"/></svg>";
        var img = await LoadSvg(svg);
        var px = img.Pixels ?? img.PixelsLazy!.Value;

        // Top edge of stroke ≈ y=2. Sample (4, 2) on left stroke → near-red.
        int iLeft = (2 * 40 + 4) * 4;
        Assert.True(px[iLeft + 0] > 150, $"left stroke R too low: {px[iLeft + 0]}");
        Assert.True(px[iLeft + 2] < 80,  $"left stroke B too high: {px[iLeft + 2]}");

        // Sample (36, 2) on right stroke → near-blue.
        int iRight = (2 * 40 + 36) * 4;
        Assert.True(px[iRight + 0] < 80,  $"right stroke R too high: {px[iRight + 0]}");
        Assert.True(px[iRight + 2] > 150, $"right stroke B too low: {px[iRight + 2]}");

        // Centre of the rect remains transparent (fill=none).
        int iCentre = (10 * 40 + 20) * 4;
        Assert.Equal(0, px[iCentre + 3]);
    }

    [Fact]
    public async Task Line_StrokedWithLinearGradient_TopHalfRedBottomHalfBlue()
    {
        // Vertical line from (10, 2) to (10, 18), 6 wide. Vertical gradient
        // bound to the line's bbox in user space (y1=2 → red, y2=18 → blue).
        const string svg =
            "<svg width=\"20\" height=\"20\">" +
            "<defs><linearGradient id=\"g\" gradientUnits=\"userSpaceOnUse\" x1=\"0\" y1=\"2\" x2=\"0\" y2=\"18\">" +
            "<stop offset=\"0\" stop-color=\"red\"/>" +
            "<stop offset=\"1\" stop-color=\"blue\"/>" +
            "</linearGradient></defs>" +
            "<line x1=\"10\" y1=\"2\" x2=\"10\" y2=\"18\" stroke=\"url(#g)\" stroke-width=\"6\"/></svg>";
        var img = await LoadSvg(svg);
        var px = img.Pixels ?? img.PixelsLazy!.Value;

        int iTop = (4 * 20 + 10) * 4;
        Assert.True(px[iTop + 0] > 150, $"top R too low: {px[iTop + 0]}");
        Assert.True(px[iTop + 2] < 80,  $"top B too high: {px[iTop + 2]}");

        int iBottom = (16 * 20 + 10) * 4;
        Assert.True(px[iBottom + 0] < 80,  $"bottom R too high: {px[iBottom + 0]}");
        Assert.True(px[iBottom + 2] > 150, $"bottom B too low: {px[iBottom + 2]}");
    }

    [Fact]
    public async Task Path_StrokedWithRadialGradient_RendersWithoutThrowing()
    {
        // Regression: before this change, any non-solid stroke on <path>
        // threw NotSupportedException. Now it should render through the
        // sampler. We only assert "some opaque pixels along the path".
        const string svg =
            "<svg width=\"30\" height=\"30\">" +
            "<defs><radialGradient id=\"r\" gradientUnits=\"userSpaceOnUse\" cx=\"15\" cy=\"15\" r=\"15\">" +
            "<stop offset=\"0\" stop-color=\"green\"/>" +
            "<stop offset=\"1\" stop-color=\"yellow\"/>" +
            "</radialGradient></defs>" +
            "<path d=\"M 5 5 L 25 5 L 25 25\" fill=\"none\" stroke=\"url(#r)\" stroke-width=\"3\"/></svg>";
        var img = await LoadSvg(svg);
        var px = img.Pixels ?? img.PixelsLazy!.Value;

        int opaque = 0;
        for (int i = 3; i < px.Length; i += 4) if (px[i] > 200) opaque++;
        Assert.True(opaque > 30, $"expected opaque stroke pixels; got {opaque}");
    }

    [Fact]
    public async Task Ellipse_StrokedWithLinearGradient_RendersGradient()
    {
        const string svg =
            "<svg width=\"40\" height=\"20\">" +
            "<defs><linearGradient id=\"g\" gradientUnits=\"userSpaceOnUse\" x1=\"0\" y1=\"0\" x2=\"40\" y2=\"0\">" +
            "<stop offset=\"0\" stop-color=\"red\"/>" +
            "<stop offset=\"1\" stop-color=\"blue\"/>" +
            "</linearGradient></defs>" +
            "<ellipse cx=\"20\" cy=\"10\" rx=\"18\" ry=\"8\" fill=\"none\" " +
            "stroke=\"url(#g)\" stroke-width=\"4\"/></svg>";
        var img = await LoadSvg(svg);
        var px = img.Pixels ?? img.PixelsLazy!.Value;

        // Left vertex of ellipse ≈ (2, 10). Stroke covers neighbourhood.
        int iLeft = (10 * 40 + 3) * 4;
        // Right vertex ≈ (38, 10).
        int iRight = (10 * 40 + 37) * 4;

        Assert.True(px[iLeft + 3] > 100, "left vertex should be on the stroke");
        Assert.True(px[iLeft + 0] > px[iLeft + 2],
            $"left vertex should be red-leaning: R={px[iLeft + 0]} B={px[iLeft + 2]}");
        Assert.True(px[iRight + 3] > 100, "right vertex should be on the stroke");
        Assert.True(px[iRight + 2] > px[iRight + 0],
            $"right vertex should be blue-leaning: R={px[iRight + 0]} B={px[iRight + 2]}");
    }

    [Fact]
    public async Task Polygon_StrokedWithLinearGradient_BothExtremesPresent()
    {
        const string svg =
            "<svg width=\"40\" height=\"20\">" +
            "<defs><linearGradient id=\"g\" gradientUnits=\"userSpaceOnUse\" x1=\"0\" y1=\"0\" x2=\"40\" y2=\"0\">" +
            "<stop offset=\"0\" stop-color=\"red\"/>" +
            "<stop offset=\"1\" stop-color=\"blue\"/>" +
            "</linearGradient></defs>" +
            "<polygon points=\"4,4 36,4 36,16 4,16\" fill=\"none\" " +
            "stroke=\"url(#g)\" stroke-width=\"3\"/></svg>";
        var img = await LoadSvg(svg);
        var px = img.Pixels ?? img.PixelsLazy!.Value;

        // Top edge at y=4: x=5 → near-red, x=35 → near-blue.
        int iLeft = (4 * 40 + 5) * 4;
        int iRight = (4 * 40 + 35) * 4;
        Assert.True(px[iLeft + 0] > px[iLeft + 2] + 40,
            $"polygon stroke left should be red-leaning: R={px[iLeft + 0]} B={px[iLeft + 2]}");
        Assert.True(px[iRight + 2] > px[iRight + 0] + 40,
            $"polygon stroke right should be blue-leaning: R={px[iRight + 0]} B={px[iRight + 2]}");
    }
}
