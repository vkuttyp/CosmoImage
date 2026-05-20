using System;
using System.IO.Pipelines;
using System.Threading.Tasks;
using Xunit;

namespace CosmoImage.Tests;

public class SvgLoaderTests
{
    [Fact]
    public async Task LoadAsync_ValidSvg_RendersImage()
    {
        // Arrange: Minimal valid SVG
        string svg = "<svg width=\"100\" height=\"100\"><rect width=\"100\" height=\"100\" fill=\"red\"/></svg>";
        byte[] svgBytes = System.Text.Encoding.ASCII.GetBytes(svg);

        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(svgBytes);
        await pipe.Writer.CompleteAsync();

        await using var source = new PipeVipsSource(pipe.Reader);

        // Act
        var image = await VipsSvgLoader.LoadAsync(source);

        // Assert
        Assert.NotNull(image);
        Assert.Equal(100, image.Width);
        Assert.Equal(100, image.Height);
        Assert.Equal(4, image.Bands); // RGBA

        using var outRegion = new VipsRegion(image);
        outRegion.Prepare(new VipsRect(0, 0, 1, 1));
        var pixel = outRegion.GetAddress(0, 0);
        // Pure red should be (255, 0, 0, 255)
        Assert.Equal(255, pixel[0]);
        Assert.Equal(0, pixel[1]);
        Assert.Equal(0, pixel[2]);
        Assert.Equal(255, pixel[3]);
    }

    [Fact]
    public async Task IsSvgAsync_ValidSvg_ReturnsTrue()
    {
        // Arrange
        byte[] svgBytes = System.Text.Encoding.ASCII.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(svgBytes);
        await pipe.Writer.CompleteAsync();

        await using var source = new PipeVipsSource(pipe.Reader);

        // Act
        bool isSvg = await VipsSvgLoader.IsSvgAsync(source);

        // Assert
        Assert.True(isSvg);
    }

    [Fact]
    public async Task LoadAsync_TransparentBackgroundOutsideShape()
    {
        // 50×50 SVG with a 20×20 blue rect at (10,10). Pixel (5,5) should be
        // transparent (SVG default), not white or black.
        const string svg = "<svg width=\"50\" height=\"50\"><rect x=\"10\" y=\"10\" width=\"20\" height=\"20\" fill=\"blue\"/></svg>";
        var img = await LoadSvg(svg);

        var bytes = img.PixelsLazy!.Value;
        int OffsetOf(int x, int y) => (y * 50 + x) * 4;

        // (5,5) outside the rect → transparent.
        Assert.Equal(0, bytes[OffsetOf(5, 5) + 3]);
        // (20,20) inside the rect → opaque blue.
        Assert.Equal(0,   bytes[OffsetOf(20, 20) + 0]);
        Assert.Equal(0,   bytes[OffsetOf(20, 20) + 1]);
        Assert.Equal(255, bytes[OffsetOf(20, 20) + 2]);
        Assert.Equal(255, bytes[OffsetOf(20, 20) + 3]);
    }

    [Fact]
    public async Task LoadAsync_Circle_FillsCenter()
    {
        const string svg = "<svg width=\"60\" height=\"60\"><circle cx=\"30\" cy=\"30\" r=\"20\" fill=\"#00FF00\"/></svg>";
        var img = await LoadSvg(svg);
        var bytes = img.PixelsLazy!.Value;
        int idx = (30 * 60 + 30) * 4;
        Assert.Equal(0,   bytes[idx + 0]);
        Assert.Equal(255, bytes[idx + 1]);
        Assert.Equal(0,   bytes[idx + 2]);
        Assert.Equal(255, bytes[idx + 3]);
        // Corner should remain transparent.
        Assert.Equal(0, bytes[(0 * 60 + 0) * 4 + 3]);
    }

    [Fact]
    public async Task LoadAsync_Polygon_RgbColor()
    {
        // Triangle covering pixel (5, 8). rgb() syntax + 0..255 components.
        const string svg = "<svg width=\"20\" height=\"20\"><polygon points=\"0,0 20,0 10,20\" fill=\"rgb(128, 64, 200)\"/></svg>";
        var img = await LoadSvg(svg);
        var bytes = img.PixelsLazy!.Value;
        int idx = (8 * 20 + 5) * 4;
        Assert.Equal(128, bytes[idx + 0]);
        Assert.Equal(64,  bytes[idx + 1]);
        Assert.Equal(200, bytes[idx + 2]);
        Assert.Equal(255, bytes[idx + 3]);
    }

    [Fact]
    public async Task LoadAsync_Group_InheritsFill()
    {
        // <g fill="red"> with a <rect> that doesn't specify fill: inherits red.
        const string svg = "<svg width=\"10\" height=\"10\"><g fill=\"red\"><rect width=\"10\" height=\"10\"/></g></svg>";
        var img = await LoadSvg(svg);
        var bytes = img.PixelsLazy!.Value;
        Assert.Equal(255, bytes[0]);   // R
        Assert.Equal(0,   bytes[1]);   // G
        Assert.Equal(0,   bytes[2]);   // B
        Assert.Equal(255, bytes[3]);   // A
    }

    [Fact]
    public async Task LoadAsync_ViewBox_AppliesToCoordinates()
    {
        // viewBox 0 0 200 200, intrinsic 100x100. A rect at (100,100) of size
        // 100×100 in SVG coords lands at (50,50)-(100,100) in pixels.
        const string svg = "<svg width=\"100\" height=\"100\" viewBox=\"0 0 200 200\">" +
                           "<rect x=\"100\" y=\"100\" width=\"100\" height=\"100\" fill=\"black\"/></svg>";
        var img = await LoadSvg(svg);
        var bytes = img.PixelsLazy!.Value;
        // (25,25): outside (viewBox-mapped) → transparent.
        Assert.Equal(0, bytes[(25 * 100 + 25) * 4 + 3]);
        // (75,75): inside → opaque black.
        int idx = (75 * 100 + 75) * 4;
        Assert.Equal(0,   bytes[idx + 0]);
        Assert.Equal(0,   bytes[idx + 1]);
        Assert.Equal(0,   bytes[idx + 2]);
        Assert.Equal(255, bytes[idx + 3]);
    }

    [Fact]
    public async Task LoadAsync_HexShorthand_ParsedCorrectly()
    {
        // #f00 == #ff0000 == red.
        const string svg = "<svg width=\"4\" height=\"4\"><rect width=\"4\" height=\"4\" fill=\"#f00\"/></svg>";
        var img = await LoadSvg(svg);
        var bytes = img.PixelsLazy!.Value;
        Assert.Equal(255, bytes[0]);
        Assert.Equal(0,   bytes[1]);
        Assert.Equal(0,   bytes[2]);
    }

    [Fact]
    public async Task LoadAsync_Path_TriangleFillsCenter()
    {
        // Triangle (0,0)-(20,0)-(10,20) — pixel (10, 10) is inside.
        const string svg = "<svg width=\"20\" height=\"20\"><path d=\"M 0 0 L 20 0 L 10 20 Z\" fill=\"red\"/></svg>";
        var img = await LoadSvg(svg);
        var bytes = img.PixelsLazy!.Value;
        int idx = (10 * 20 + 10) * 4;
        Assert.Equal(255, bytes[idx + 0]);
        Assert.Equal(0,   bytes[idx + 1]);
        Assert.Equal(0,   bytes[idx + 2]);
        Assert.Equal(255, bytes[idx + 3]);
        // Bottom-left corner — far from the apex at (10, 20) — is outside.
        Assert.Equal(0, bytes[(19 * 20 + 0) * 4 + 3]);
    }

    [Fact]
    public async Task LoadAsync_Path_RelativeCommandsAndImplicitLineto()
    {
        // "M 5 5 l 10 0 l 0 10 l -10 0 z" — relative L's drawing a 10×10
        // square starting at (5,5). Implicit close.
        const string svg = "<svg width=\"20\" height=\"20\"><path d=\"M 5 5 l 10 0 l 0 10 l -10 0 z\" fill=\"blue\"/></svg>";
        var img = await LoadSvg(svg);
        var bytes = img.PixelsLazy!.Value;
        int idx = (10 * 20 + 10) * 4;
        Assert.Equal(0,   bytes[idx + 0]);
        Assert.Equal(0,   bytes[idx + 1]);
        Assert.Equal(255, bytes[idx + 2]);
    }

    [Fact]
    public async Task LoadAsync_Path_HVCommands()
    {
        // H/V draw axis-aligned segments. Square from (0,0) via H10, V10, H0, Z.
        const string svg = "<svg width=\"10\" height=\"10\"><path d=\"M 0 0 H 10 V 10 H 0 Z\" fill=\"green\"/></svg>";
        var img = await LoadSvg(svg);
        var bytes = img.PixelsLazy!.Value;
        Assert.Equal(255, bytes[(5 * 10 + 5) * 4 + 3]);  // center opaque
    }

    [Fact]
    public async Task LoadAsync_Path_CubicBezier()
    {
        // A wide, gentle cubic across a 40×20 canvas, filled. The exact
        // center (20, 10) is inside the curve loop closing back to start.
        const string svg = "<svg width=\"40\" height=\"20\">" +
                           "<path d=\"M 0 20 C 10 -10 30 -10 40 20 L 40 20 L 0 20 Z\" fill=\"black\"/></svg>";
        var img = await LoadSvg(svg);
        var bytes = img.PixelsLazy!.Value;
        // (20, 18) — definitely inside the curve.
        int idx = (18 * 40 + 20) * 4;
        Assert.Equal(255, bytes[idx + 3]);
    }

    [Fact]
    public async Task LoadAsync_Path_EvenOddRule_RingHasHole()
    {
        // Outer 40×40 square + inner 20×20 square (offset by 10). With the
        // default even-odd fill rule, the inner square is a hole — fully
        // transparent — while the ring is opaque.
        const string svg = "<svg width=\"40\" height=\"40\">" +
                           "<path d=\"M 0 0 H 40 V 40 H 0 Z M 10 10 H 30 V 30 H 10 Z\" fill=\"orange\"/></svg>";
        var img = await LoadSvg(svg);
        var bytes = img.PixelsLazy!.Value;
        // Inside the ring (between the two squares): opaque.
        Assert.Equal(255, bytes[(5 * 40 + 5) * 4 + 3]);
        // Inside the inner hole: transparent.
        Assert.Equal(0, bytes[(20 * 40 + 20) * 4 + 3]);
    }

    [Fact]
    public async Task LoadAsync_Path_Arc()
    {
        // Quarter-circle arc from (10,0) sweeping clockwise to (0,10),
        // closed back to (0,0) — a pie-slice. Center pixel (3,3) should
        // be inside.
        const string svg = "<svg width=\"12\" height=\"12\">" +
                           "<path d=\"M 10 0 A 10 10 0 0 0 0 10 L 0 0 Z\" fill=\"magenta\"/></svg>";
        var img = await LoadSvg(svg);
        var bytes = img.PixelsLazy!.Value;
        int idx = (3 * 12 + 3) * 4;
        Assert.Equal(255, bytes[idx + 0]);
        Assert.Equal(0,   bytes[idx + 1]);
        Assert.Equal(255, bytes[idx + 2]);
        Assert.Equal(255, bytes[idx + 3]);
    }

    [Fact]
    public async Task LoadAsync_Path_TightlyPackedNumbers()
    {
        // SVG path data in the wild often packs numbers with no separators:
        // "M0 0L10,10-5-5Z" should parse as M(0,0) L(10,10) L(-5,-5) Z.
        const string svg = "<svg width=\"20\" height=\"20\"><path d=\"M0 0L10 10 0 10Z\" fill=\"black\"/></svg>";
        var img = await LoadSvg(svg);
        var bytes = img.PixelsLazy!.Value;
        // Triangle (0,0)-(10,10)-(0,10) — (3, 5) is inside.
        Assert.Equal(255, bytes[(5 * 20 + 3) * 4 + 3]);
    }

    [Fact]
    public async Task LoadAsync_Transform_Translate_ShiftsShape()
    {
        // 4x4 red rect inside a 16x16 canvas, translated by (8, 4).
        // After transform it covers x∈[8,12] y∈[4,8]. Sample at (10, 6) → red.
        const string svg = "<svg width=\"16\" height=\"16\">" +
                           "<rect width=\"4\" height=\"4\" fill=\"red\" transform=\"translate(8 4)\"/></svg>";
        var img = await LoadSvg(svg);
        var bytes = img.PixelsLazy!.Value;
        Assert.Equal(255, bytes[(6 * 16 + 10) * 4 + 0]);  // R
        // Original position (0,0) is now empty.
        Assert.Equal(0, bytes[(0 * 16 + 0) * 4 + 3]);
    }

    [Fact]
    public async Task LoadAsync_Transform_Scale_GrowsShape()
    {
        // 4x4 rect scaled 2× → covers 8x8. Sample at (7, 7) which is inside
        // the scaled rect, originally outside.
        const string svg = "<svg width=\"16\" height=\"16\">" +
                           "<rect width=\"4\" height=\"4\" fill=\"blue\" transform=\"scale(2)\"/></svg>";
        var img = await LoadSvg(svg);
        var bytes = img.PixelsLazy!.Value;
        Assert.Equal(255, bytes[(7 * 16 + 7) * 4 + 2]);  // B
    }

    [Fact]
    public async Task LoadAsync_Transform_RotateAroundCenter_PutsShapeAtRightPlace()
    {
        // 4x4 rect at (8,2) rotated 90° around (10, 10). The rect (8..12, 2..6)
        // rotates to roughly (18..14 in y after CW 90°) = becomes the band
        // (14..18, 8..12) — outside the 20x20 canvas? Let's pick a more
        // testable rotation: 180° around the center of the canvas.
        // Original rect (2,2)-(6,6); 180° rot around (10,10) puts it at
        // (14,14)-(18,18). Sample (16, 16) → filled, (4, 4) → empty.
        const string svg = "<svg width=\"20\" height=\"20\">" +
                           "<rect x=\"2\" y=\"2\" width=\"4\" height=\"4\" fill=\"green\" " +
                           "transform=\"rotate(180 10 10)\"/></svg>";
        var img = await LoadSvg(svg);
        var bytes = img.PixelsLazy!.Value;
        Assert.Equal(255, bytes[(16 * 20 + 16) * 4 + 3]);  // alpha
        Assert.Equal(0,   bytes[(4 * 20 + 4) * 4 + 3]);    // original spot empty
    }

    [Fact]
    public async Task LoadAsync_Transform_ChainedLeftToRight()
    {
        // transform="translate(10 0) scale(2)" applied to a 2x2 rect at origin:
        // SVG semantics: rightmost applied first to the point. So scale first
        // (rect becomes 4x4), then translate by (10, 0) → covers (10,0)-(14,4).
        const string svg = "<svg width=\"20\" height=\"10\">" +
                           "<rect width=\"2\" height=\"2\" fill=\"black\" transform=\"translate(10 0) scale(2)\"/></svg>";
        var img = await LoadSvg(svg);
        var bytes = img.PixelsLazy!.Value;
        Assert.Equal(255, bytes[(2 * 20 + 12) * 4 + 3]);  // (12, 2) filled
        Assert.Equal(0,   bytes[(2 * 20 + 5) * 4 + 3]);   // (5, 2) empty
    }

    [Fact]
    public async Task LoadAsync_Transform_GroupNesting_Composes()
    {
        // <g translate(5)><g translate(5)><rect>></g></g> = translate(10) total.
        const string svg = "<svg width=\"20\" height=\"20\">" +
                           "<g transform=\"translate(5 0)\"><g transform=\"translate(5 0)\">" +
                           "<rect width=\"2\" height=\"2\" fill=\"red\"/></g></g></svg>";
        var img = await LoadSvg(svg);
        var bytes = img.PixelsLazy!.Value;
        Assert.Equal(255, bytes[(1 * 20 + 11) * 4 + 0]);  // R at (11, 1)
        Assert.Equal(0,   bytes[(1 * 20 + 1) * 4 + 3]);   // original origin empty
    }

    [Fact]
    public async Task LoadAsync_Transform_MatrixForm()
    {
        // matrix(1 0 0 1 4 6) is identity + translate(4, 6).
        const string svg = "<svg width=\"16\" height=\"16\">" +
                           "<rect width=\"2\" height=\"2\" fill=\"black\" transform=\"matrix(1 0 0 1 4 6)\"/></svg>";
        var img = await LoadSvg(svg);
        var bytes = img.PixelsLazy!.Value;
        Assert.Equal(255, bytes[(7 * 16 + 5) * 4 + 3]);  // (5, 7) filled (inside 4..6 × 6..8)
    }

    [Fact]
    public async Task LoadAsync_Transform_PathRespectsCtm()
    {
        // Triangle path translated by (5, 5). Original vertex (0,0) lands at
        // (5,5); apex (10,20) at (15,25) which is outside the 16x16 canvas —
        // that's fine, the rasterizer clips. Sample inside the translated
        // triangle's bottom edge.
        const string svg = "<svg width=\"16\" height=\"16\">" +
                           "<path d=\"M 0 0 L 10 0 L 5 10 Z\" fill=\"red\" transform=\"translate(3 3)\"/></svg>";
        var img = await LoadSvg(svg);
        var bytes = img.PixelsLazy!.Value;
        // Center of the translated triangle is roughly (8, 6).
        Assert.Equal(255, bytes[(6 * 16 + 8) * 4 + 0]);
        // Original origin (1,1) outside the translated shape.
        Assert.Equal(0, bytes[(1 * 16 + 1) * 4 + 3]);
    }

    [Fact]
    public async Task LoadAsync_LinearGradient_HorizontalRedToBlue()
    {
        // Linear gradient from red at x=0 to blue at x=20 across a 20x4 rect.
        // Sample pixel (1, 2): mostly red. Pixel (18, 2): mostly blue.
        // Pixel (10, 2): roughly halfway → purple-ish (≈ 128, 0, 128).
        const string svg =
            "<svg width=\"20\" height=\"4\">" +
            "<defs><linearGradient id=\"g\" gradientUnits=\"userSpaceOnUse\" x1=\"0\" y1=\"0\" x2=\"20\" y2=\"0\">" +
            "<stop offset=\"0\" stop-color=\"red\"/>" +
            "<stop offset=\"1\" stop-color=\"blue\"/>" +
            "</linearGradient></defs>" +
            "<rect width=\"20\" height=\"4\" fill=\"url(#g)\"/></svg>";
        var img = await LoadSvg(svg);
        var bytes = img.PixelsLazy!.Value;

        // Far-left pixel: should be (close to) pure red.
        Assert.True(bytes[(2 * 20 + 1) * 4 + 0] > 200, $"left R too low: {bytes[(2 * 20 + 1) * 4 + 0]}");
        Assert.True(bytes[(2 * 20 + 1) * 4 + 2] < 60,  $"left B too high: {bytes[(2 * 20 + 1) * 4 + 2]}");

        // Far-right pixel: (close to) pure blue.
        Assert.True(bytes[(2 * 20 + 18) * 4 + 0] < 60,  $"right R too high: {bytes[(2 * 20 + 18) * 4 + 0]}");
        Assert.True(bytes[(2 * 20 + 18) * 4 + 2] > 200, $"right B too low: {bytes[(2 * 20 + 18) * 4 + 2]}");

        // Middle pixel: R ≈ B, both meaningfully > 0 (mixed).
        int rMid = bytes[(2 * 20 + 10) * 4 + 0];
        int bMid = bytes[(2 * 20 + 10) * 4 + 2];
        Assert.True(rMid > 50 && bMid > 50, $"middle insufficiently mixed: R={rMid} B={bMid}");
        Assert.True(Math.Abs(rMid - bMid) < 60, $"middle imbalanced: R={rMid} B={bMid}");
    }

    [Fact]
    public async Task LoadAsync_LinearGradient_VerticalDirection()
    {
        // Vertical gradient: x1=y1=x2=0, y2=20 — red at top, blue at bottom.
        const string svg =
            "<svg width=\"4\" height=\"20\">" +
            "<defs><linearGradient id=\"g\" gradientUnits=\"userSpaceOnUse\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"20\">" +
            "<stop offset=\"0\" stop-color=\"red\"/>" +
            "<stop offset=\"1\" stop-color=\"blue\"/>" +
            "</linearGradient></defs>" +
            "<rect width=\"4\" height=\"20\" fill=\"url(#g)\"/></svg>";
        var img = await LoadSvg(svg);
        var bytes = img.PixelsLazy!.Value;
        // Top pixel red.
        Assert.True(bytes[(1 * 4 + 2) * 4 + 0] > 200);
        // Bottom pixel blue.
        Assert.True(bytes[(18 * 4 + 2) * 4 + 2] > 200);
    }

    [Fact]
    public async Task LoadAsync_LinearGradient_PercentageStopOffsets()
    {
        // stop offsets given as percentages, with stop-opacity halving the
        // start color's alpha.
        const string svg =
            "<svg width=\"20\" height=\"4\">" +
            "<defs><linearGradient id=\"g\" gradientUnits=\"userSpaceOnUse\" x1=\"0\" y1=\"0\" x2=\"20\" y2=\"0\">" +
            "<stop offset=\"0%\" stop-color=\"#FF0000\" stop-opacity=\"0.5\"/>" +
            "<stop offset=\"100%\" stop-color=\"#0000FF\"/>" +
            "</linearGradient></defs>" +
            "<rect width=\"20\" height=\"4\" fill=\"url(#g)\"/></svg>";
        var img = await LoadSvg(svg);
        var bytes = img.PixelsLazy!.Value;
        // Far-left pixel: stop-opacity 0.5 → alpha ≈ 127 (when composited over
        // transparent background, the output alpha = 127).
        Assert.InRange(bytes[(2 * 20 + 0) * 4 + 3], 100, 160);
    }

    [Fact]
    public async Task LoadAsync_LinearGradient_StrokeOnLine_RendersThroughSampler()
    {
        // Horizontal red→blue gradient stroke on a horizontal line. Sampling
        // the left end of the stroke should be red-leaning, the right end
        // blue-leaning. See SvgStrokeGradientTests for the full coverage —
        // this case used to throw under the phase-4 first cut.
        const string svg =
            "<svg width=\"20\" height=\"20\">" +
            "<defs><linearGradient id=\"g\" gradientUnits=\"userSpaceOnUse\" x1=\"0\" y1=\"0\" x2=\"20\" y2=\"0\">" +
            "<stop offset=\"0\" stop-color=\"red\"/><stop offset=\"1\" stop-color=\"blue\"/>" +
            "</linearGradient></defs>" +
            "<line x1=\"0\" y1=\"10\" x2=\"20\" y2=\"10\" stroke=\"url(#g)\" stroke-width=\"4\"/></svg>";
        var img = await LoadSvg(svg);
        var px = img.PixelsLazy!.Value;
        int iLeft = (10 * 20 + 1) * 4;
        int iRight = (10 * 20 + 18) * 4;
        Assert.True(px[iLeft + 0] > px[iLeft + 2] + 40,
            $"left R={px[iLeft + 0]} B={px[iLeft + 2]}");
        Assert.True(px[iRight + 2] > px[iRight + 0] + 40,
            $"right R={px[iRight + 0]} B={px[iRight + 2]}");
    }

    [Fact]
    public async Task LoadAsync_LinearGradient_PathFill_GradientAcrossTriangle()
    {
        const string svg =
            "<svg width=\"20\" height=\"20\">" +
            "<defs><linearGradient id=\"g\" gradientUnits=\"userSpaceOnUse\" x1=\"0\" y1=\"0\" x2=\"20\" y2=\"0\">" +
            "<stop offset=\"0\" stop-color=\"red\"/>" +
            "<stop offset=\"1\" stop-color=\"green\"/>" +
            "</linearGradient></defs>" +
            "<path d=\"M 0 0 L 20 0 L 10 20 Z\" fill=\"url(#g)\"/></svg>";
        var img = await LoadSvg(svg);
        var bytes = img.PixelsLazy!.Value;
        // Inside the triangle at left side (3, 1) — predominantly red.
        Assert.True(bytes[(1 * 20 + 3) * 4 + 0] > 150);
        // Right side (17, 1) — predominantly green.
        Assert.True(bytes[(1 * 20 + 17) * 4 + 1] > 60);
        Assert.True(bytes[(1 * 20 + 17) * 4 + 0] < 100);
    }

    [Fact]
    public async Task LoadAsync_ClipPath_RestrictsRectFill()
    {
        // 20×20 canvas. Red rect covers the whole canvas; clipPath defines a
        // 10×10 region at (5,5). Only pixels inside the clip get filled.
        const string svg =
            "<svg width=\"20\" height=\"20\">" +
            "<defs><clipPath id=\"c\"><rect x=\"5\" y=\"5\" width=\"10\" height=\"10\"/></clipPath></defs>" +
            "<rect width=\"20\" height=\"20\" fill=\"red\" clip-path=\"url(#c)\"/></svg>";
        var img = await LoadSvg(svg);
        var bytes = img.PixelsLazy!.Value;

        // Inside the clip — red.
        Assert.Equal(255, bytes[(10 * 20 + 10) * 4 + 0]);
        Assert.Equal(255, bytes[(10 * 20 + 10) * 4 + 3]);
        // Outside the clip — transparent (the rest of the rect is clipped out).
        Assert.Equal(0, bytes[(0 * 20 + 0) * 4 + 3]);
        Assert.Equal(0, bytes[(2 * 20 + 2) * 4 + 3]);
        // On the clip boundary (just inside) — red.
        Assert.Equal(255, bytes[(6 * 20 + 6) * 4 + 0]);
    }

    [Fact]
    public async Task LoadAsync_ClipPath_CircleClipsBackground()
    {
        // Clip path with a circle. Renders a square rect, but only the
        // disc shape ends up filled.
        const string svg =
            "<svg width=\"20\" height=\"20\">" +
            "<defs><clipPath id=\"c\"><circle cx=\"10\" cy=\"10\" r=\"5\"/></clipPath></defs>" +
            "<rect width=\"20\" height=\"20\" fill=\"blue\" clip-path=\"url(#c)\"/></svg>";
        var img = await LoadSvg(svg);
        var bytes = img.PixelsLazy!.Value;
        // Center of the disc — blue.
        Assert.Equal(255, bytes[(10 * 20 + 10) * 4 + 2]);
        // Far corner — outside the disc — transparent.
        Assert.Equal(0, bytes[(0 * 20 + 0) * 4 + 3]);
    }

    [Fact]
    public async Task LoadAsync_ClipPath_OnGroup_AppliesToAllChildren()
    {
        // <g clip-path="..."> wraps two rects. Both should be clipped.
        const string svg =
            "<svg width=\"20\" height=\"20\">" +
            "<defs><clipPath id=\"c\"><rect x=\"0\" y=\"0\" width=\"10\" height=\"20\"/></clipPath></defs>" +
            "<g clip-path=\"url(#c)\">" +
            "<rect x=\"0\" y=\"0\" width=\"20\" height=\"10\" fill=\"red\"/>" +
            "<rect x=\"0\" y=\"10\" width=\"20\" height=\"10\" fill=\"blue\"/>" +
            "</g></svg>";
        var img = await LoadSvg(svg);
        var bytes = img.PixelsLazy!.Value;
        // Inside left half: red top, blue bottom.
        Assert.Equal(255, bytes[(5 * 20 + 5) * 4 + 0]);     // red, top
        Assert.Equal(255, bytes[(15 * 20 + 5) * 4 + 2]);    // blue, bottom
        // Outside left half (clip excludes it): transparent.
        Assert.Equal(0, bytes[(5 * 20 + 15) * 4 + 3]);
        Assert.Equal(0, bytes[(15 * 20 + 15) * 4 + 3]);
    }

    [Fact]
    public async Task LoadAsync_ClipPath_UnresolvedRef_RendersUnclipped()
    {
        // url() pointing at a missing clipPath def: the spec says to treat
        // as no clip. We fall back to rendering without restriction.
        const string svg =
            "<svg width=\"4\" height=\"4\">" +
            "<rect width=\"4\" height=\"4\" fill=\"red\" clip-path=\"url(#nope)\"/></svg>";
        var img = await LoadSvg(svg);
        // Whole canvas filled.
        Assert.Equal(255, img.PixelsLazy!.Value[0]);
    }

    [Fact]
    public async Task LoadAsync_Filter_GaussianBlur_BleedsBeyondShape()
    {
        // 40×40 canvas, sharp 20×20 red rect centered. With stdDeviation=4
        // the blur kernel radius is ~12 pixels — pixels several pixels
        // outside the original rect bounds should be non-transparent due
        // to the blur leaking out, while the center should still be mostly red.
        const string svg =
            "<svg width=\"40\" height=\"40\">" +
            "<defs><filter id=\"blur\"><feGaussianBlur stdDeviation=\"4\"/></filter></defs>" +
            "<rect x=\"10\" y=\"10\" width=\"20\" height=\"20\" fill=\"red\" filter=\"url(#blur)\"/>" +
            "</svg>";
        var img = await LoadSvg(svg);
        var bytes = img.PixelsLazy!.Value;

        // Center of the original rect: still mostly red (some softening at
        // the center due to averaging in from edges, but R should dominate).
        int rCenter = bytes[(20 * 40 + 20) * 4 + 0];
        int aCenter = bytes[(20 * 40 + 20) * 4 + 3];
        Assert.True(rCenter > 200, $"center R={rCenter} should still be mostly red");
        Assert.True(aCenter > 200, $"center A={aCenter}");

        // A pixel a few px outside the original rect (was transparent before
        // blur): should now have non-zero alpha thanks to the bleed.
        int aOutside = bytes[(5 * 40 + 20) * 4 + 3];  // 5px above the rect, centered
        Assert.True(aOutside > 0 && aOutside < 100,
            $"outside-rect alpha should be partial: A={aOutside}");

        // Corner of the canvas (far from the rect): still transparent.
        Assert.Equal(0, bytes[(0 * 40 + 0) * 4 + 3]);
    }

    [Fact]
    public async Task LoadAsync_Filter_GaussianBlur_PerAxisStdDeviation()
    {
        // stdDeviation="6 0" — blur on x axis only. Horizontal bleed should
        // be substantial; vertical bleed should be ~0.
        const string svg =
            "<svg width=\"40\" height=\"40\">" +
            "<defs><filter id=\"blur\"><feGaussianBlur stdDeviation=\"6 0\"/></filter></defs>" +
            "<rect x=\"15\" y=\"15\" width=\"10\" height=\"10\" fill=\"black\" filter=\"url(#blur)\"/>" +
            "</svg>";
        var img = await LoadSvg(svg);
        var bytes = img.PixelsLazy!.Value;

        // Pixel directly left of the rect at the same y row → blurred
        // outward horizontally → non-zero alpha.
        int aLeft = bytes[(20 * 40 + 10) * 4 + 3];
        // Pixel directly above the rect at the same x column → no vertical
        // blur → should remain transparent.
        int aAbove = bytes[(10 * 40 + 20) * 4 + 3];

        Assert.True(aLeft > aAbove + 30,
            $"horizontal-only blur should bleed left more than up: aLeft={aLeft}, aAbove={aAbove}");
    }

    [Fact]
    public async Task LoadAsync_Filter_UnsupportedPrimitive_Throws()
    {
        // feColorMatrix, feFlood, etc. throw with a clear message.
        const string svg =
            "<svg width=\"4\" height=\"4\">" +
            "<defs><filter id=\"f\"><feColorMatrix type=\"matrix\" values=\"0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 1 0\"/></filter></defs>" +
            "<rect width=\"4\" height=\"4\" fill=\"red\" filter=\"url(#f)\"/></svg>";
        await Assert.ThrowsAsync<NotSupportedException>(() => LoadSvg(svg).AsTask());
    }

    [Fact]
    public async Task LoadAsync_Filter_ZeroStdDeviation_NoOp()
    {
        // stdDeviation="0" should be a no-op (or close to it). The shape
        // renders unchanged.
        const string svg =
            "<svg width=\"10\" height=\"10\">" +
            "<defs><filter id=\"none\"><feGaussianBlur stdDeviation=\"0\"/></filter></defs>" +
            "<rect width=\"10\" height=\"10\" fill=\"red\" filter=\"url(#none)\"/></svg>";
        var img = await LoadSvg(svg);
        var bytes = img.PixelsLazy!.Value;
        Assert.Equal(255, bytes[(5 * 10 + 5) * 4 + 0]);
        Assert.Equal(0,   bytes[(5 * 10 + 5) * 4 + 1]);
        Assert.Equal(255, bytes[(5 * 10 + 5) * 4 + 3]);
    }

    [Fact]
    public async Task LoadAsync_Mask_RendersThroughWhiteMask()
    {
        // <mask> is supported as of #38. A white-fill mask should pass the
        // referenced element through unchanged. Full coverage lives in
        // SvgMaskTests.
        const string svg =
            "<svg width=\"4\" height=\"4\">" +
            "<defs><mask id=\"m\"><rect width=\"4\" height=\"4\" fill=\"white\"/></mask></defs>" +
            "<rect width=\"4\" height=\"4\" fill=\"red\" mask=\"url(#m)\"/></svg>";
        var img = await LoadSvg(svg);
        var bytes = img.PixelsLazy!.Value;
        int i = (2 * 4 + 2) * 4;
        Assert.True(bytes[i + 0] > 240, $"R: {bytes[i + 0]}");
        Assert.Equal(255, bytes[i + 3]);
    }

    [Fact]
    public async Task LoadAsync_LinearGradient_ObjectBoundingBoxDefault()
    {
        // No gradientUnits specified → defaults to objectBoundingBox per spec.
        // x1=0, x2=1 in bbox space maps to the full shape width.
        const string svg =
            "<svg width=\"20\" height=\"4\">" +
            "<defs><linearGradient id=\"g\" x1=\"0\" y1=\"0\" x2=\"1\" y2=\"0\">" +
            "<stop offset=\"0\" stop-color=\"red\"/>" +
            "<stop offset=\"1\" stop-color=\"blue\"/>" +
            "</linearGradient></defs>" +
            "<rect width=\"20\" height=\"4\" fill=\"url(#g)\"/></svg>";
        var img = await LoadSvg(svg);
        var bytes = img.PixelsLazy!.Value;
        // Same result as the userSpaceOnUse test with x2=20: red at left, blue at right.
        Assert.True(bytes[(2 * 20 + 1) * 4 + 0] > 200);
        Assert.True(bytes[(2 * 20 + 18) * 4 + 2] > 200);
    }

    [Fact]
    public async Task LoadAsync_RadialGradient_CenterRedToEdgeBlue()
    {
        // 20×20 rect with a radial gradient: red at the center (focal),
        // blue at the edge. Sample the center → red; sample the corner → blue.
        const string svg =
            "<svg width=\"20\" height=\"20\">" +
            "<defs><radialGradient id=\"g\" gradientUnits=\"userSpaceOnUse\" cx=\"10\" cy=\"10\" r=\"14\">" +
            "<stop offset=\"0\" stop-color=\"red\"/>" +
            "<stop offset=\"1\" stop-color=\"blue\"/>" +
            "</radialGradient></defs>" +
            "<rect width=\"20\" height=\"20\" fill=\"url(#g)\"/></svg>";
        var img = await LoadSvg(svg);
        var bytes = img.PixelsLazy!.Value;
        // Center pixel: mostly red.
        int rCenter = bytes[(10 * 20 + 10) * 4 + 0];
        int bCenter = bytes[(10 * 20 + 10) * 4 + 2];
        Assert.True(rCenter > 200 && bCenter < 60, $"center R={rCenter} B={bCenter}");
        // Corner pixel: mostly blue.
        int rCorner = bytes[(0 * 20 + 0) * 4 + 0];
        int bCorner = bytes[(0 * 20 + 0) * 4 + 2];
        Assert.True(bCorner > 150 && rCorner < 100, $"corner R={rCorner} B={bCorner}");
    }

    [Fact]
    public async Task LoadAsync_RadialGradient_BboxDefaults()
    {
        // Defaults: cx=cy=r=0.5, focal=center, gradientUnits=objectBoundingBox.
        // Center of a 16×16 rect at (8, 8) → fully red.
        const string svg =
            "<svg width=\"16\" height=\"16\">" +
            "<defs><radialGradient id=\"g\">" +
            "<stop offset=\"0\" stop-color=\"red\"/>" +
            "<stop offset=\"1\" stop-color=\"#00FF00\"/>" +
            "</radialGradient></defs>" +
            "<rect width=\"16\" height=\"16\" fill=\"url(#g)\"/></svg>";
        var img = await LoadSvg(svg);
        var bytes = img.PixelsLazy!.Value;
        // Center red; corner (outside r=0.5 in bbox = radius 8 px in user space) green.
        int idxC = (8 * 16 + 8) * 4;
        Assert.True(bytes[idxC + 0] > 200 && bytes[idxC + 1] < 60);
        int idxK = (0 * 16 + 0) * 4;
        Assert.True(bytes[idxK + 1] > 200);
    }

    [Fact]
    public async Task LoadAsync_RadialGradient_FocalOffset_AsymmetricFade()
    {
        // Focal point displaced toward top-left within a centered circle.
        // The "highlight" sits at the focal; transition along rays from there.
        const string svg =
            "<svg width=\"40\" height=\"40\">" +
            "<defs><radialGradient id=\"g\" gradientUnits=\"userSpaceOnUse\" " +
                  "cx=\"20\" cy=\"20\" r=\"20\" fx=\"10\" fy=\"10\">" +
            "<stop offset=\"0\" stop-color=\"white\"/>" +
            "<stop offset=\"1\" stop-color=\"black\"/>" +
            "</radialGradient></defs>" +
            "<rect width=\"40\" height=\"40\" fill=\"url(#g)\"/></svg>";
        var img = await LoadSvg(svg);
        var bytes = img.PixelsLazy!.Value;
        // At focal point (10,10): bright (R=G=B close to 255).
        int idxF = (10 * 40 + 10) * 4;
        Assert.True(bytes[idxF + 0] > 200, $"focal R={bytes[idxF + 0]}");
        // Opposite end (30,30): much darker.
        int idxD = (30 * 40 + 30) * 4;
        Assert.True(bytes[idxD + 0] < bytes[idxF + 0] - 50,
            $"opposite-end R={bytes[idxD + 0]} not darker than focal R={bytes[idxF + 0]}");
    }

    [Fact]
    public async Task LoadAsync_GradientTransform_RotatesGradient()
    {
        // Vertical gradient via gradientTransform="rotate(90)" applied to a
        // horizontal-axis-aligned authored gradient. After rotation, red
        // should be at the top, blue at the bottom.
        const string svg =
            "<svg width=\"4\" height=\"20\">" +
            "<defs><linearGradient id=\"g\" gradientUnits=\"userSpaceOnUse\" " +
                  "x1=\"0\" y1=\"0\" x2=\"20\" y2=\"0\" gradientTransform=\"rotate(90)\">" +
            "<stop offset=\"0\" stop-color=\"red\"/>" +
            "<stop offset=\"1\" stop-color=\"blue\"/>" +
            "</linearGradient></defs>" +
            "<rect width=\"4\" height=\"20\" fill=\"url(#g)\"/></svg>";
        var img = await LoadSvg(svg);
        var bytes = img.PixelsLazy!.Value;
        // Top: red.
        Assert.True(bytes[(1 * 4 + 2) * 4 + 0] > 200);
        // Bottom: blue.
        Assert.True(bytes[(18 * 4 + 2) * 4 + 2] > 200);
    }

    [Fact]
    public async Task LoadAsync_SpreadMethodRepeat_TilesGradient()
    {
        // Gradient covers x=0..5 in user space; the rest of the 20-wide rect
        // should tile: positions 5..10, 10..15, 15..20 each repeat the same
        // red→blue ramp from the original 0..5. So x=2 ≈ red, x=7 ≈ red again.
        const string svg =
            "<svg width=\"20\" height=\"4\">" +
            "<defs><linearGradient id=\"g\" gradientUnits=\"userSpaceOnUse\" " +
                  "x1=\"0\" y1=\"0\" x2=\"5\" y2=\"0\" spreadMethod=\"repeat\">" +
            "<stop offset=\"0\" stop-color=\"red\"/>" +
            "<stop offset=\"1\" stop-color=\"blue\"/>" +
            "</linearGradient></defs>" +
            "<rect width=\"20\" height=\"4\" fill=\"url(#g)\"/></svg>";
        var img = await LoadSvg(svg);
        var bytes = img.PixelsLazy!.Value;
        // Each "red-end" sample is at the start of a tile. Pixel-center
        // sampling lands the read 10% into the tile (cx + 0.5), so the
        // returned colour is ~70% red / 30% blue — red still dominates by
        // a wide margin but never reaches 200.
        for (int x = 0; x < 20; x += 5)
        {
            int r = bytes[(2 * 20 + x) * 4 + 0];
            int b = bytes[(2 * 20 + x) * 4 + 2];
            Assert.True(r > b + 50, $"tile-start x={x}: R={r} not red-dominant over B={b}");
        }
    }

    [Fact]
    public async Task LoadAsync_SpreadMethodReflect_MirrorsAlternateTiles()
    {
        // Reflect: tile boundaries flip the gradient direction.
        // 0..5 = red→blue (forward), 5..10 = blue→red (reversed), 10..15 = red→blue, ...
        const string svg =
            "<svg width=\"20\" height=\"4\">" +
            "<defs><linearGradient id=\"g\" gradientUnits=\"userSpaceOnUse\" " +
                  "x1=\"0\" y1=\"0\" x2=\"5\" y2=\"0\" spreadMethod=\"reflect\">" +
            "<stop offset=\"0\" stop-color=\"red\"/>" +
            "<stop offset=\"1\" stop-color=\"blue\"/>" +
            "</linearGradient></defs>" +
            "<rect width=\"20\" height=\"4\" fill=\"url(#g)\"/></svg>";
        var img = await LoadSvg(svg);
        var bytes = img.PixelsLazy!.Value;
        // x=4: end of first forward tile → blue.
        Assert.True(bytes[(2 * 20 + 4) * 4 + 2] > 150);
        // x=6: start of reversed tile → still blue (since reflected from the blue end).
        Assert.True(bytes[(2 * 20 + 6) * 4 + 2] > 150);
        // x=9: end of reversed tile → red.
        Assert.True(bytes[(2 * 20 + 9) * 4 + 0] > 150);
    }

    [Fact]
    public async Task LoadAsync_FillUrl_UnresolvedId_FallsBackToInherited()
    {
        // url() pointing at a non-existent id falls back to the inherited
        // fill (per ParseColor's policy).
        const string svg =
            "<svg width=\"4\" height=\"4\">" +
            "<g fill=\"red\"><rect width=\"4\" height=\"4\" fill=\"url(#nope)\"/></g></svg>";
        var img = await LoadSvg(svg);
        // Inherited red wins.
        Assert.Equal(255, img.PixelsLazy!.Value[0]);
    }

    // ---- helper ----

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
}
