using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Loaders;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Tests for the phase-2 SVG filter chain: in/result plumbing across
/// <c>feGaussianBlur</c>, <c>feOffset</c>, <c>feFlood</c>, and
/// <c>feMerge</c>/<c>feMergeNode</c> — enough to express the canonical
/// drop-shadow pattern.
/// </summary>
public class SvgFilterChainTests
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
    public async Task FeOffset_ShiftsPixelsByGivenDxDy()
    {
        // A 4-pixel-wide red dot at (4, 4) on a 20×20 canvas, with an
        // feOffset of (5, 5). The dot should appear shifted to (9, 9).
        const string svg =
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"20\" height=\"20\">" +
            "<defs><filter id=\"f\" x=\"-50%\" y=\"-50%\" width=\"200%\" height=\"200%\">" +
            "<feOffset dx=\"5\" dy=\"5\"/></filter></defs>" +
            "<rect x=\"4\" y=\"4\" width=\"4\" height=\"4\" fill=\"red\" filter=\"url(#f)\"/></svg>";
        var img = await LoadSvg(svg);
        var px = img.PixelsLazy!.Value;
        // After shift, the dot should be visible around (9..12, 9..12).
        int iShifted = (10 * 20 + 10) * 4;
        Assert.True(px[iShifted + 0] > 200, $"shifted R: {px[iShifted + 0]}");
        // Original position (5, 5) should now be empty.
        int iOriginal = (5 * 20 + 5) * 4;
        Assert.Equal(0, px[iOriginal + 3]);
    }

    [Fact]
    public async Task FeFlood_FillsCanvasWithSolidColor()
    {
        // Standalone feFlood with cyan fills the entire canvas — confirms
        // the primitive produces a flat output buffer in the chain.
        const string svg =
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\">" +
            "<defs><filter id=\"f\">" +
            "<feFlood flood-color=\"#00FFFF\"/>" +
            "</filter></defs>" +
            // Any non-empty source — the filter discards SourceGraphic and
            // outputs the flood as the final result.
            "<rect width=\"10\" height=\"10\" fill=\"red\" filter=\"url(#f)\"/></svg>";
        var img = await LoadSvg(svg);
        var px = img.PixelsLazy!.Value;
        int i = (5 * 10 + 5) * 4;
        Assert.Equal(0,   px[i + 0]);
        Assert.Equal(255, px[i + 1]);
        Assert.Equal(255, px[i + 2]);
    }

    [Fact]
    public async Task FeFlood_RespectsFloodOpacity()
    {
        // flood-opacity = 0.5 → output alpha ≈ 128 at every pixel.
        const string svg =
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\">" +
            "<defs><filter id=\"f\">" +
            "<feFlood flood-color=\"black\" flood-opacity=\"0.5\"/>" +
            "</filter></defs>" +
            "<rect width=\"10\" height=\"10\" fill=\"red\" filter=\"url(#f)\"/></svg>";
        var img = await LoadSvg(svg);
        var px = img.PixelsLazy!.Value;
        int i = (5 * 10 + 5) * 4;
        Assert.InRange(px[i + 3], 100, 156);
    }

    [Fact]
    public async Task FeMerge_StacksLayersBottomToTop()
    {
        // Two named results — a red flood and a blue flood — merged with
        // blue on top. Expected: pure blue (top fully covers).
        const string svg =
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\">" +
            "<defs><filter id=\"f\">" +
            "<feFlood flood-color=\"red\" result=\"r\"/>" +
            "<feFlood flood-color=\"blue\" result=\"b\"/>" +
            "<feMerge>" +
            "<feMergeNode in=\"r\"/>" +
            "<feMergeNode in=\"b\"/>" +
            "</feMerge>" +
            "</filter></defs>" +
            "<rect width=\"10\" height=\"10\" fill=\"white\" filter=\"url(#f)\"/></svg>";
        var img = await LoadSvg(svg);
        var px = img.PixelsLazy!.Value;
        int i = (5 * 10 + 5) * 4;
        Assert.True(px[i + 2] > 200, $"top-of-stack B: {px[i + 2]}");
        Assert.True(px[i + 0] < 60, $"bottom-red should not show: {px[i + 0]}");
    }

    [Fact]
    public async Task DropShadow_BlurOffsetMergeWithSourceGraphic()
    {
        // Canonical drop-shadow pattern: blur SourceAlpha → offset → merge
        // shadow under the original.
        //
        // Layout: a 6×6 red square at (10, 10) on a 40×40 canvas. Shadow
        // offset is (4, 4), blur σ = 2. Verify:
        //   1. The original red square is still opaque at (12, 12).
        //   2. The empty corner at (3, 3) is still fully transparent.
        //   3. A blurred-shadow region around (16, 16) has darkened alpha
        //      from the SourceAlpha blur (= grayish/translucent black).
        const string svg =
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<defs><filter id=\"ds\" x=\"-50%\" y=\"-50%\" width=\"200%\" height=\"200%\">" +
            "<feGaussianBlur in=\"SourceAlpha\" stdDeviation=\"2\" result=\"blur\"/>" +
            "<feOffset in=\"blur\" dx=\"4\" dy=\"4\" result=\"shadow\"/>" +
            "<feMerge>" +
            "<feMergeNode in=\"shadow\"/>" +
            "<feMergeNode in=\"SourceGraphic\"/>" +
            "</feMerge>" +
            "</filter></defs>" +
            "<rect x=\"10\" y=\"10\" width=\"6\" height=\"6\" fill=\"red\" filter=\"url(#ds)\"/></svg>";
        var img = await LoadSvg(svg);
        var px = img.PixelsLazy!.Value;

        // Original square interior: opaque red.
        int iSquare = (12 * 40 + 12) * 4;
        Assert.True(px[iSquare + 0] > 200, $"square R: {px[iSquare + 0]}");
        Assert.Equal(255, px[iSquare + 3]);

        // Empty corner: transparent.
        int iEmpty = (3 * 40 + 3) * 4;
        Assert.Equal(0, px[iEmpty + 3]);

        // Shadow region (offset 4,4 + blur σ=2 around (10..16) puts the
        // shadow centred around (14..20) — sample (19, 19) for a likely
        // shadow pixel). Should have some alpha but no red.
        int iShadow = (19 * 40 + 19) * 4;
        Assert.True(px[iShadow + 3] > 10, $"shadow alpha: {px[iShadow + 3]}");
        Assert.True(px[iShadow + 0] < 64, $"shadow R should be near-black: {px[iShadow + 0]}");
    }

    [Fact]
    public async Task FilterChain_InAttributeDefaultsToPreviousResult()
    {
        // Two-stage chain without explicit `in` attrs: blur then offset.
        // The second primitive's input defaults to the first's output.
        // Verifies the lastResult fallback in the in-resolver.
        const string svg =
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"30\" height=\"30\">" +
            "<defs><filter id=\"f\" x=\"-50%\" y=\"-50%\" width=\"200%\" height=\"200%\">" +
            "<feGaussianBlur stdDeviation=\"1\"/>" +
            "<feOffset dx=\"6\" dy=\"0\"/>" +
            "</filter></defs>" +
            "<rect x=\"5\" y=\"12\" width=\"4\" height=\"4\" fill=\"red\" filter=\"url(#f)\"/></svg>";
        var img = await LoadSvg(svg);
        var px = img.PixelsLazy!.Value;
        // Expect the (blurred) square to appear shifted +6 to the right —
        // around x=11..15. Sample (12, 13).
        int iShifted = (13 * 30 + 12) * 4;
        Assert.True(px[iShifted + 0] > 100, $"shifted R: {px[iShifted + 0]}");
        // Original x=6 should now be empty (offset moved everything).
        int iOriginal = (13 * 30 + 6) * 4;
        Assert.True(px[iOriginal + 3] < 64, $"original alpha should be near-empty: {px[iOriginal + 3]}");
    }
}
