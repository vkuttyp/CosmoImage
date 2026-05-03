using System;
using System.Runtime.InteropServices;
using CosmoImage.Core;
using Xunit;

namespace CosmoImage.Tests;

public class Round57Tests
{
    // ---- Compile-time descriptor sanity ----

    [Fact]
    public void Bgr24_HasExpected_BandCount_AndFormat()
    {
        Assert.Equal(3, Bgr24.BandCount);
        Assert.Equal(VipsBandFormat.UChar, Bgr24.BandFormat);
        Assert.Equal(3, Marshal.SizeOf<Bgr24>());
    }

    [Fact]
    public void Bgra32_HasExpected_BandCount_AndFormat()
    {
        Assert.Equal(4, Bgra32.BandCount);
        Assert.Equal(VipsBandFormat.UChar, Bgra32.BandFormat);
        Assert.Equal(4, Marshal.SizeOf<Bgra32>());
    }

    [Fact]
    public void Argb32_HasExpected_BandCount_AndFormat()
    {
        Assert.Equal(4, Argb32.BandCount);
        Assert.Equal(VipsBandFormat.UChar, Argb32.BandFormat);
        Assert.Equal(4, Marshal.SizeOf<Argb32>());
    }

    [Fact]
    public void L16_HasExpected_BandCount_AndFormat()
    {
        Assert.Equal(1, L16.BandCount);
        Assert.Equal(VipsBandFormat.UShort, L16.BandFormat);
        Assert.Equal(2, Marshal.SizeOf<L16>());
    }

    [Fact]
    public void Rgb48_HasExpected_BandCount_AndFormat()
    {
        Assert.Equal(3, Rgb48.BandCount);
        Assert.Equal(VipsBandFormat.UShort, Rgb48.BandFormat);
        Assert.Equal(6, Marshal.SizeOf<Rgb48>());
    }

    [Fact]
    public void Rgba64_HasExpected_BandCount_AndFormat()
    {
        Assert.Equal(4, Rgba64.BandCount);
        Assert.Equal(VipsBandFormat.UShort, Rgba64.BandFormat);
        Assert.Equal(8, Marshal.SizeOf<Rgba64>());
    }

    [Fact]
    public void La32_HasExpected_BandCount_AndFormat()
    {
        Assert.Equal(2, La32.BandCount);
        Assert.Equal(VipsBandFormat.UShort, La32.BandFormat);
        Assert.Equal(4, Marshal.SizeOf<La32>());
    }

    // ---- TypedImage round-trips ----

    [Fact]
    public void TypedImage_Bgr24_RoundTrips()
    {
        var t = new TypedImage<Bgr24>(2, 2);
        t[0, 0] = new Bgr24(b: 10, g: 20, r: 30);
        t[1, 0] = new Bgr24(b: 40, g: 50, r: 60);
        var read = t[0, 0];
        Assert.Equal(10, read.B);
        Assert.Equal(20, read.G);
        Assert.Equal(30, read.R);
    }

    [Fact]
    public void TypedImage_Rgba64_RoundTripsHigh16BitValues()
    {
        var t = new TypedImage<Rgba64>(2, 2);
        t[0, 0] = new Rgba64(r: 65535, g: 32768, b: 1024, a: 65535);
        var read = t[0, 0];
        Assert.Equal(65535, read.R);
        Assert.Equal(32768, read.G);
        Assert.Equal(1024, read.B);
        Assert.Equal(65535, read.A);
    }

    [Fact]
    public void TypedImage_L16_RoundTrips()
    {
        var t = new TypedImage<L16>(4, 4);
        t[2, 3] = new L16(60000);
        Assert.Equal(60000, t[2, 3].L);
    }

    [Fact]
    public void TypedImage_RowSpan_OverBgra32_ReinterpretsBytes()
    {
        var t = new TypedImage<Bgra32>(4, 1);
        t[0, 0] = new Bgra32(b: 10, g: 20, r: 30, a: 255);
        t[1, 0] = new Bgra32(b: 40, g: 50, r: 60, a: 255);
        t[2, 0] = new Bgra32(b: 70, g: 80, r: 90, a: 255);
        t[3, 0] = new Bgra32(b: 100, g: 110, r: 120, a: 255);

        var row = t.RowSpan(0);
        Assert.Equal(4, row.Length);
        Assert.Equal(40, row[1].B);
        Assert.Equal(50, row[1].G);
        Assert.Equal(60, row[1].R);
        Assert.Equal(255, row[1].A);
    }

    [Fact]
    public void TypedImage_AsVipsImage_RoundTripsThroughBgr24()
    {
        // Build a TypedImage<Bgr24>, materialise to VipsImage,
        // re-wrap as TypedImage and read pixels back.
        var src = new TypedImage<Bgr24>(2, 2);
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 2; x++)
                src[x, y] = new Bgr24((byte)(x * 10), (byte)(y * 20), 99);
        var img = src.AsVipsImage();
        Assert.Equal(3, img.Bands);
        Assert.Equal(VipsBandFormat.UChar, img.BandFormat);

        var t2 = new TypedImage<Bgr24>(img);
        Assert.Equal(99, t2[1, 1].R);
        Assert.Equal(20, t2[0, 1].G);
        Assert.Equal(10, t2[1, 0].B);
    }

    [Fact]
    public void TypedImage_RejectsBandFormatMismatch()
    {
        // Build an L8 image, try to wrap as L16 — should reject.
        var src = new TypedImage<L8>(2, 2);
        var img = src.AsVipsImage();
        Assert.Throws<InvalidOperationException>(() => new TypedImage<L16>(img));
    }
}
