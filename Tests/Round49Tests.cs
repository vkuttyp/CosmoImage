using System;
using System.Buffers.Binary;
using CosmoImage.Operations.Convolution;
using Xunit;

namespace CosmoImage.Tests;

public class Round49Tests
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

    private static VipsImage VStep(int w, int h, int splitCol)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                        addr[x] = (byte)((reg.Valid.Left + x) >= splitCol ? 255 : 0);
                }
                return 0;
            }
        };

    // ---- ConvSep ----

    [Fact]
    public void ConvSep_NormalisedBox_AveragesIntoFlatRegion()
    {
        // 5-tap normalised box: each weight = 0.2.
        var box = new double[] { 0.2, 0.2, 0.2, 0.2, 0.2 };
        var src = Solid(8, 8, 100);
        var blurred = src.ConvSep(box);
        using var reg = new VipsRegion(blurred);
        reg.Prepare(new VipsRect(0, 0, 8, 8));
        // Constant input → output stays the constant.
        Assert.Equal(100, reg.GetAddress(4, 4)[0]);
    }

    [Fact]
    public void ConvSep_GaussianAttenuatesStepEdge()
    {
        // 3-tap gaussian: 0.25, 0.5, 0.25.
        var g = new double[] { 0.25, 0.5, 0.25 };
        var src = VStep(20, 4, splitCol: 10);
        var blurred = src.ConvSep(g);
        using var reg = new VipsRegion(blurred);
        reg.Prepare(new VipsRect(0, 0, 20, 4));
        // Far from seam: original values (clamped 0 / 255). At seam: ~mid.
        // Conv1D zero-pads at edges, so we sample inside the white region
        // (3+ pixels from each border) where the kernel sees only 255s.
        Assert.Equal(0, reg.GetAddress(3, 1)[0]);
        Assert.Equal(255, reg.GetAddress(15, 1)[0]);
        // Right at the seam (column 9 was 0, column 10 was 255):
        // out[9] ≈ 0.25·0 + 0.5·0 + 0.25·255 ≈ 64.
        // out[10] ≈ 0.25·0 + 0.5·255 + 0.25·255 ≈ 191.
        Assert.InRange(reg.GetAddress(9, 1)[0], 60, 70);
        Assert.InRange(reg.GetAddress(10, 1)[0], 188, 195);
    }

    // ---- BoxBlur ----

    [Fact]
    public void BoxBlur_FlatImage_LeavesValueUnchanged()
    {
        var src = Solid(8, 8, 100);
        var blurred = src.BoxBlur(radius: 2, passes: 3);
        using var reg = new VipsRegion(blurred);
        reg.Prepare(new VipsRect(0, 0, 8, 8));
        Assert.Equal(100, reg.GetAddress(4, 4)[0]);
    }

    [Fact]
    public void BoxBlur_StepEdge_SmoothsTransition()
    {
        var src = VStep(20, 8, splitCol: 10);
        var blurred = src.BoxBlur(radius: 2, passes: 1);
        using var reg = new VipsRegion(blurred);
        reg.Prepare(new VipsRect(0, 0, 20, 8));
        // Far from seam stays near original.
        Assert.InRange(reg.GetAddress(0, 4)[0], 0, 5);
        Assert.InRange(reg.GetAddress(19, 4)[0], 250, 255);
        // At seam, value lands somewhere in the middle.
        Assert.InRange(reg.GetAddress(10, 4)[0], 100, 200);
    }

    [Fact]
    public void BoxBlur_MultiplePasses_SmootherThanOnePass()
    {
        // After more box passes, the response at the seam should be
        // closer to the average (127ish) than after one pass.
        var src = VStep(40, 8, splitCol: 20);
        var one = src.BoxBlur(radius: 3, passes: 1);
        var three = src.BoxBlur(radius: 3, passes: 3);
        using var r1 = new VipsRegion(one);
        using var r3 = new VipsRegion(three);
        r1.Prepare(new VipsRect(0, 0, 40, 8));
        r3.Prepare(new VipsRect(0, 0, 40, 8));
        // Probe a point near the seam — expect three-pass result smoother.
        // We sample at x=23 (3 px right of seam).
        int v1 = r1.GetAddress(23, 4)[0];
        int v3 = r3.GetAddress(23, 4)[0];
        int oneDelta = Math.Abs(v1 - 127);
        int threeDelta = Math.Abs(v3 - 127);
        Assert.True(threeDelta <= oneDelta + 5,
            $"three-pass should be ≥ as smooth: 1-pass {v1} (Δ {oneDelta}) vs 3-pass {v3} (Δ {threeDelta})");
    }

    // ---- Edge dispatcher ----

    [Fact]
    public void Edge_DispatchesToSobel()
    {
        var src = VStep(10, 10, splitCol: 5);
        var ed = VipsImageOps.Edge(src, VipsEdgeMethod.Sobel);
        using var reg = new VipsRegion(ed);
        reg.Prepare(new VipsRect(0, 0, 10, 10));
        // Sobel maxes out at the seam.
        Assert.Equal(255, reg.GetAddress(4, 5)[0]);
    }

    [Fact]
    public void Edge_DispatchesToCompass()
    {
        var src = VStep(10, 10, splitCol: 5);
        var ed = VipsImageOps.Edge(src, VipsEdgeMethod.Compass);
        using var reg = new VipsRegion(ed);
        reg.Prepare(new VipsRect(0, 0, 10, 10));
        Assert.True(reg.GetAddress(4, 5)[0] > 100);
    }

    [Fact]
    public void Edge_DispatchesToCanny()
    {
        var src = VStep(20, 20, splitCol: 10);
        var ed = VipsImageOps.Edge(src, VipsEdgeMethod.Canny);
        using var reg = new VipsRegion(ed);
        reg.Prepare(new VipsRect(0, 0, 20, 20));
        // Some pixel near the seam should be a 255-edge.
        bool found = false;
        for (int y = 5; y < 15 && !found; y++)
            for (int x = 7; x < 13 && !found; x++)
                if (reg.GetAddress(x, y)[0] == 255) found = true;
        Assert.True(found);
    }
}
