using System;
using System.Linq;
using CosmoImage.Operations.Drawing;
using Xunit;

namespace CosmoImage.Tests;

public class Round65Tests
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

    /// <summary>Walk a horizontal painted line; count sequences of painted/unpainted runs.</summary>
    private static int CountRuns(VipsImage img, int y)
    {
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, img.Width, img.Height));
        int runs = 0;
        bool? lastPainted = null;
        for (int x = 0; x < img.Width; x++)
        {
            bool painted = reg.GetAddress(x, y)[0] >= 100;
            if (lastPainted == null || painted != lastPainted.Value)
            {
                runs++;
                lastPainted = painted;
            }
        }
        return runs;
    }

    // ---- Pen with dashes ----

    [Fact]
    public void VipsPen_AcceptsDashesArray()
    {
        var pen = new VipsPen(new VipsSolidBrush(0, 0, 0), 2,
            dashes: new[] { 4.0, 2.0 });
        Assert.NotNull(pen.Dashes);
        Assert.Equal(2, pen.Dashes!.Length);
        Assert.Equal(4.0, pen.Dashes[0]);
    }

    [Fact]
    public void VipsPen_RejectsEmptyDashesArray()
    {
        Assert.Throws<ArgumentException>(() =>
            new VipsPen(new VipsSolidBrush(0, 0, 0), 2, dashes: Array.Empty<double>()));
    }

    [Fact]
    public void VipsPen_RejectsAllZeroDashes()
    {
        Assert.Throws<ArgumentException>(() =>
            new VipsPen(new VipsSolidBrush(0, 0, 0), 2, dashes: new[] { 0.0, 0.0 }));
    }

    [Fact]
    public void VipsPen_RejectsNegativeDashes()
    {
        Assert.Throws<ArgumentException>(() =>
            new VipsPen(new VipsSolidBrush(0, 0, 0), 2, dashes: new[] { 4.0, -1.0 }));
    }

    [Fact]
    public void Dashed_FactoryConvenience()
    {
        var pen = VipsPen.Dashed(255, 0, 0, 2, 5, 3);
        Assert.NotNull(pen.Dashes);
        Assert.Equal(new[] { 5.0, 3.0 }, pen.Dashes);
    }

    // ---- Dashed line strokes ----

    [Fact]
    public void DashedLine_ProducesAlternatingRuns()
    {
        // 60-px line; dash 4 on / 4 off → ~7 dashes visible.
        var bg = RgbSolid(64, 4, 0, 0, 0);
        var pen = VipsPen.Dashed(255, 255, 255, 2, 4, 4);
        var painted = VipsImageOps.StrokeLine(bg, pen, 2, 2, 62, 2, aa: false);

        // CountRuns on the centre row counts unique on/off transitions.
        int runs = CountRuns(painted, 2);
        // For a 60-px line dashed 4-on/4-off, expect ~7-8 painted segments
        // → 14-16 runs (alternating). At a minimum, more than 3 (which
        // would mean a solid stroke + 2 background ends).
        Assert.True(runs > 5, $"expected multiple dash segments, got {runs} runs");
    }

    [Fact]
    public void SolidPen_AndDashlessPath_StillProducesContiguousLine()
    {
        // No dashes → solid stroke; only 3 runs total: bg, painted, bg.
        var bg = RgbSolid(64, 4, 0, 0, 0);
        var pen = VipsPen.Solid(255, 255, 255, 2);
        var painted = VipsImageOps.StrokeLine(bg, pen, 2, 2, 62, 2, aa: false);
        int runs = CountRuns(painted, 2);
        Assert.Equal(3, runs);
    }

    [Fact]
    public void DashOffset_ShiftsPattern()
    {
        // Same dash pattern, two different offsets — pattern shifts.
        var bg = RgbSolid(64, 4, 0, 0, 0);
        var penA = new VipsPen(new VipsSolidBrush(255, 255, 255), 2,
            dashes: new[] { 4.0, 4.0 }, dashOffset: 0);
        var penB = new VipsPen(new VipsSolidBrush(255, 255, 255), 2,
            dashes: new[] { 4.0, 4.0 }, dashOffset: 4);

        var paintedA = VipsImageOps.StrokeLine(bg, penA, 2, 2, 62, 2, aa: false);
        var paintedB = VipsImageOps.StrokeLine(bg, penB, 2, 2, 62, 2, aa: false);

        // Where penA paints, penB likely doesn't (and vice versa) — the
        // patterns are anti-aligned.
        bool foundDifference = false;
        for (int x = 4; x < 60 && !foundDifference; x++)
        {
            int a = ReadPel(paintedA, x, 2)[0];
            int b = ReadPel(paintedB, x, 2)[0];
            if ((a >= 100) != (b >= 100)) foundDifference = true;
        }
        Assert.True(foundDifference);
    }

    [Fact]
    public void DashedRectangle_OutlineHasGaps()
    {
        // Stroke a rectangle's outline with dashes — corners + gaps
        // along each side. The total painted area is less than a
        // solid stroke.
        var bg = RgbSolid(40, 40, 0, 0, 0);
        var solidPen = VipsPen.Solid(255, 255, 255, 2);
        var dashedPen = VipsPen.Dashed(255, 255, 255, 2, 3, 3);
        var solid = VipsImageOps.StrokeRectangle(bg, solidPen, 5, 5, 30, 30, aa: false);
        var dashed = VipsImageOps.StrokeRectangle(bg, dashedPen, 5, 5, 30, 30, aa: false);

        int Painted(VipsImage img)
        {
            using var reg = new VipsRegion(img);
            reg.Prepare(new VipsRect(0, 0, img.Width, img.Height));
            int c = 0;
            for (int y = 0; y < img.Height; y++)
                for (int x = 0; x < img.Width; x++)
                    if (reg.GetAddress(x, y)[0] >= 100) c++;
            return c;
        }
        // Dashed should paint roughly half (3-on / 3-off cycle).
        int s = Painted(solid);
        int d = Painted(dashed);
        Assert.True(d < s);
        Assert.InRange(d * 1.0 / s, 0.3, 0.7);
    }
}
