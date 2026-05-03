using System;
using CosmoImage.Operations.Drawing;
using Xunit;

namespace CosmoImage.Tests;

public class Round70Tests
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

    private static byte[] Sample(IVipsBrush b, int x, int y, int bands)
    {
        var dst = new byte[bands];
        b.SampleAt(x, y, dst);
        return dst;
    }

    // ---- Sampling at known points ----

    [Fact]
    public void PathGradient_AtVertex_ReturnsThatVertexColour()
    {
        // Square with red/green/blue/yellow corners.
        var verts = new (double, double)[] { (0, 0), (10, 0), (10, 10), (0, 10) };
        var colors = new byte[][] {
            new byte[] { 255, 0, 0 },
            new byte[] { 0, 255, 0 },
            new byte[] { 0, 0, 255 },
            new byte[] { 255, 255, 0 },
        };
        var brush = new VipsPathGradientBrush(verts, colors);
        Assert.Equal(new byte[] { 255, 0, 0 }, Sample(brush, 0, 0, 3));
        Assert.Equal(new byte[] { 0, 255, 0 }, Sample(brush, 10, 0, 3));
        Assert.Equal(new byte[] { 0, 0, 255 }, Sample(brush, 10, 10, 3));
        Assert.Equal(new byte[] { 255, 255, 0 }, Sample(brush, 0, 10, 3));
    }

    [Fact]
    public void PathGradient_AtCentroid_ReturnsCentreColour()
    {
        // Square; centroid = (5, 5).
        var verts = new (double, double)[] { (0, 0), (10, 0), (10, 10), (0, 10) };
        var colors = new byte[][] {
            new byte[] { 100, 0, 0 },
            new byte[] { 100, 0, 0 },
            new byte[] { 100, 0, 0 },
            new byte[] { 100, 0, 0 },
        };
        var centre = new byte[] { 0, 200, 0 };
        var brush = new VipsPathGradientBrush(verts, colors, centre);
        var pel = Sample(brush, 5, 5, 3);
        Assert.Equal(0, pel[0]);
        Assert.Equal(200, pel[1]);
        Assert.Equal(0, pel[2]);
    }

    [Fact]
    public void PathGradient_DefaultCentreIsAverageOfVertexColours()
    {
        // 4 vertices: (200, 100, 0). Average band-wise → (200, 100, 0).
        var verts = new (double, double)[] { (0, 0), (10, 0), (10, 10), (0, 10) };
        var colors = new byte[][] {
            new byte[] { 200, 100, 0 },
            new byte[] { 200, 100, 0 },
            new byte[] { 200, 100, 0 },
            new byte[] { 200, 100, 0 },
        };
        var brush = new VipsPathGradientBrush(verts, colors);  // no centre colour
        var atCentroid = Sample(brush, 5, 5, 3);
        Assert.Equal(200, atCentroid[0]);
        Assert.Equal(100, atCentroid[1]);
        Assert.Equal(0, atCentroid[2]);
    }

    [Fact]
    public void PathGradient_AtMidEdge_BlendsCentreAndTwoNearestVertices()
    {
        // Triangle (0,0), (10,0), (5,10). Centroid = (5, 10/3) ≈ (5, 3.33).
        // Midpoint of bottom edge = (5, 0). At y=0 it's between v0 and v1.
        var verts = new (double, double)[] { (0, 0), (10, 0), (5, 10) };
        var colors = new byte[][] {
            new byte[] { 200, 0, 0 },
            new byte[] { 0, 200, 0 },
            new byte[] { 0, 0, 200 },
        };
        // Equal centre colour so only vertex influence shows.
        var centre = new byte[] { 0, 0, 0 };
        var brush = new VipsPathGradientBrush(verts, colors, centre);
        var pel = Sample(brush, 5, 0, 3);
        // Should be roughly red+green half-way (centre weight 0 at the edge).
        Assert.True(pel[0] > 50, $"R should be sizable, got {pel[0]}");
        Assert.True(pel[1] > 50, $"G should be sizable, got {pel[1]}");
        Assert.Equal(0, pel[2]);
    }

    [Fact]
    public void PathGradient_OutsidePolygon_FallsBackToCentre()
    {
        var verts = new (double, double)[] { (5, 5), (10, 5), (10, 10), (5, 10) };
        var colors = new byte[][] {
            new byte[] { 100, 0, 0 },
            new byte[] { 100, 0, 0 },
            new byte[] { 100, 0, 0 },
            new byte[] { 100, 0, 0 },
        };
        var centre = new byte[] { 0, 200, 0 };
        var brush = new VipsPathGradientBrush(verts, colors, centre);
        var pel = Sample(brush, 0, 0, 3); // far outside
        Assert.Equal(centre, pel);
    }

    // ---- Rendered correctness ----

    [Fact]
    public void PathGradient_RenderedFill_ProducesGradient()
    {
        // Fill a triangle and check that distinct colours appear in
        // distinct sub-regions.
        var bg = RgbSolid(20, 20, 0, 0, 0);
        var verts = new (double, double)[] { (2, 18), (18, 18), (10, 2) };
        var colors = new byte[][] {
            new byte[] { 255, 0, 0 },    // red bottom-left
            new byte[] { 0, 255, 0 },    // green bottom-right
            new byte[] { 0, 0, 255 },    // blue top
        };
        var brush = new VipsPathGradientBrush(verts, colors);
        var path = VipsPath.Polygon((2, 18), (18, 18), (10, 2));
        var painted = VipsImageOps.FillPath(bg, path, brush, aa: false);
        // Bottom-left corner should be red-dominant.
        var bl = ReadPel(painted, 4, 16);
        Assert.True(bl[0] > bl[1] && bl[0] > bl[2],
            $"bottom-left should be red-dominant, got {bl[0]},{bl[1]},{bl[2]}");
        // Bottom-right corner should be green-dominant.
        var br = ReadPel(painted, 16, 16);
        Assert.True(br[1] > br[0] && br[1] > br[2],
            $"bottom-right should be green-dominant, got {br[0]},{br[1]},{br[2]}");
        // Top corner should be blue-dominant.
        var top = ReadPel(painted, 10, 4);
        Assert.True(top[2] > top[0] && top[2] > top[1],
            $"top should be blue-dominant, got {top[0]},{top[1]},{top[2]}");
    }

    // ---- Validation ----

    [Fact]
    public void PathGradient_RejectsTooFewVertices()
    {
        var verts = new (double, double)[] { (0, 0), (5, 5) };
        var colors = new byte[][] { new byte[] { 0 }, new byte[] { 0 } };
        Assert.Throws<ArgumentException>(() => new VipsPathGradientBrush(verts, colors));
    }

    [Fact]
    public void PathGradient_RejectsCountMismatch()
    {
        var verts = new (double, double)[] { (0, 0), (5, 0), (5, 5) };
        var colors = new byte[][] { new byte[] { 0 }, new byte[] { 0 } };
        Assert.Throws<ArgumentException>(() => new VipsPathGradientBrush(verts, colors));
    }

    [Fact]
    public void PathGradient_RejectsBandCountMismatch()
    {
        var verts = new (double, double)[] { (0, 0), (5, 0), (5, 5) };
        var colors = new byte[][] {
            new byte[] { 255, 0, 0 },
            new byte[] { 255, 0 },          // mismatched band count
            new byte[] { 255, 0, 0 },
        };
        Assert.Throws<ArgumentException>(() => new VipsPathGradientBrush(verts, colors));
    }
}
