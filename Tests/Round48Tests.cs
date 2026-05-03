using System;
using System.Buffers.Binary;
using Xunit;

namespace CosmoImage.Tests;

public class Round48Tests
{
    private static VipsImage UCharGen(int w, int h, int bands, Func<int, int, int, byte> fn)
        => new VipsImage
        {
            Width = w, Height = h, Bands = bands, BandFormat = VipsBandFormat.UChar,
            Interpretation = bands == 1 ? VipsInterpretation.BW : VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                        for (int bnd = 0; bnd < bands; bnd++)
                            addr[x * bands + bnd] = fn(reg.Valid.Left + x, reg.Valid.Top + y, bnd);
                }
                return 0;
            }
        };

    private static uint ReadUInt(VipsRegion reg, int x, int y, int band = 0, int bands = 1)
        => BinaryPrimitives.ReadUInt32LittleEndian(reg.GetAddress(x, y).Slice((band) * 4, 4));

    // ---- HistFindNDim ----

    [Fact]
    public void HistFindNDim_OneBand_BehavesLikeHistFind()
    {
        var src = UCharGen(4, 1, 1, (x, y, b) => (byte)(x * 64));
        // Values: 0, 64, 128, 192. With bins=4, mapped to 0, 1, 2, 3.
        var hist = VipsImageOps.HistFindNDim(src, bins: 4);
        Assert.Equal(4, hist.Width);
        Assert.Equal(1, hist.Height);
        Assert.Equal(1, hist.Bands);

        using var reg = new VipsRegion(hist);
        reg.Prepare(new VipsRect(0, 0, 4, 1));
        for (int x = 0; x < 4; x++)
            Assert.Equal(1u, ReadUInt(reg, x, 0));
    }

    [Fact]
    public void HistFindNDim_TwoBand_GivesBinsByBins()
    {
        // 4 pixels, bands (R, G): (0, 0), (0, 128), (128, 0), (128, 128).
        // bins=2 maps to (0,0), (0,1), (1,0), (1,1) → one count per bin.
        byte[][] vals = { new byte[] { 0, 0 }, new byte[] { 0, 128 },
                          new byte[] { 128, 0 }, new byte[] { 128, 128 } };
        var src = UCharGen(4, 1, 2, (x, y, b) => vals[x][b]);
        var hist = VipsImageOps.HistFindNDim(src, bins: 2);
        Assert.Equal(2, hist.Width);
        Assert.Equal(2, hist.Height);
        Assert.Equal(1, hist.Bands);

        using var reg = new VipsRegion(hist);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        Assert.Equal(1u, ReadUInt(reg, 0, 0));
        Assert.Equal(1u, ReadUInt(reg, 1, 0));
        Assert.Equal(1u, ReadUInt(reg, 0, 1));
        Assert.Equal(1u, ReadUInt(reg, 1, 1));
    }

    [Fact]
    public void HistFindNDim_ThreeBand_OutputsBinsBands()
    {
        // 1-pixel image with (R, G, B) = (0, 128, 200). bins=4.
        // R bin: 0 / 64 = 0; G bin: 128 / 64 = 2; B bin: 200 / 64 = 3.
        var src = UCharGen(1, 1, 3, (x, y, b) => b == 0 ? (byte)0 : b == 1 ? (byte)128 : (byte)200);
        var hist = VipsImageOps.HistFindNDim(src, bins: 4);
        Assert.Equal(4, hist.Width);
        Assert.Equal(4, hist.Height);
        Assert.Equal(4, hist.Bands);

        // Layout: (bx=0, by=2) at band bz=3 → offset at pixel (0, 2), band 3.
        using var reg = new VipsRegion(hist);
        reg.Prepare(new VipsRect(0, 0, 4, 4));
        Assert.Equal(1u, ReadUInt(reg, 0, 2, band: 3, bands: 4));
        // Other cells should be zero.
        Assert.Equal(0u, ReadUInt(reg, 0, 0, band: 0, bands: 4));
    }

    // ---- Getpoint ----

    [Fact]
    public void Getpoint_UCharRGB()
    {
        var src = UCharGen(4, 4, 3, (x, y, b) => (byte)(x * 5 + y * 20 + b));
        var p = VipsImageOps.Getpoint(src, 2, 3);
        Assert.Equal(3, p.Length);
        Assert.Equal(70, p[0]); // 2*5 + 3*20 + 0
        Assert.Equal(71, p[1]);
        Assert.Equal(72, p[2]);
    }

    [Fact]
    public void Getpoint_OutOfBoundsThrows()
    {
        var src = UCharGen(4, 4, 1, (x, y, b) => 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => VipsImageOps.Getpoint(src, -1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => VipsImageOps.Getpoint(src, 4, 0));
    }

    // ---- Profile ----

    [Fact]
    public void Profile_FindsTopLeftEdges()
    {
        // 4×4 with non-zero content starting at (1, 2).
        var src = UCharGen(4, 4, 1, (x, y, b) => (byte)(x >= 1 && y >= 2 ? 100 : 0));
        var (cols, rows) = VipsImageOps.Profile(src);
        Assert.Equal(4, cols.Width);
        Assert.Equal(1, cols.Height);
        Assert.Equal(1, rows.Width);
        Assert.Equal(4, rows.Height);

        using var rc = new VipsRegion(cols);
        using var rr = new VipsRegion(rows);
        rc.Prepare(new VipsRect(0, 0, 4, 1));
        rr.Prepare(new VipsRect(0, 0, 1, 4));
        // Column 0 has no non-zero → height (4); columns 1..3 first non-zero at y=2.
        Assert.Equal(4u, ReadUInt(rc, 0, 0));
        Assert.Equal(2u, ReadUInt(rc, 1, 0));
        Assert.Equal(2u, ReadUInt(rc, 2, 0));
        Assert.Equal(2u, ReadUInt(rc, 3, 0));
        // Rows 0..1 are all zero → width (4); rows 2..3 first non-zero at x=1.
        Assert.Equal(4u, ReadUInt(rr, 0, 0));
        Assert.Equal(4u, ReadUInt(rr, 0, 1));
        Assert.Equal(1u, ReadUInt(rr, 0, 2));
        Assert.Equal(1u, ReadUInt(rr, 0, 3));
    }

    // ---- Grey ----

    [Fact]
    public void Grey_FloatRamps0to1()
    {
        var g = VipsImageOps.Grey(4, 2);
        Assert.Equal(VipsBandFormat.Float, g.BandFormat);
        Assert.Equal(4, g.Width);

        using var reg = new VipsRegion(g);
        reg.Prepare(new VipsRect(0, 0, 4, 2));
        Assert.Equal(0f, BinaryPrimitives.ReadSingleLittleEndian(reg.GetAddress(0, 0).Slice(0, 4)));
        Assert.Equal(1f, BinaryPrimitives.ReadSingleLittleEndian(reg.GetAddress(3, 0).Slice(0, 4)));
        // Constant down columns.
        Assert.Equal(
            BinaryPrimitives.ReadSingleLittleEndian(reg.GetAddress(2, 0).Slice(0, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(reg.GetAddress(2, 1).Slice(0, 4)));
    }

    [Fact]
    public void Grey_UCharRamp0to255()
    {
        var g = VipsImageOps.Grey(256, 1, uchar: true);
        Assert.Equal(VipsBandFormat.UChar, g.BandFormat);
        using var reg = new VipsRegion(g);
        reg.Prepare(new VipsRect(0, 0, 256, 1));
        Assert.Equal(0, reg.GetAddress(0, 0)[0]);
        Assert.Equal(255, reg.GetAddress(255, 0)[0]);
    }
}
