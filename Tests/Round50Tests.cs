using System;
using System.Buffers.Binary;
using System.Threading;
using Xunit;

namespace CosmoImage.Tests;

public class Round50Tests
{
    private static VipsImage RampX(int w, int h)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++) addr[x] = (byte)(reg.Valid.Left + x);
                }
                return 0;
            }
        };

    /// <summary>Source that counts how many times Generate runs.</summary>
    private static (VipsImage img, Func<int> count) RampWithCounter(int w, int h)
    {
        int generateCalls = 0;
        var img = new VipsImage
        {
            Width = w, Height = h, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                Interlocked.Increment(ref generateCalls);
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++) addr[x] = (byte)(reg.Valid.Left + x);
                }
                return 0;
            }
        };
        return (img, () => generateCalls);
    }

    // ---- Cache ----

    [Fact]
    public void Cache_PassesThroughPixelData()
    {
        var src = RampX(8, 4);
        var cached = src.Cache();
        using var rs = new VipsRegion(src);
        using var rc = new VipsRegion(cached);
        rs.Prepare(new VipsRect(0, 0, 8, 4));
        rc.Prepare(new VipsRect(0, 0, 8, 4));
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 8; x++)
                Assert.Equal(rs.GetAddress(x, y)[0], rc.GetAddress(x, y)[0]);
    }

    [Fact]
    public void Cache_SuppressesRecomputeOnSecondPrepare()
    {
        // Two reads from the cached image should still only fire one
        // upstream compute, because Cache materialises eagerly.
        var (src, count) = RampWithCounter(8, 4);
        var cached = src.Cache(); // materialise once during Build
        int afterBuild = count();

        using var r1 = new VipsRegion(cached);
        r1.Prepare(new VipsRect(0, 0, 8, 4));
        using var r2 = new VipsRegion(cached);
        r2.Prepare(new VipsRect(0, 0, 4, 2));

        // No further upstream calls past materialisation.
        Assert.Equal(afterBuild, count());
    }

    // ---- Sequential ----

    [Fact]
    public void Sequential_PreservesPixelData()
    {
        var src = RampX(8, 4);
        var seq = src.Sequential();
        using var rs = new VipsRegion(src);
        using var rq = new VipsRegion(seq);
        rs.Prepare(new VipsRect(0, 0, 8, 4));
        rq.Prepare(new VipsRect(0, 0, 8, 4));
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 8; x++)
                Assert.Equal(rs.GetAddress(x, y)[0], rq.GetAddress(x, y)[0]);
    }

    [Fact]
    public void Sequential_DemandStyleIsFatStrip()
    {
        var src = RampX(8, 4);
        var seq = src.Sequential();
        Assert.Equal(VipsDemandStyle.FatStrip, seq.DemandHint);
    }

    // ---- Copy ----

    [Fact]
    public void Copy_PassesPixelsThrough()
    {
        var src = RampX(4, 4);
        var copied = src.Copy();
        using var rs = new VipsRegion(src);
        using var rc = new VipsRegion(copied);
        rs.Prepare(new VipsRect(0, 0, 4, 4));
        rc.Prepare(new VipsRect(0, 0, 4, 4));
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
                Assert.Equal(rs.GetAddress(x, y)[0], rc.GetAddress(x, y)[0]);
    }

    [Fact]
    public void Copy_RewritesInterpretationAndRes()
    {
        var src = RampX(4, 4);
        var c = src.Copy(interpretation: VipsInterpretation.SRGB, xRes: 300, yRes: 300);
        Assert.Equal(VipsInterpretation.SRGB, c.Interpretation);
        Assert.Equal(300, c.XRes);
        Assert.Equal(300, c.YRes);
    }

    [Fact]
    public void Copy_RebandsIfPelSizeMatches()
    {
        // 4-band UChar (4 bytes/pel) reinterpreted as 1-band UInt (4 bytes/pel).
        var src = new VipsImage
        {
            Width = 2, Height = 1, Bands = 4, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                var addr = reg.GetAddress(0, 0);
                addr[0] = 1; addr[1] = 2; addr[2] = 3; addr[3] = 4;
                addr[4] = 5; addr[5] = 6; addr[6] = 7; addr[7] = 8;
                return 0;
            }
        };
        var c = src.Copy(bandFormat: VipsBandFormat.UInt, bands: 1);
        Assert.Equal(1, c.Bands);
        Assert.Equal(VipsBandFormat.UInt, c.BandFormat);
        using var reg = new VipsRegion(c);
        reg.Prepare(new VipsRect(0, 0, 2, 1));
        // First pel (1, 2, 3, 4) → UInt little-endian = 0x04030201 = 67305985.
        Assert.Equal(0x04030201u,
            BinaryPrimitives.ReadUInt32LittleEndian(reg.GetAddress(0, 0).Slice(0, 4)));
        Assert.Equal(0x08070605u,
            BinaryPrimitives.ReadUInt32LittleEndian(reg.GetAddress(1, 0).Slice(0, 4)));
    }

    [Fact]
    public void Copy_RejectsInconsistentPelSize()
    {
        var src = RampX(4, 4); // 1-byte pel (1-band UChar)
        // Reinterpret as 1-band Float = 4-byte pel → mismatch, should throw.
        Assert.Throws<Exception>(() => src.Copy(bandFormat: VipsBandFormat.Float));
    }
}
