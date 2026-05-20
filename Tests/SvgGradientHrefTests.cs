using System;
using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Loaders;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Tests for SVG gradient inheritance via <c>xlink:href</c> / <c>href</c>.
/// A derived gradient can pull stops + attributes from a base gradient;
/// any attribute the derived one sets explicitly overrides the inherited
/// value. Stops are an all-or-nothing inheritance.
/// </summary>
public class SvgGradientHrefTests
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
    public async Task DerivedLinear_InheritsStopsFromBase()
    {
        // Base has the red→blue stops; derived has only the endpoint
        // coordinates. Render the derived gradient and verify both colours
        // appear at the expected ends.
        const string svg =
            "<svg xmlns=\"http://www.w3.org/2000/svg\" " +
            "xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"20\" height=\"4\">" +
            "<defs>" +
            "<linearGradient id=\"base\">" +
            "<stop offset=\"0\" stop-color=\"red\"/>" +
            "<stop offset=\"1\" stop-color=\"blue\"/>" +
            "</linearGradient>" +
            "<linearGradient id=\"derived\" xlink:href=\"#base\" " +
            "gradientUnits=\"userSpaceOnUse\" x1=\"0\" y1=\"0\" x2=\"20\" y2=\"0\"/>" +
            "</defs>" +
            "<rect width=\"20\" height=\"4\" fill=\"url(#derived)\"/></svg>";
        var img = await LoadSvg(svg);
        var px = img.PixelsLazy!.Value;
        int iLeft = (2 * 20 + 1) * 4;
        int iRight = (2 * 20 + 18) * 4;
        Assert.True(px[iLeft + 0] > 200, $"left R: {px[iLeft + 0]}");
        Assert.True(px[iLeft + 2] < 60,  $"left B: {px[iLeft + 2]}");
        Assert.True(px[iRight + 0] < 60,  $"right R: {px[iRight + 0]}");
        Assert.True(px[iRight + 2] > 200, $"right B: {px[iRight + 2]}");
    }

    [Fact]
    public async Task DerivedLinear_OverridesCoordsButInheritsStops()
    {
        // Base has horizontal coords; derived flips to vertical and inherits
        // the stop list. Vertical red-at-top → blue-at-bottom.
        const string svg =
            "<svg xmlns=\"http://www.w3.org/2000/svg\" " +
            "xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"4\" height=\"20\">" +
            "<defs>" +
            "<linearGradient id=\"base\" gradientUnits=\"userSpaceOnUse\" " +
            "x1=\"0\" y1=\"0\" x2=\"100\" y2=\"0\">" +
            "<stop offset=\"0\" stop-color=\"red\"/>" +
            "<stop offset=\"1\" stop-color=\"blue\"/>" +
            "</linearGradient>" +
            "<linearGradient id=\"derived\" xlink:href=\"#base\" " +
            "x1=\"0\" y1=\"0\" x2=\"0\" y2=\"20\"/>" +
            "</defs>" +
            "<rect width=\"4\" height=\"20\" fill=\"url(#derived)\"/></svg>";
        var img = await LoadSvg(svg);
        var px = img.PixelsLazy!.Value;
        int iTop = (1 * 4 + 2) * 4;
        int iBottom = (18 * 4 + 2) * 4;
        Assert.True(px[iTop + 0] > 200, $"top R: {px[iTop + 0]}");
        Assert.True(px[iBottom + 2] > 200, $"bottom B: {px[iBottom + 2]}");
    }

    [Fact]
    public async Task DerivedWithOwnStops_IgnoresInheritedStops()
    {
        // Per spec, defining ANY stops on the derived gradient means it
        // uses ITS OWN stops exclusively — base stops are ignored. The
        // derived gradient here is green→yellow; the base's red→blue must
        // not leak through.
        const string svg =
            "<svg xmlns=\"http://www.w3.org/2000/svg\" " +
            "xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"20\" height=\"4\">" +
            "<defs>" +
            "<linearGradient id=\"base\" gradientUnits=\"userSpaceOnUse\" " +
            "x1=\"0\" y1=\"0\" x2=\"20\" y2=\"0\">" +
            "<stop offset=\"0\" stop-color=\"red\"/>" +
            "<stop offset=\"1\" stop-color=\"blue\"/>" +
            "</linearGradient>" +
            "<linearGradient id=\"derived\" xlink:href=\"#base\">" +
            "<stop offset=\"0\" stop-color=\"green\"/>" +
            "<stop offset=\"1\" stop-color=\"yellow\"/>" +
            "</linearGradient>" +
            "</defs>" +
            "<rect width=\"20\" height=\"4\" fill=\"url(#derived)\"/></svg>";
        var img = await LoadSvg(svg);
        var px = img.PixelsLazy!.Value;
        int iLeft = (2 * 20 + 1) * 4;
        // Left: green (#008000). Verify dominant G, low R and B.
        Assert.True(px[iLeft + 1] > 100, $"left G: {px[iLeft + 1]}");
        Assert.True(px[iLeft + 0] < 80,  $"left R: {px[iLeft + 0]}");
        Assert.True(px[iLeft + 2] < 80,  $"left B: {px[iLeft + 2]}");
    }

    [Fact]
    public async Task DerivedLinear_SupportsModernPlainHref()
    {
        // SVG 2 allows plain `href` (no xlink: prefix). Verify the chain
        // resolver finds the target with either spelling.
        const string svg =
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"20\" height=\"4\">" +
            "<defs>" +
            "<linearGradient id=\"base\">" +
            "<stop offset=\"0\" stop-color=\"red\"/>" +
            "<stop offset=\"1\" stop-color=\"blue\"/>" +
            "</linearGradient>" +
            "<linearGradient id=\"derived\" href=\"#base\" " +
            "gradientUnits=\"userSpaceOnUse\" x1=\"0\" y1=\"0\" x2=\"20\" y2=\"0\"/>" +
            "</defs>" +
            "<rect width=\"20\" height=\"4\" fill=\"url(#derived)\"/></svg>";
        var img = await LoadSvg(svg);
        var px = img.PixelsLazy!.Value;
        int iLeft = (2 * 20 + 1) * 4;
        int iRight = (2 * 20 + 18) * 4;
        Assert.True(px[iLeft + 0] > 200);
        Assert.True(px[iRight + 2] > 200);
    }

    [Fact]
    public async Task DerivedRadial_InheritsLinearBaseStops()
    {
        // Cross-type inheritance: a radial gradient referencing a linear
        // base. Per spec the referenced element supplies attributes + stops
        // even if it's a different gradient type; the referencing
        // element's type (radial here) determines layout.
        const string svg =
            "<svg xmlns=\"http://www.w3.org/2000/svg\" " +
            "xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"40\" height=\"40\">" +
            "<defs>" +
            "<linearGradient id=\"stops\">" +
            "<stop offset=\"0\" stop-color=\"red\"/>" +
            "<stop offset=\"1\" stop-color=\"blue\"/>" +
            "</linearGradient>" +
            "<radialGradient id=\"derived\" xlink:href=\"#stops\" " +
            "gradientUnits=\"userSpaceOnUse\" cx=\"20\" cy=\"20\" r=\"20\"/>" +
            "</defs>" +
            "<rect width=\"40\" height=\"40\" fill=\"url(#derived)\"/></svg>";
        var img = await LoadSvg(svg);
        var px = img.PixelsLazy!.Value;
        int iCenter = (20 * 40 + 20) * 4;
        int iEdge = (20 * 40 + 39) * 4;
        // Centre near red.
        Assert.True(px[iCenter + 0] > 150, $"centre R: {px[iCenter + 0]}");
        // Edge near blue.
        Assert.True(px[iEdge + 2] > 100, $"edge B: {px[iEdge + 2]}");
    }

    [Fact]
    public async Task ThreeLevelChain_ResolvesAttributesFromAcrossChain()
    {
        // A → B → C. A defines coords, B inherits and adds stops, C
        // inherits stops + coords from B (and thus A).
        const string svg =
            "<svg xmlns=\"http://www.w3.org/2000/svg\" " +
            "xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"20\" height=\"4\">" +
            "<defs>" +
            "<linearGradient id=\"A\" gradientUnits=\"userSpaceOnUse\" " +
            "x1=\"0\" y1=\"0\" x2=\"20\" y2=\"0\"/>" +
            "<linearGradient id=\"B\" xlink:href=\"#A\">" +
            "<stop offset=\"0\" stop-color=\"red\"/>" +
            "<stop offset=\"1\" stop-color=\"blue\"/>" +
            "</linearGradient>" +
            "<linearGradient id=\"C\" xlink:href=\"#B\"/>" +
            "</defs>" +
            "<rect width=\"20\" height=\"4\" fill=\"url(#C)\"/></svg>";
        var img = await LoadSvg(svg);
        var px = img.PixelsLazy!.Value;
        int iLeft = (2 * 20 + 1) * 4;
        int iRight = (2 * 20 + 18) * 4;
        Assert.True(px[iLeft + 0] > 200, $"left R: {px[iLeft + 0]}");
        Assert.True(px[iRight + 2] > 200, $"right B: {px[iRight + 2]}");
    }

    [Fact]
    public async Task CyclicChain_DoesNotInfiniteLoop()
    {
        // A and B point at each other. The chain resolver must break the
        // cycle and treat each gradient as if the other doesn't exist (so
        // their stops + coords come only from themselves). A defines
        // stops, B doesn't → B's resolved stop set is also red→blue via
        // the cycle-breaking pass.
        const string svg =
            "<svg xmlns=\"http://www.w3.org/2000/svg\" " +
            "xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"20\" height=\"4\">" +
            "<defs>" +
            "<linearGradient id=\"A\" xlink:href=\"#B\" " +
            "gradientUnits=\"userSpaceOnUse\" x1=\"0\" y1=\"0\" x2=\"20\" y2=\"0\">" +
            "<stop offset=\"0\" stop-color=\"red\"/>" +
            "<stop offset=\"1\" stop-color=\"blue\"/>" +
            "</linearGradient>" +
            "<linearGradient id=\"B\" xlink:href=\"#A\"/>" +
            "</defs>" +
            "<rect width=\"20\" height=\"4\" fill=\"url(#A)\"/></svg>";
        var img = await LoadSvg(svg);
        var px = img.PixelsLazy!.Value;
        int iLeft = (2 * 20 + 1) * 4;
        Assert.True(px[iLeft + 0] > 200, $"left R: {px[iLeft + 0]}");
    }
}
