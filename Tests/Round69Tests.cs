using System.Linq;
using CosmoImage.Operations.Drawing;
using Xunit;

namespace CosmoImage.Tests;

public class Round69Tests
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

    private static byte[] ReadPel(VipsImage img, int x, int y)
    {
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, img.Width, img.Height));
        return reg.GetAddress(x, y).Slice(0, img.Bands).ToArray();
    }

    // ---- Geometry of arc-to-cubic conversion ----

    [Fact]
    public void ArcTo_HalfCircle_EmitsTwoCubics()
    {
        // 180° arc → splits into 2 × 90° pieces, one cubic each.
        var path = new VipsPath().MoveTo(0, 20)
            .ArcTo(20, 20, 0, false, true, 40, 20);
        int cubics = path.Segments.Count(s => s.Kind == VipsPathSegmentKind.CubicTo);
        Assert.Equal(2, cubics);
    }

    [Fact]
    public void ArcTo_DegenerateZeroRadius_BecomesLine()
    {
        var path = new VipsPath().MoveTo(0, 0).ArcTo(0, 5, 0, false, true, 10, 10);
        Assert.Equal(VipsPathSegmentKind.LineTo, path.Segments[1].Kind);
        Assert.Equal(10, path.Segments[1].X1);
        Assert.Equal(10, path.Segments[1].Y1);
    }

    [Fact]
    public void ArcTo_SamePoint_OmittedPerSvgSpec()
    {
        var path = new VipsPath().MoveTo(5, 5).ArcTo(10, 10, 0, false, true, 5, 5);
        Assert.Single(path.Segments); // just the MoveTo
    }

    [Fact]
    public void ArcTo_LargeArc_EmitsMoreCubicsThanSmall()
    {
        // Same endpoints + radii, different largeArc flag.
        var small = new VipsPath().MoveTo(10, 0).ArcTo(10, 10, 0, false, true, 0, 10);
        var large = new VipsPath().MoveTo(10, 0).ArcTo(10, 10, 0, true, true, 0, 10);
        int s = small.Segments.Count(seg => seg.Kind == VipsPathSegmentKind.CubicTo);
        int l = large.Segments.Count(seg => seg.Kind == VipsPathSegmentKind.CubicTo);
        // Small arc is 90° → 1 cubic; large arc is 270° → 3 cubics.
        Assert.Equal(1, s);
        Assert.Equal(3, l);
    }

    [Fact]
    public void ArcTo_FirstCubicEndpoint_AtQuarterCircle()
    {
        // Arc from (20, 0) sweeping a half-circle to (-20, 0), centred at origin.
        // First 90° piece should end at (0, 20) (sweep=true, y-down screen → curves down).
        var path = new VipsPath().MoveTo(20, 0).ArcTo(20, 20, 0, false, true, -20, 0);
        // Segment[1] is the first cubic; its endpoint is (X3, Y3).
        Assert.Equal(VipsPathSegmentKind.CubicTo, path.Segments[1].Kind);
        Assert.Equal(0, path.Segments[1].X3, 6);
        Assert.Equal(20, path.Segments[1].Y3, 6);
    }

    // ---- Rendered arcs ----

    [Fact]
    public void ArcTo_HalfCircle_SweepTrue_CurvesUpward()
    {
        // sweep=true on screen (y-down) is positive-angle direction.
        // From angle π (left of centre) sweeping positively goes
        // through angle 3π/2 = (0, -1) relative to centre = top of
        // screen. So the arc curves UP and the closed half-disk fills
        // the upper half (low y).
        var bg = RgbSolid(40, 40, 0, 0, 0);
        var brush = new VipsSolidBrush(255, 0, 0);
        var path = new VipsPath()
            .MoveTo(0, 20)
            .ArcTo(20, 20, 0, false, true, 40, 20)
            .Close();
        var painted = VipsImageOps.FillPath(bg, path, brush, aa: false);
        Assert.Equal(255, ReadPel(painted, 20, 10)[0]);  // upper half: inside
        Assert.Equal(0, ReadPel(painted, 20, 30)[0]);    // lower half: outside
    }

    [Fact]
    public void ArcTo_SweepFalse_CurvesDownward()
    {
        var bg = RgbSolid(40, 40, 0, 0, 0);
        var brush = new VipsSolidBrush(255, 0, 0);
        var path = new VipsPath()
            .MoveTo(0, 20)
            .ArcTo(20, 20, 0, false, false, 40, 20)
            .Close();
        var painted = VipsImageOps.FillPath(bg, path, brush, aa: false);
        Assert.Equal(0, ReadPel(painted, 20, 10)[0]);    // upper: outside
        Assert.Equal(255, ReadPel(painted, 20, 30)[0]);  // lower: inside
    }

    [Fact]
    public void ArcTo_TransformedPath_CubicsTransformCorrectly()
    {
        // ArcTo emits cubics at construction; subsequent transforms
        // act on those cubics directly.
        var path = new VipsPath().MoveTo(0, 0)
            .ArcTo(5, 5, 0, false, true, 10, 0)
            .Translate(50, 100);
        Assert.Equal(50, path.Segments[0].X1);
        Assert.Equal(100, path.Segments[0].Y1);
        // Last cubic's endpoint = original (10, 0) translated by (50, 100).
        var lastCubic = path.Segments[^1];
        Assert.Equal(VipsPathSegmentKind.CubicTo, lastCubic.Kind);
        Assert.Equal(60, lastCubic.X3, 6);
        Assert.Equal(100, lastCubic.Y3, 6);
    }

    [Fact]
    public void ArcTo_FullCircleViaTwoSemicircles_FillsDisk()
    {
        // Compose a full circle from two opposite half-arcs; even-odd
        // fill should produce a solid disk.
        var bg = RgbSolid(40, 40, 0, 0, 0);
        var brush = new VipsSolidBrush(255, 0, 0);
        var path = new VipsPath()
            .MoveTo(5, 20)
            .ArcTo(15, 15, 0, false, true, 35, 20)
            .ArcTo(15, 15, 0, false, true, 5, 20)
            .Close();
        var painted = VipsImageOps.FillPath(bg, path, brush, aa: false);
        Assert.Equal(255, ReadPel(painted, 20, 20)[0]);  // centre
        Assert.Equal(255, ReadPel(painted, 20, 10)[0]);  // top-of-disk
        Assert.Equal(255, ReadPel(painted, 20, 30)[0]);  // bottom-of-disk
        Assert.Equal(0, ReadPel(painted, 0, 0)[0]);      // outside
    }

    [Fact]
    public void ArcTo_StrokingProducesPaintedPixels()
    {
        // Stroking should "just work" because ArcTo emits cubics —
        // the stroke outline + fill pipeline already handles those.
        var bg = RgbSolid(40, 40, 0, 0, 0);
        var pen = VipsPen.Solid(255, 255, 255, 2);
        var path = new VipsPath()
            .MoveTo(5, 20)
            .ArcTo(15, 15, 0, false, true, 35, 20);
        var painted = VipsImageOps.StrokePath(bg, path, pen, aa: false);
        int count = 0;
        using var reg = new VipsRegion(painted);
        reg.Prepare(new VipsRect(0, 0, 40, 40));
        for (int y = 0; y < 40; y++)
            for (int x = 0; x < 40; x++)
                if (reg.GetAddress(x, y)[0] == 255) count++;
        // ~30-px arc with width-2 stroke → at least a few dozen pixels.
        Assert.True(count > 30, $"expected painted arc, got {count} pixels");
    }
}
