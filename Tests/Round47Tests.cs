using System;
using System.Buffers.Binary;
using CosmoImage.Operations.Analysis;
using Xunit;

namespace CosmoImage.Tests;

public class Round47Tests
{
    /// <summary>Build a UChar 1-band image with `pixelSetter` filling each pixel.</summary>
    private static VipsImage UCharGen(int w, int h, Func<int, int, byte> fn)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                        addr[x] = fn(reg.Valid.Left + x, reg.Valid.Top + y);
                }
                return 0;
            }
        };

    private static uint ReadUInt(VipsRegion reg, int x, int y)
        => BinaryPrimitives.ReadUInt32LittleEndian(reg.GetAddress(x, y).Slice(0, 4));

    // ---- HoughLine ----

    [Fact]
    public void HoughLine_HorizontalLine_PeaksAtThetaPiOver2()
    {
        // 32×32 image with a single horizontal line at y=15.
        var src = UCharGen(32, 32, (x, y) => (byte)(y == 15 ? 255 : 0));
        var h = src.HoughLine(width: 180, height: 200, threshold: 128);
        Assert.Equal(180, h.Width);

        using var reg = new VipsRegion(h);
        reg.Prepare(new VipsRect(0, 0, 180, 200));
        // Find the peak.
        int bestX = 0, bestY = 0;
        uint bestV = 0;
        for (int y = 0; y < 200; y++)
            for (int x = 0; x < 180; x++)
            {
                uint v = ReadUInt(reg, x, y);
                if (v > bestV) { bestV = v; bestX = x; bestY = y; }
            }
        // Horizontal line: ρ = x cos θ + y sin θ. With θ = π/2, ρ = y.
        // θ-bin for π/2 is 90 (out of 180).
        Assert.InRange(bestX, 88, 92);
        Assert.True(bestV >= 30, $"expected strong peak, got {bestV}");
    }

    // ---- HoughCircle ----

    [Fact]
    public void HoughCircle_CircleOutline_PeaksAtCentre()
    {
        // 32×32 image with a Bresenham circle at (16, 16) radius 8.
        var circlePixels = new bool[32 * 32];
        int cx = 16, cy = 16, rr = 8;
        int xx = rr, yy = 0, err = 0;
        while (xx >= yy)
        {
            int[] dxs = { +xx, +xx, -xx, -xx, +yy, +yy, -yy, -yy };
            int[] dys = { +yy, -yy, +yy, -yy, +xx, -xx, +xx, -xx };
            for (int i = 0; i < 8; i++)
            {
                int px = cx + dxs[i], py = cy + dys[i];
                if (px >= 0 && px < 32 && py >= 0 && py < 32)
                    circlePixels[py * 32 + px] = true;
            }
            yy++;
            if (err <= 0) err += 2 * yy + 1;
            else { xx--; err += 2 * (yy - xx) + 1; }
        }
        var src = UCharGen(32, 32, (x, y) => (byte)(circlePixels[y * 32 + x] ? 255 : 0));
        var h = src.HoughCircle(minRadius: 8, maxRadius: 8, threshold: 128);

        using var reg = new VipsRegion(h);
        reg.Prepare(new VipsRect(0, 0, 32, 32));
        // Peak should land at (16, 16) — every edge pixel votes for it.
        int peakX = 0, peakY = 0; uint peakV = 0;
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
            {
                uint v = ReadUInt(reg, x, y);
                if (v > peakV) { peakV = v; peakX = x; peakY = y; }
            }
        Assert.InRange(peakX, 15, 17);
        Assert.InRange(peakY, 15, 17);
    }

    // ---- HistFindIndexed ----

    [Fact]
    public void HistFindIndexed_SumGroupsByIndex()
    {
        // Input: 4 pixels (10, 20, 30, 40). Index: (0, 0, 1, 1).
        // Sum: bin 0 = 30, bin 1 = 70.
        var inp = new VipsImage
        {
            Width = 4, Height = 1, Bands = 1, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                var addr = reg.GetAddress(0, 0);
                addr[0] = 10; addr[1] = 20; addr[2] = 30; addr[3] = 40;
                return 0;
            }
        };
        var idx = new VipsImage
        {
            Width = 4, Height = 1, Bands = 1, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                var addr = reg.GetAddress(0, 0);
                addr[0] = 0; addr[1] = 0; addr[2] = 1; addr[3] = 1;
                return 0;
            }
        };
        var hist = inp.HistFindIndexed(idx);
        Assert.Equal(2, hist.Width);
        using var reg = new VipsRegion(hist);
        reg.Prepare(new VipsRect(0, 0, 2, 1));
        Assert.Equal(30f, BinaryPrimitives.ReadSingleLittleEndian(reg.GetAddress(0, 0).Slice(0, 4)));
        Assert.Equal(70f, BinaryPrimitives.ReadSingleLittleEndian(reg.GetAddress(1, 0).Slice(0, 4)));
    }

    [Fact]
    public void HistFindIndexed_MeanDividesBySize()
    {
        var inp = new VipsImage
        {
            Width = 4, Height = 1, Bands = 1, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                var addr = reg.GetAddress(0, 0);
                addr[0] = 10; addr[1] = 30; addr[2] = 100; addr[3] = 200;
                return 0;
            }
        };
        var idx = new VipsImage
        {
            Width = 4, Height = 1, Bands = 1, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                var addr = reg.GetAddress(0, 0);
                addr[0] = 0; addr[1] = 0; addr[2] = 1; addr[3] = 1;
                return 0;
            }
        };
        var hist = inp.HistFindIndexed(idx, VipsHistIndexedReduction.Mean);
        using var reg = new VipsRegion(hist);
        reg.Prepare(new VipsRect(0, 0, 2, 1));
        Assert.Equal(20f, BinaryPrimitives.ReadSingleLittleEndian(reg.GetAddress(0, 0).Slice(0, 4)));
        Assert.Equal(150f, BinaryPrimitives.ReadSingleLittleEndian(reg.GetAddress(1, 0).Slice(0, 4)));
    }

    // ---- HistPlot ----

    [Fact]
    public void HistPlot_PaintsBarsProportionally()
    {
        // Hist of length 4 with values (10, 20, 0, 5). Tallest bar = 20.
        var hist = new VipsImage
        {
            Width = 4, Height = 1, Bands = 1, BandFormat = VipsBandFormat.UInt,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                var addr = reg.GetAddress(0, 0);
                BinaryPrimitives.WriteUInt32LittleEndian(addr.Slice(0, 4), 10);
                BinaryPrimitives.WriteUInt32LittleEndian(addr.Slice(4, 4), 20);
                BinaryPrimitives.WriteUInt32LittleEndian(addr.Slice(8, 4), 0);
                BinaryPrimitives.WriteUInt32LittleEndian(addr.Slice(12, 4), 5);
                return 0;
            }
        };
        var plot = VipsImageOps.HistPlot(hist, height: 100);
        Assert.Equal(4, plot.Width);
        Assert.Equal(100, plot.Height);

        using var reg = new VipsRegion(plot);
        reg.Prepare(new VipsRect(0, 0, 4, 100));
        // Tallest bar (col 1) → painted from y=0 to y=99.
        Assert.Equal(255, reg.GetAddress(1, 0)[0]);
        // Empty bar (col 2) → all black.
        for (int y = 0; y < 100; y++)
            Assert.Equal(0, reg.GetAddress(2, y)[0]);
        // Half-tallest bar (col 0, value 10/20 = 0.5) → bottom half painted.
        Assert.Equal(0, reg.GetAddress(0, 49)[0]);
        Assert.Equal(255, reg.GetAddress(0, 51)[0]);
    }
}
