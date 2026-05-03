using System;
using CosmoImage.Operations.Drawing;
using Xunit;

namespace CosmoImage.Tests;

public class Round56Tests
{
    private static VipsImage RgbSolid(int w, int h, byte r, byte g, byte b)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 3, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? bb, ref bool stop) => {
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

    private static VipsImage RgbaSolid(int w, int h, byte r, byte g, byte b, byte a)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 4, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? aa, object? bb, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        addr[x * 4 + 0] = r;
                        addr[x * 4 + 1] = g;
                        addr[x * 4 + 2] = b;
                        addr[x * 4 + 3] = a;
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

    // ---- Normal (source-over baseline) ----

    [Fact]
    public void Blend_Normal_FullyOpaqueOverlay_ReplacesBase()
    {
        var b = RgbSolid(2, 2, 100, 100, 100);
        var o = RgbaSolid(2, 2, 200, 50, 30, 255);
        var blended = VipsImageOps.CompositeBlend(b, o, VipsBlendMode.Normal);
        var p = ReadPel(blended, 0, 0);
        Assert.Equal(200, p[0]);
        Assert.Equal(50, p[1]);
        Assert.Equal(30, p[2]);
    }

    [Fact]
    public void Blend_Normal_TransparentOverlay_PreservesBase()
    {
        var b = RgbSolid(2, 2, 100, 100, 100);
        var o = RgbaSolid(2, 2, 200, 50, 30, 0);
        var blended = VipsImageOps.CompositeBlend(b, o, VipsBlendMode.Normal);
        var p = ReadPel(blended, 0, 0);
        Assert.Equal(100, p[0]);
        Assert.Equal(100, p[1]);
        Assert.Equal(100, p[2]);
    }

    // ---- Multiply / Screen ----

    [Fact]
    public void Blend_Multiply_DarkensBase()
    {
        // 200 * 200 / 255 = 156.86.
        var b = RgbSolid(2, 2, 200, 200, 200);
        var o = RgbaSolid(2, 2, 200, 200, 200, 255);
        var blended = VipsImageOps.CompositeBlend(b, o, VipsBlendMode.Multiply);
        var p = ReadPel(blended, 0, 0);
        Assert.InRange(p[0], 155, 158);
    }

    [Fact]
    public void Blend_Screen_LightensBase()
    {
        // Screen(200, 100) = 255 - (55 * 155) / 255 = 255 - 33 = 222.
        var b = RgbSolid(2, 2, 200, 200, 200);
        var o = RgbaSolid(2, 2, 100, 100, 100, 255);
        var blended = VipsImageOps.CompositeBlend(b, o, VipsBlendMode.Screen);
        var p = ReadPel(blended, 0, 0);
        Assert.InRange(p[0], 220, 224);
    }

    // ---- Darken / Lighten ----

    [Fact]
    public void Blend_Darken_PicksMin()
    {
        var b = RgbSolid(2, 2, 200, 50, 100);
        var o = RgbaSolid(2, 2, 100, 100, 100, 255);
        var blended = VipsImageOps.CompositeBlend(b, o, VipsBlendMode.Darken);
        var p = ReadPel(blended, 0, 0);
        Assert.Equal(100, p[0]);  // min(200, 100)
        Assert.Equal(50, p[1]);   // min(50, 100)
        Assert.Equal(100, p[2]);  // min(100, 100)
    }

    [Fact]
    public void Blend_Lighten_PicksMax()
    {
        var b = RgbSolid(2, 2, 200, 50, 100);
        var o = RgbaSolid(2, 2, 100, 100, 100, 255);
        var blended = VipsImageOps.CompositeBlend(b, o, VipsBlendMode.Lighten);
        var p = ReadPel(blended, 0, 0);
        Assert.Equal(200, p[0]);
        Assert.Equal(100, p[1]);
        Assert.Equal(100, p[2]);
    }

    // ---- Add / Subtract ----

    [Fact]
    public void Blend_Add_ClampsAt255()
    {
        var b = RgbSolid(2, 2, 200, 200, 200);
        var o = RgbaSolid(2, 2, 100, 100, 100, 255);
        var blended = VipsImageOps.CompositeBlend(b, o, VipsBlendMode.Add);
        var p = ReadPel(blended, 0, 0);
        Assert.Equal(255, p[0]);
    }

    [Fact]
    public void Blend_Subtract_ClampsAtZero()
    {
        var b = RgbSolid(2, 2, 100, 100, 100);
        var o = RgbaSolid(2, 2, 200, 200, 200, 255);
        var blended = VipsImageOps.CompositeBlend(b, o, VipsBlendMode.Subtract);
        var p = ReadPel(blended, 0, 0);
        Assert.Equal(0, p[0]);
    }

    // ---- Difference ----

    [Fact]
    public void Blend_Difference_GivesAbsoluteGap()
    {
        var b = RgbSolid(2, 2, 200, 100, 50);
        var o = RgbaSolid(2, 2, 50, 100, 200, 255);
        var blended = VipsImageOps.CompositeBlend(b, o, VipsBlendMode.Difference);
        var p = ReadPel(blended, 0, 0);
        Assert.Equal(150, p[0]); // |200 - 50|
        Assert.Equal(0, p[1]);   // |100 - 100|
        Assert.Equal(150, p[2]); // |50 - 200|
    }

    // ---- Opacity ----

    [Fact]
    public void Blend_OpacityHalf_BlendsHalfway()
    {
        // Opacity 0.5 over Normal: out = 0.5 * overlay + 0.5 * base.
        var b = RgbSolid(2, 2, 0, 0, 0);
        var o = RgbaSolid(2, 2, 200, 200, 200, 255);
        var blended = VipsImageOps.CompositeBlend(b, o, VipsBlendMode.Normal, opacity: 0.5);
        var p = ReadPel(blended, 0, 0);
        Assert.Equal(100, p[0]);
    }

    // ---- DrawImage with offset ----

    [Fact]
    public void DrawImage_WithOffset_PaintsAtPoint()
    {
        var b = RgbSolid(8, 8, 0, 0, 0);
        var o = RgbaSolid(2, 2, 255, 255, 255, 255);
        var blended = VipsImageOps.DrawImage(b, o, x: 3, y: 3, VipsBlendMode.Normal);
        // (0, 0) is base. (3, 3)..(4, 4) is overlay.
        Assert.Equal(0, ReadPel(blended, 0, 0)[0]);
        Assert.Equal(255, ReadPel(blended, 3, 3)[0]);
        Assert.Equal(255, ReadPel(blended, 4, 4)[0]);
        Assert.Equal(0, ReadPel(blended, 5, 5)[0]);
    }

    // ---- Sanity: every mode runs ----

    [Fact]
    public void Blend_AllModes_DoNotThrow()
    {
        var b = RgbSolid(2, 2, 100, 100, 100);
        var o = RgbaSolid(2, 2, 200, 200, 200, 255);
        foreach (VipsBlendMode m in Enum.GetValues<VipsBlendMode>())
        {
            var x = VipsImageOps.CompositeBlend(b, o, m);
            Assert.Equal(3, x.Bands);
        }
    }
}
