using System;
using CosmoImage.Operations.Drawing;
using Xunit;

namespace CosmoImage.Tests;

public class Round63Tests
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

    // ---- AA produces partial coverage at edges ----

    [Fact]
    public void FillCircle_AA_HasIntermediateValuesOnEdge()
    {
        var bg = RgbSolid(40, 40, 0, 0, 0);
        var brush = new VipsSolidBrush(255, 255, 255);
        var painted = VipsImageOps.FillCircle(bg, brush, cx: 20, cy: 20, radius: 10, aa: true);
        // Centre pixel: fully inside → 255.
        Assert.Equal(255, ReadPel(painted, 20, 20)[0]);
        // Far outside: 0.
        Assert.Equal(0, ReadPel(painted, 0, 0)[0]);
        // The circle edge runs through pixels around radius ~10 from
        // centre. At least one of those should land at an intermediate
        // value — that's the AA signature.
        bool foundEdge = false;
        for (int x = 9; x <= 31 && !foundEdge; x++)
            for (int y = 9; y <= 31 && !foundEdge; y++)
            {
                int v = ReadPel(painted, x, y)[0];
                if (v > 10 && v < 245) foundEdge = true;
            }
        Assert.True(foundEdge, "AA fill should produce at least one partially-covered edge pixel");
    }

    [Fact]
    public void FillCircle_NoAA_OnlyHardEdges()
    {
        var bg = RgbSolid(40, 40, 0, 0, 0);
        var brush = new VipsSolidBrush(255, 255, 255);
        var painted = VipsImageOps.FillCircle(bg, brush, cx: 20, cy: 20, radius: 10, aa: false);
        // Without AA, every pixel is either 0 or 255 — no intermediates.
        for (int y = 0; y < 40; y++)
            for (int x = 0; x < 40; x++)
            {
                int v = ReadPel(painted, x, y)[0];
                Assert.True(v == 0 || v == 255, $"non-AA fill should be binary, got {v} at ({x}, {y})");
            }
    }

    // ---- AA blends with non-zero base ----

    [Fact]
    public void Fill_AA_BlendsAgainstColouredBase()
    {
        var bg = RgbSolid(20, 20, 100, 100, 100);  // mid-grey base
        var brush = new VipsSolidBrush(255, 0, 0);
        // Tilted rectangle to force partial-coverage at the corners.
        var path = new VipsPath()
            .MoveTo(5, 8.5).LineTo(15, 8.5).LineTo(15, 11.5).LineTo(5, 11.5).Close();
        var painted = VipsImageOps.FillPath(bg, path, brush, aa: true);
        // Pixel (10, 8) center y=8.5: top edge is exactly here, so
        // half-coverage. Expected channel 0 ≈ 0.5 · 255 + 0.5 · 100 ≈ 178.
        var top = ReadPel(painted, 10, 8);
        Assert.InRange(top[0], 160, 200);
    }

    // ---- StrokePath inherits AA ----

    [Fact]
    public void StrokeLine_AA_ProducesSoftEdges()
    {
        var bg = RgbSolid(20, 20, 0, 0, 0);
        var pen = VipsPen.Solid(255, 255, 255, width: 2);
        // Tilted line — its stroked outline has slanted edges that AA
        // softens.
        var painted = VipsImageOps.StrokeLine(bg, pen, 2, 2, 18, 18, aa: true);
        bool foundSoft = false;
        for (int y = 0; y < 20 && !foundSoft; y++)
            for (int x = 0; x < 20 && !foundSoft; x++)
            {
                int v = ReadPel(painted, x, y)[0];
                if (v > 30 && v < 230) foundSoft = true;
            }
        Assert.True(foundSoft);
    }

    [Fact]
    public void StrokeLine_NoAA_BinaryEdges()
    {
        var bg = RgbSolid(20, 20, 0, 0, 0);
        var pen = VipsPen.Solid(255, 255, 255, width: 2);
        var painted = VipsImageOps.StrokeLine(bg, pen, 2, 2, 18, 18, aa: false);
        for (int y = 0; y < 20; y++)
            for (int x = 0; x < 20; x++)
            {
                int v = ReadPel(painted, x, y)[0];
                Assert.True(v == 0 || v == 255);
            }
    }

    // ---- Default behaviour ----

    [Fact]
    public void Fill_DefaultIsAA()
    {
        // Default-arg `aa` is true: the same call without specifying
        // aa should produce the AA result.
        var bg = RgbSolid(40, 40, 0, 0, 0);
        var brush = new VipsSolidBrush(255, 255, 255);
        var withDefault = VipsImageOps.FillCircle(bg, brush, 20, 20, 10);
        var withAa = VipsImageOps.FillCircle(bg, brush, 20, 20, 10, aa: true);
        // Pixel-perfect equality at every position.
        for (int y = 0; y < 40; y++)
            for (int x = 0; x < 40; x++)
                Assert.Equal(ReadPel(withDefault, x, y)[0], ReadPel(withAa, x, y)[0]);
    }
}
