using System;
using Xunit;

namespace CosmoImage.Tests;

public class Round43Tests
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

    /// <summary>Two-region image: left half value 0, right half value 100.</summary>
    private static VipsImage Halves(int w, int h)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                        addr[x] = (byte)((reg.Valid.Left + x) < w / 2 ? 0 : 100);
                }
                return 0;
            }
        };

    // ---- DrawCircle ----

    [Fact]
    public void DrawCircle_Outline_LeavesInteriorUntouched()
    {
        var img = Solid(20, 20, 50);
        var c = VipsImageOps.DrawCircle(img, cx: 10, cy: 10, radius: 5, ink: new byte[] { 255 });
        using var reg = new VipsRegion(c);
        reg.Prepare(new VipsRect(0, 0, 20, 20));
        // Centre stays unchanged.
        Assert.Equal(50, reg.GetAddress(10, 10)[0]);
        // A point on the circle at (15, 10) gets ink.
        Assert.Equal(255, reg.GetAddress(15, 10)[0]);
        // Far outside stays unchanged.
        Assert.Equal(50, reg.GetAddress(0, 0)[0]);
    }

    [Fact]
    public void DrawCircle_Filled_CoversInterior()
    {
        var img = Solid(20, 20, 50);
        var c = VipsImageOps.DrawCircle(img, cx: 10, cy: 10, radius: 5,
            ink: new byte[] { 200 }, fill: true);
        using var reg = new VipsRegion(c);
        reg.Prepare(new VipsRect(0, 0, 20, 20));
        Assert.Equal(200, reg.GetAddress(10, 10)[0]); // centre filled
        Assert.Equal(200, reg.GetAddress(13, 10)[0]); // inside ring
        Assert.Equal(50, reg.GetAddress(0, 0)[0]);    // outside unchanged
    }

    // ---- DrawFlood ----

    [Fact]
    public void DrawFlood_FillsConnectedRegion()
    {
        var img = Halves(10, 4);
        var f = VipsImageOps.DrawFlood(img, x: 0, y: 0, ink: new byte[] { 200 });
        using var reg = new VipsRegion(f);
        reg.Prepare(new VipsRect(0, 0, 10, 4));
        // Left half (was 0) is now 200; right half (was 100) untouched.
        Assert.Equal(200, reg.GetAddress(0, 0)[0]);
        Assert.Equal(200, reg.GetAddress(4, 3)[0]);
        Assert.Equal(100, reg.GetAddress(5, 0)[0]);
        Assert.Equal(100, reg.GetAddress(9, 3)[0]);
    }

    [Fact]
    public void DrawFlood_NoOpOnSameColour()
    {
        var img = Solid(8, 8, 100);
        var f = VipsImageOps.DrawFlood(img, x: 4, y: 4, ink: new byte[] { 100 });
        using var ri = new VipsRegion(img);
        using var rf = new VipsRegion(f);
        ri.Prepare(new VipsRect(0, 0, 8, 8));
        rf.Prepare(new VipsRect(0, 0, 8, 8));
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                Assert.Equal(ri.GetAddress(x, y)[0], rf.GetAddress(x, y)[0]);
    }

    // ---- DrawImage ----

    [Fact]
    public void DrawImage_PastesAtOffset()
    {
        var b = Solid(8, 8, 50);
        var s = Solid(2, 2, 200);
        var d = VipsImageOps.DrawImage(b, s, 3, 3);
        Assert.Equal(8, d.Width);
        using var reg = new VipsRegion(d);
        reg.Prepare(new VipsRect(0, 0, 8, 8));
        Assert.Equal(50, reg.GetAddress(0, 0)[0]);
        Assert.Equal(200, reg.GetAddress(3, 3)[0]);
        Assert.Equal(200, reg.GetAddress(4, 4)[0]);
        Assert.Equal(50, reg.GetAddress(7, 7)[0]);
    }

    [Fact]
    public void DrawImage_ClipsAtEdges()
    {
        var b = Solid(4, 4, 50);
        var s = Solid(3, 3, 200);
        var d = VipsImageOps.DrawImage(b, s, 2, 2); // sub goes (2..4, 2..4), partly off the right/bottom
        using var reg = new VipsRegion(d);
        reg.Prepare(new VipsRect(0, 0, 4, 4));
        Assert.Equal(200, reg.GetAddress(2, 2)[0]);
        Assert.Equal(200, reg.GetAddress(3, 3)[0]);
    }

    // ---- DrawMask ----

    [Fact]
    public void DrawMask_AppliesInkWhereMaskFull()
    {
        var b = Solid(4, 4, 50);
        var m = Solid(2, 2, 255);
        var d = VipsImageOps.DrawMask(b, m, 1, 1, ink: new byte[] { 200 });
        using var reg = new VipsRegion(d);
        reg.Prepare(new VipsRect(0, 0, 4, 4));
        Assert.Equal(50, reg.GetAddress(0, 0)[0]);
        Assert.Equal(200, reg.GetAddress(1, 1)[0]);
        Assert.Equal(200, reg.GetAddress(2, 2)[0]);
    }

    [Fact]
    public void DrawMask_BlendsWithPartialAlpha()
    {
        // Mask 128 → blend 50% base / 50% ink.
        var b = Solid(2, 2, 0);
        var m = Solid(2, 2, 128);
        var d = VipsImageOps.DrawMask(b, m, 0, 0, ink: new byte[] { 200 });
        using var reg = new VipsRegion(d);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        // With shift-based blend (alpha >> 8), result ≈ 100 (within 1 of true 0.5*200).
        Assert.InRange(reg.GetAddress(0, 0)[0], 99, 101);
    }

    // ---- DrawSmudge ----

    [Fact]
    public void DrawSmudge_AveragesAcrossSeam()
    {
        // Halves image (left=0, right=100). Smudge across the seam
        // averages pixels in the smudge rect toward 50.
        var img = Halves(10, 4);
        var sm = VipsImageOps.DrawSmudge(img, x: 4, y: 0, width: 2, height: 4);
        using var reg = new VipsRegion(sm);
        reg.Prepare(new VipsRect(0, 0, 10, 4));
        // Pixel at the seam (x=4, was 0) now sees 0/0/0/0/0/100/100/100/100 = avg ≈ 33.
        // Pixel at (x=5, was 100) sees the same neighbourhood from other side.
        byte v0 = reg.GetAddress(4, 1)[0];
        byte v1 = reg.GetAddress(5, 1)[0];
        Assert.InRange(v0, 20, 50);
        Assert.InRange(v1, 50, 80);
        // Pixels well inside the un-smudged region stay original.
        Assert.Equal(0, reg.GetAddress(0, 1)[0]);
        Assert.Equal(100, reg.GetAddress(9, 1)[0]);
    }

    [Fact]
    public void DrawSmudge_NoOpOutsideRect()
    {
        var img = Halves(10, 4);
        var sm = VipsImageOps.DrawSmudge(img, x: 0, y: 0, width: 0, height: 0);
        using var ri = new VipsRegion(img);
        using var rs = new VipsRegion(sm);
        ri.Prepare(new VipsRect(0, 0, 10, 4));
        rs.Prepare(new VipsRect(0, 0, 10, 4));
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 10; x++)
                Assert.Equal(ri.GetAddress(x, y)[0], rs.GetAddress(x, y)[0]);
    }
}
