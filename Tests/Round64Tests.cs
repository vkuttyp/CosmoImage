using System;
using CosmoImage.Operations.Drawing;
using Xunit;

namespace CosmoImage.Tests;

public class Round64Tests
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

    /// <summary>Count painted (≥ 100) pixels in the source image.</summary>
    private static int CountPainted(VipsImage img)
    {
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, img.Width, img.Height));
        int count = 0;
        for (int y = 0; y < img.Height; y++)
            for (int x = 0; x < img.Width; x++)
                if (reg.GetAddress(x, y)[0] >= 100) count++;
        return count;
    }

    // ---- Caps ----

    [Fact]
    public void SquareCap_ExtendsBeyondEndpoint()
    {
        // Horizontal stroke from (5, 5) to (15, 5), width 4.
        // Butt cap ends at x=5 / x=15. Square cap extends by half=2 →
        // ends at x=3 / x=17 (so pixel at x=3 should be painted).
        var bg = RgbSolid(20, 10, 0, 0, 0);
        var path = new VipsPath().MoveTo(5, 5).LineTo(15, 5);

        var butt = VipsImageOps.StrokePath(bg, path,
            new VipsPen(new VipsSolidBrush(255, 255, 255), width: 4,
                cap: VipsLineCap.Butt), aa: false);
        var square = VipsImageOps.StrokePath(bg, path,
            new VipsPen(new VipsSolidBrush(255, 255, 255), width: 4,
                cap: VipsLineCap.Square), aa: false);

        // Pixel just outside the butt cap (x=3) is unpainted by butt
        // but painted by square (extension reaches x=3).
        Assert.Equal(0, ReadPel(butt, 3, 5)[0]);
        Assert.Equal(255, ReadPel(square, 3, 5)[0]);
    }

    [Fact]
    public void RoundCap_PaintsCircularExtension()
    {
        // Horizontal stroke; round cap should paint a half-circle at
        // each endpoint. Pixel directly above the endpoint (within
        // half-width) should be inside the cap.
        var bg = RgbSolid(20, 20, 0, 0, 0);
        var path = new VipsPath().MoveTo(5, 10).LineTo(15, 10);
        var pen = new VipsPen(new VipsSolidBrush(255, 255, 255), width: 6,
            cap: VipsLineCap.Round);
        var painted = VipsImageOps.StrokePath(bg, path, pen, aa: false);

        // The round cap at (5, 10) is a half-circle of radius 3
        // sweeping into the (-1, 0) direction (back of the line).
        // A pixel like (3, 10) — 2 px left of the endpoint, on the
        // line of the stroke — sits inside the round cap.
        Assert.True(ReadPel(painted, 3, 10)[0] > 0);
        // A pixel far above (e.g. y=2) is outside both the stroke
        // body and the cap (cap radius 3, but at y=2 we'd be 8 px above
        // the line — way outside).
        Assert.Equal(0, ReadPel(painted, 5, 2)[0]);
    }

    // ---- Joins ----

    [Fact]
    public void MiterJoin_ProducesSpikeOuter()
    {
        // Two segments meeting at a 90° angle: ╗-shape (right then down).
        // Bevel cuts the outer corner flat; miter extends to the
        // outer-intersection point — a sharp corner pixel.
        var bg = RgbSolid(30, 30, 0, 0, 0);
        var path = new VipsPath().MoveTo(5, 15).LineTo(20, 15).LineTo(20, 28);

        var bevelPen = new VipsPen(new VipsSolidBrush(255, 255, 255), width: 4,
            join: VipsLineJoin.Bevel);
        var miterPen = new VipsPen(new VipsSolidBrush(255, 255, 255), width: 4,
            join: VipsLineJoin.Miter);
        var bevel = VipsImageOps.StrokePath(bg, path, bevelPen, aa: false);
        var miter = VipsImageOps.StrokePath(bg, path, miterPen, aa: false);

        // The miter outline covers more pixels at the corner — the
        // outer-intersection sharp spike adds area the bevel cuts off.
        int bevelPainted = CountPainted(bevel);
        int miterPainted = CountPainted(miter);
        Assert.True(miterPainted > bevelPainted,
            $"miter should paint more than bevel at a sharp corner, got bevel={bevelPainted} miter={miterPainted}");
    }

    [Fact]
    public void MiterLimit_FallsBackToBevelOnSharpAngle()
    {
        // Two segments meeting at a very acute angle (~10°). Without a
        // miter limit, the miter spike would extend wildly. With the
        // default limit (4), miter falls back to bevel — same painted
        // area as a Bevel pen.
        var bg = RgbSolid(50, 50, 0, 0, 0);
        // Acute V-shape: long segment + sharp turn back nearly the
        // same direction.
        var path = new VipsPath().MoveTo(5, 25).LineTo(45, 25).LineTo(8, 27);

        var bevelPen = new VipsPen(new VipsSolidBrush(255, 255, 255), width: 4,
            join: VipsLineJoin.Bevel);
        var miterPen = new VipsPen(new VipsSolidBrush(255, 255, 255), width: 4,
            join: VipsLineJoin.Miter, miterLimit: 4.0);
        var bevel = VipsImageOps.StrokePath(bg, path, bevelPen, aa: false);
        var miter = VipsImageOps.StrokePath(bg, path, miterPen, aa: false);

        // Painted area is roughly equal — the limit kicked in.
        int bevelPainted = CountPainted(bevel);
        int miterPainted = CountPainted(miter);
        // Allow some slack since the bevel and miter geometry differ
        // even at the limit boundary.
        Assert.InRange(Math.Abs(miterPainted - bevelPainted), 0, bevelPainted / 5);
    }

    [Fact]
    public void RoundJoin_PaintsArcAtCorner()
    {
        var bg = RgbSolid(30, 30, 0, 0, 0);
        var path = new VipsPath().MoveTo(5, 15).LineTo(15, 15).LineTo(15, 25);
        var roundPen = new VipsPen(new VipsSolidBrush(255, 255, 255), width: 6,
            join: VipsLineJoin.Round);
        var painted = VipsImageOps.StrokePath(bg, path, roundPen, aa: false);

        // Round join paints a quarter-circle at the corner. The arc
        // at the (15, 15) corner with half-width 3 sweeps from
        // (15, 12) to (18, 15) — at y=13 the arc has bulged past
        // x=17.6 so pixel (17, 13) sits inside the round-join arc.
        // (Pixel (17, 12) is just past the start of the arc and not
        // yet inside.)
        Assert.True(ReadPel(painted, 17, 13)[0] > 100);
    }

    // ---- Default behaviour ----

    [Fact]
    public void Default_CapAndJoin_ButtAndBevel()
    {
        var pen = new VipsPen(new VipsSolidBrush(0, 0, 0), 2);
        Assert.Equal(VipsLineCap.Butt, pen.Cap);
        Assert.Equal(VipsLineJoin.Bevel, pen.Join);
        Assert.Equal(4.0, pen.MiterLimit);
    }

    [Fact]
    public void MiterLimit_RejectsLessThanOne()
    {
        Assert.Throws<ArgumentException>(() =>
            new VipsPen(new VipsSolidBrush(0, 0, 0), 2, miterLimit: 0.5));
    }
}
