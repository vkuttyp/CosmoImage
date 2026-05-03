using System;
using System.Buffers.Binary;
using Xunit;

namespace CosmoImage.Tests;

public class Round44Tests
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

    private static VipsImage RampX(int w, int h)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                        addr[x] = (byte)(reg.Valid.Left + x);
                }
                return 0;
            }
        };

    /// <summary>Image with a non-background blob in the centre 4×4 of an 8×8 canvas.</summary>
    private static VipsImage BlobInCentre(byte bg, byte fg)
        => new VipsImage
        {
            Width = 8, Height = 8, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    int gy = reg.Valid.Top + y;
                    var addr = reg.GetAddress(reg.Valid.Left, gy);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        int gx = reg.Valid.Left + x;
                        bool inBlob = gx >= 2 && gx <= 5 && gy >= 2 && gy <= 5;
                        addr[x] = inBlob ? fg : bg;
                    }
                }
                return 0;
            }
        };

    // ---- Sum ----

    [Fact]
    public void Sum_AddsBytewise()
    {
        var a = Solid(2, 2, 50);
        var b = Solid(2, 2, 60);
        var c = Solid(2, 2, 70);
        var s = VipsImageOps.Sum(a, b, c);
        using var reg = new VipsRegion(s);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        Assert.Equal(180, reg.GetAddress(0, 0)[0]);
    }

    [Fact]
    public void Sum_ClampsAt255()
    {
        var a = Solid(2, 2, 200);
        var b = Solid(2, 2, 200);
        var s = VipsImageOps.Sum(a, b);
        using var reg = new VipsRegion(s);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        Assert.Equal(255, reg.GetAddress(0, 0)[0]);
    }

    [Fact]
    public void Sum_SingleInput_PassesThrough()
    {
        var a = Solid(2, 2, 100);
        var s = VipsImageOps.Sum(a);
        Assert.Same(a, s);
    }

    // ---- MinImage / MaxImage ----

    [Fact]
    public void MinImage_PicksLowestPerPixel()
    {
        var a = Solid(2, 2, 50);
        var b = Solid(2, 2, 100);
        var c = Solid(2, 2, 200);
        var min = VipsImageOps.MinImage(a, b, c);
        using var reg = new VipsRegion(min);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        Assert.Equal(50, reg.GetAddress(0, 0)[0]);
    }

    [Fact]
    public void MaxImage_PicksHighestPerPixel()
    {
        var a = Solid(2, 2, 50);
        var b = Solid(2, 2, 200);
        var max = VipsImageOps.MaxImage(a, b);
        using var reg = new VipsRegion(max);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        Assert.Equal(200, reg.GetAddress(0, 0)[0]);
    }

    // ---- Project ----

    [Fact]
    public void Project_RampX_ColumnsSumToConstant()
    {
        // RampX(8, 4): pixel x = x. Column sum at col x = 4·x.
        var src = RampX(8, 4);
        var (cols, rows) = VipsImageOps.Project(src);
        Assert.Equal(8, cols.Width);
        Assert.Equal(1, cols.Height);
        Assert.Equal(1, rows.Width);
        Assert.Equal(4, rows.Height);

        using var rc = new VipsRegion(cols);
        rc.Prepare(new VipsRect(0, 0, 8, 1));
        for (int x = 0; x < 8; x++)
        {
            float v = BinaryPrimitives.ReadSingleLittleEndian(rc.GetAddress(x, 0).Slice(0, 4));
            Assert.Equal(4 * x, v, 3);
        }

        // Each row sum is 0+1+2+...+7 = 28, regardless of y.
        using var rr = new VipsRegion(rows);
        rr.Prepare(new VipsRect(0, 0, 1, 4));
        for (int y = 0; y < 4; y++)
        {
            float v = BinaryPrimitives.ReadSingleLittleEndian(rr.GetAddress(0, y).Slice(0, 4));
            Assert.Equal(28, v, 3);
        }
    }

    // ---- FindTrim ----

    [Fact]
    public void FindTrim_BlobInCentre_GivesTightBox()
    {
        var img = BlobInCentre(bg: 0, fg: 200);
        var rect = VipsImageOps.FindTrim(img);
        Assert.Equal(2, rect.Left);
        Assert.Equal(2, rect.Top);
        Assert.Equal(4, rect.Width);
        Assert.Equal(4, rect.Height);
    }

    [Fact]
    public void FindTrim_UniformImage_ReturnsZeroRect()
    {
        var img = Solid(8, 8, 100);
        var rect = VipsImageOps.FindTrim(img);
        Assert.Equal(0, rect.Width);
        Assert.Equal(0, rect.Height);
    }

    [Fact]
    public void FindTrim_ExplicitBackground_ChangesResult()
    {
        var img = BlobInCentre(bg: 0, fg: 200);
        // If we declare background = 200 (the blob colour), then the
        // border is what looks "non-background".
        var rect = VipsImageOps.FindTrim(img, background: new[] { 200.0 });
        // Border spans the whole image except the blob (rows 0..7, cols 0..7).
        Assert.Equal(8, rect.Width);
        Assert.Equal(8, rect.Height);
    }
}
