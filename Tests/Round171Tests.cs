using System;
using CosmoImage.Operations.Geometric;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 171 — <c>rot45</c> (45-degree rotation increments). Mirrors
/// libvips' constraint of square + odd-sided input. Tests pin axis-
/// aligned cases against <c>Rotate</c>'s output, and check that
/// diagonal rotations preserve the centre pixel and move the corners
/// in the expected diamond pattern.
/// </summary>
public class Round171Tests
{
    /// <summary>
    /// 5×5 1-band UChar with a distinctive interior pattern. The centre
    /// is at (2, 2). Pixel value = y * 10 + x — uniquely identifies any
    /// pixel after rotation.
    /// </summary>
    private static VipsImage Make5x5()
        => new VipsImage
        {
            Width = 5, Height = 5, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) =>
            {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                        addr[x] = (byte)((reg.Valid.Top + y) * 10 + (reg.Valid.Left + x));
                }
                return 0;
            }
        };

    private static byte ReadPel(VipsImage img, int x, int y)
    {
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, img.Width, img.Height));
        return reg.GetAddress(x, y)[0];
    }

    [Fact]
    public void D0_IsIdentity()
    {
        var src = Make5x5();
        var rot = src.Rot45(VipsAngle45.D0);
        for (int y = 0; y < 5; y++)
            for (int x = 0; x < 5; x++)
                Assert.Equal((byte)(y * 10 + x), ReadPel(rot, x, y));
    }

    [Fact]
    public void D90_MatchesOrthogonalRotate()
    {
        var src = Make5x5();
        var via45 = src.Rot45(VipsAngle45.D90);
        var viaOrth = src.Rotate(VipsAngle.D90);
        for (int y = 0; y < 5; y++)
            for (int x = 0; x < 5; x++)
                Assert.Equal(ReadPel(viaOrth, x, y), ReadPel(via45, x, y));
    }

    [Fact]
    public void D180_MatchesOrthogonalRotate()
    {
        var src = Make5x5();
        var via45 = src.Rot45(VipsAngle45.D180);
        var viaOrth = src.Rotate(VipsAngle.D180);
        for (int y = 0; y < 5; y++)
            for (int x = 0; x < 5; x++)
                Assert.Equal(ReadPel(viaOrth, x, y), ReadPel(via45, x, y));
    }

    [Fact]
    public void D270_MatchesOrthogonalRotate()
    {
        var src = Make5x5();
        var via45 = src.Rot45(VipsAngle45.D270);
        var viaOrth = src.Rotate(VipsAngle.D270);
        for (int y = 0; y < 5; y++)
            for (int x = 0; x < 5; x++)
                Assert.Equal(ReadPel(viaOrth, x, y), ReadPel(via45, x, y));
    }

    [Fact]
    public void D45_PreservesCentre()
    {
        // The centre pixel is the rotation fixed-point — all 8 angles
        // must keep it. (2, 2) carries value 22.
        var src = Make5x5();
        foreach (VipsAngle45 a in Enum.GetValues<VipsAngle45>())
        {
            var rot = src.Rot45(a);
            Assert.Equal((byte)22, ReadPel(rot, 2, 2));
        }
    }

    [Fact]
    public void D45_MovesEastNeighbourToSoutheast()
    {
        // Clockwise rotation by 45° maps the East input neighbour (3, 2)
        // to the South-East output position (3, 3). value 23 lands at
        // image-coords (+1, +1) from centre (y-down → SE = +x, +y).
        var src = Make5x5();
        var rot = src.Rot45(VipsAngle45.D45);
        Assert.Equal((byte)23, ReadPel(rot, 3, 3));
    }

    [Fact]
    public void D45_CornerOutsideRotatedDiamond_ZeroFilled()
    {
        // After 45° rotation the inscribed diamond covers the centre
        // cross; the original corners (0,0), (4,0), (0,4), (4,4) sample
        // from positions outside the input bounds, so they zero-fill.
        var src = Make5x5();
        var rot = src.Rot45(VipsAngle45.D45);
        Assert.Equal((byte)0, ReadPel(rot, 0, 0));
        Assert.Equal((byte)0, ReadPel(rot, 4, 0));
        Assert.Equal((byte)0, ReadPel(rot, 0, 4));
        Assert.Equal((byte)0, ReadPel(rot, 4, 4));
    }

    [Fact]
    public void D45_KeepsCentreCross_OnRotated()
    {
        // The four cardinal neighbours sit on the rotation diamond;
        // a 45° clockwise rotation moves them to the four diagonals.
        // North → NE, East → SE, South → SW, West → NW.
        // Values: N=12, E=23, S=32, W=21.
        var src = Make5x5();
        var rot = src.Rot45(VipsAngle45.D45);
        Assert.Equal((byte)12, ReadPel(rot, 3, 1)); // N → NE
        Assert.Equal((byte)23, ReadPel(rot, 3, 3)); // E → SE
        Assert.Equal((byte)32, ReadPel(rot, 1, 3)); // S → SW
        Assert.Equal((byte)21, ReadPel(rot, 1, 1)); // W → NW
    }

    [Fact]
    public void NonSquareInput_Rejected()
    {
        var nonSquare = new VipsImage
        {
            Width = 5, Height = 7, Bands = 1, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => 0,
        };
        Assert.Throws<Exception>(() => nonSquare.Rot45());
    }

    [Fact]
    public void EvenSidedInput_Rejected()
    {
        var even = new VipsImage
        {
            Width = 4, Height = 4, Bands = 1, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => 0,
        };
        Assert.Throws<Exception>(() => even.Rot45());
    }
}
