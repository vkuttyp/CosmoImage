using System;
using Xunit;

namespace CosmoImage.Tests;

public class Round37Tests
{
    private static VipsImage Solid(int w, int h, byte v)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++) addr[x] = v;
                }
                return 0;
            }
        };

    // ---- Arrayjoin ----

    [Fact]
    public void Arrayjoin_HorizontalRow_LaysSidebySide()
    {
        var a = Solid(2, 2, 10);
        var b = Solid(2, 2, 20);
        var c = Solid(2, 2, 30);
        var row = VipsImageOps.Arrayjoin(new[] { a, b, c });
        Assert.Equal(6, row.Width);
        Assert.Equal(2, row.Height);

        using var reg = new VipsRegion(row);
        reg.Prepare(new VipsRect(0, 0, 6, 2));
        Assert.Equal(10, reg.GetAddress(0, 0)[0]);
        Assert.Equal(20, reg.GetAddress(2, 0)[0]);
        Assert.Equal(30, reg.GetAddress(4, 0)[0]);
    }

    [Fact]
    public void Arrayjoin_2DGrid_FillsRowsAcross()
    {
        // 4 inputs, across = 2 → 2 rows × 2 cols.
        var a = Solid(2, 2, 10);
        var b = Solid(2, 2, 20);
        var c = Solid(2, 2, 30);
        var d = Solid(2, 2, 40);
        var grid = VipsImageOps.Arrayjoin(new[] { a, b, c, d }, across: 2);
        Assert.Equal(4, grid.Width);
        Assert.Equal(4, grid.Height);

        using var reg = new VipsRegion(grid);
        reg.Prepare(new VipsRect(0, 0, 4, 4));
        Assert.Equal(10, reg.GetAddress(0, 0)[0]);
        Assert.Equal(20, reg.GetAddress(2, 0)[0]);
        Assert.Equal(30, reg.GetAddress(0, 2)[0]);
        Assert.Equal(40, reg.GetAddress(2, 2)[0]);
    }

    [Fact]
    public void Arrayjoin_ShimFillsBetweenWithBackground()
    {
        var a = Solid(2, 2, 100);
        var b = Solid(2, 2, 200);
        var row = VipsImageOps.Arrayjoin(new[] { a, b }, shim: 2, background: new[] { 50.0 });
        Assert.Equal(6, row.Width); // 2 + 2 (shim) + 2

        using var reg = new VipsRegion(row);
        reg.Prepare(new VipsRect(0, 0, 6, 2));
        Assert.Equal(100, reg.GetAddress(1, 0)[0]); // first cell
        Assert.Equal(50, reg.GetAddress(2, 0)[0]);  // shim
        Assert.Equal(50, reg.GetAddress(3, 0)[0]);  // shim
        Assert.Equal(200, reg.GetAddress(4, 0)[0]); // second cell
    }

    [Fact]
    public void Arrayjoin_VariableSizes_PadsByMaxRowHeight()
    {
        var tall = Solid(2, 4, 100);
        var short_ = Solid(2, 2, 200);
        var row = VipsImageOps.Arrayjoin(new[] { tall, short_ }, background: new[] { 50.0 });
        Assert.Equal(4, row.Height);

        using var reg = new VipsRegion(row);
        reg.Prepare(new VipsRect(0, 0, 4, 4));
        // Short_ is at column 1 (cols 2..3) — top 2 rows have it, bottom 2 rows are background.
        Assert.Equal(200, reg.GetAddress(2, 0)[0]);
        Assert.Equal(200, reg.GetAddress(2, 1)[0]);
        Assert.Equal(50, reg.GetAddress(2, 2)[0]);  // background
        Assert.Equal(50, reg.GetAddress(2, 3)[0]);
    }

    // ---- Join ----

    [Fact]
    public void Join_Horizontal_HardSeam()
    {
        var a = Solid(3, 2, 50);
        var b = Solid(3, 2, 200);
        var joined = a.Join(b);
        Assert.Equal(6, joined.Width);
        Assert.Equal(2, joined.Height);

        using var reg = new VipsRegion(joined);
        reg.Prepare(new VipsRect(0, 0, 6, 2));
        Assert.Equal(50, reg.GetAddress(2, 0)[0]);
        Assert.Equal(200, reg.GetAddress(3, 0)[0]);
    }

    [Fact]
    public void Join_Vertical_StackTopBottom()
    {
        var a = Solid(3, 2, 50);
        var b = Solid(3, 2, 200);
        var joined = a.Join(b, direction: VipsDirection.Vertical);
        Assert.Equal(3, joined.Width);
        Assert.Equal(4, joined.Height);

        using var reg = new VipsRegion(joined);
        reg.Prepare(new VipsRect(0, 0, 3, 4));
        Assert.Equal(50, reg.GetAddress(1, 1)[0]);
        Assert.Equal(200, reg.GetAddress(1, 2)[0]);
    }

    [Fact]
    public void Join_DifferentHeights_AlignsLowAndFillsBackground()
    {
        var tall = Solid(2, 4, 100);
        var short_ = Solid(2, 2, 200);
        var joined = tall.Join(short_, background: new[] { 50.0 });
        Assert.Equal(4, joined.Width);
        Assert.Equal(4, joined.Height);

        using var reg = new VipsRegion(joined);
        reg.Prepare(new VipsRect(0, 0, 4, 4));
        Assert.Equal(200, reg.GetAddress(2, 0)[0]);
        Assert.Equal(200, reg.GetAddress(2, 1)[0]);
        Assert.Equal(50, reg.GetAddress(2, 2)[0]);
        Assert.Equal(50, reg.GetAddress(2, 3)[0]);
    }

    [Fact]
    public void Join_Shim_BlendsAcrossSeam()
    {
        // 4-wide A solid 0, 4-wide B solid 200, shim 4 → output is 4 wide blend.
        var a = Solid(4, 1, 0);
        var b = Solid(4, 1, 200);
        var joined = a.Join(b, shim: 4);
        Assert.Equal(4, joined.Width);

        using var reg = new VipsRegion(joined);
        reg.Prepare(new VipsRect(0, 0, 4, 1));
        // Mid column should be roughly halfway (~100).
        Assert.InRange(reg.GetAddress(2, 0)[0], 80, 140);
    }

    // ---- Insert ----

    [Fact]
    public void Insert_NoExpand_PastesIntoBase()
    {
        var b = Solid(6, 6, 50);
        var s = Solid(2, 2, 200);
        var ins = b.Insert(s, x: 2, y: 2);
        Assert.Equal(6, ins.Width);
        Assert.Equal(6, ins.Height);

        using var reg = new VipsRegion(ins);
        reg.Prepare(new VipsRect(0, 0, 6, 6));
        Assert.Equal(50, reg.GetAddress(0, 0)[0]);
        Assert.Equal(200, reg.GetAddress(2, 2)[0]);
        Assert.Equal(200, reg.GetAddress(3, 3)[0]);
        Assert.Equal(50, reg.GetAddress(5, 5)[0]);
    }

    [Fact]
    public void Insert_PartiallyOff_BaseClipsSub()
    {
        var b = Solid(4, 4, 0);
        var s = Solid(3, 3, 200);
        var ins = b.Insert(s, x: 2, y: 2);
        // Sub spans (2..4, 2..4) but base is only 4×4 — the (4,4) corner is outside.
        Assert.Equal(4, ins.Width);
        using var reg = new VipsRegion(ins);
        reg.Prepare(new VipsRect(0, 0, 4, 4));
        Assert.Equal(200, reg.GetAddress(2, 2)[0]);
        Assert.Equal(200, reg.GetAddress(3, 3)[0]);
    }

    [Fact]
    public void Insert_Expand_GrowsToBoundingBox()
    {
        var b = Solid(4, 4, 100);
        var s = Solid(3, 3, 200);
        var ins = b.Insert(s, x: 5, y: 5, expand: true, background: new[] { 50.0 });
        // Bounding box: (0,0) to (8,8) → 8×8.
        Assert.Equal(8, ins.Width);
        Assert.Equal(8, ins.Height);

        using var reg = new VipsRegion(ins);
        reg.Prepare(new VipsRect(0, 0, 8, 8));
        Assert.Equal(100, reg.GetAddress(0, 0)[0]); // base
        Assert.Equal(50, reg.GetAddress(4, 4)[0]);  // gap → background
        Assert.Equal(200, reg.GetAddress(5, 5)[0]); // sub
        Assert.Equal(200, reg.GetAddress(7, 7)[0]); // sub corner
    }

    [Fact]
    public void Insert_Expand_NegativeOffsetPadsLeft()
    {
        var b = Solid(2, 2, 100);
        var s = Solid(2, 2, 200);
        var ins = b.Insert(s, x: -1, y: -1, expand: true, background: new[] { 50.0 });
        // Bounding box = (-1, -1) to (2, 2) → 3×3.
        Assert.Equal(3, ins.Width);
        Assert.Equal(3, ins.Height);

        using var reg = new VipsRegion(ins);
        reg.Prepare(new VipsRect(0, 0, 3, 3));
        Assert.Equal(200, reg.GetAddress(0, 0)[0]); // sub TL
        Assert.Equal(100, reg.GetAddress(2, 2)[0]); // base BR (sub doesn't reach)
    }
}
