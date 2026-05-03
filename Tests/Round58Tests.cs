using System;
using System.Runtime.InteropServices;
using CosmoImage.Core;
using Xunit;

namespace CosmoImage.Tests;

public class Round58Tests
{
    // ---- A8 / Rg32 sanity ----

    [Fact]
    public void A8_DescriptorMatchesL8Bytes()
    {
        Assert.Equal(1, A8.BandCount);
        Assert.Equal(VipsBandFormat.UChar, A8.BandFormat);
        Assert.Equal(1, Marshal.SizeOf<A8>());
    }

    [Fact]
    public void Rg32_RoundTrips()
    {
        var t = new TypedImage<Rg32>(2, 2);
        t[0, 0] = new Rg32(r: 60000, g: 1234);
        Assert.Equal(60000, t[0, 0].R);
        Assert.Equal(1234, t[0, 0].G);
    }

    // ---- Bgr565 ----

    [Fact]
    public void Bgr565_DescriptorIsSingleUShort()
    {
        Assert.Equal(1, Bgr565.BandCount);
        Assert.Equal(VipsBandFormat.UShort, Bgr565.BandFormat);
        Assert.Equal(2, Marshal.SizeOf<Bgr565>());
    }

    [Fact]
    public void Bgr565_PureRedRoundTrips()
    {
        // 8-bit R=255 → 5-bit field 31 → expanded back to 255.
        var p = new Bgr565(r: 255, g: 0, b: 0);
        Assert.Equal(255, p.R);
        Assert.Equal(0, p.G);
        Assert.Equal(0, p.B);
    }

    [Fact]
    public void Bgr565_PureGreen_HasFullField()
    {
        // 8-bit G=255 → 6-bit field 63 → expanded back to 255.
        var p = new Bgr565(r: 0, g: 255, b: 0);
        Assert.Equal(0, p.R);
        Assert.Equal(255, p.G);
        Assert.Equal(0, p.B);
    }

    [Fact]
    public void Bgr565_MidGray_QuantisesPredictably()
    {
        // 8-bit 128 truncated to 5 bits is 16; expanded back is (16<<3)|(16>>2) = 132.
        var p = new Bgr565(r: 128, g: 128, b: 128);
        Assert.Equal(132, p.R);
        Assert.Equal(130, p.G); // 6-bit 32, expanded = (32<<2)|(32>>4) = 130
        Assert.Equal(132, p.B);
    }

    // ---- Bgra4444 ----

    [Fact]
    public void Bgra4444_RoundTripPureColor()
    {
        var p = new Bgra4444(r: 240, g: 0, b: 0, a: 240);
        // 240 >> 4 = 15; Expand4(15) = (15<<4)|15 = 255.
        Assert.Equal(255, p.R);
        Assert.Equal(0, p.G);
        Assert.Equal(0, p.B);
        Assert.Equal(255, p.A);
    }

    [Fact]
    public void Bgra4444_MidLevels()
    {
        // 8-bit 119 truncates to 4-bit nibble 7 → expanded = (7<<4)|7 = 119.
        var p = new Bgra4444(r: 119, g: 119, b: 119, a: 119);
        Assert.Equal(119, p.R);
        Assert.Equal(119, p.G);
        Assert.Equal(119, p.B);
        Assert.Equal(119, p.A);
    }

    // ---- Bgra5551 ----

    [Fact]
    public void Bgra5551_BinaryAlpha_OpaqueAt128OrAbove()
    {
        var opaque = new Bgra5551(r: 0, g: 0, b: 0, a: 128);
        var transparent = new Bgra5551(r: 0, g: 0, b: 0, a: 127);
        Assert.Equal(255, opaque.A);
        Assert.Equal(0, transparent.A);
    }

    [Fact]
    public void Bgra5551_PureBlue()
    {
        // Same 5-bit-field math as Bgr565 for B.
        var p = new Bgra5551(r: 0, g: 0, b: 255, a: 255);
        Assert.Equal(0, p.R);
        Assert.Equal(0, p.G);
        Assert.Equal(255, p.B);
        Assert.Equal(255, p.A);
    }

    // ---- TypedImage round-trip with packed format ----

    [Fact]
    public void TypedImage_Bgr565_RoundTripsThroughVipsImage()
    {
        var src = new TypedImage<Bgr565>(2, 2);
        src[0, 0] = new Bgr565(r: 200, g: 100, b: 50);
        src[1, 1] = new Bgr565(r: 0, g: 255, b: 255);
        var img = src.AsVipsImage();
        Assert.Equal(VipsBandFormat.UShort, img.BandFormat);
        Assert.Equal(1, img.Bands);

        var t2 = new TypedImage<Bgr565>(img);
        // Quantisation through 5/6/5 means values won't match exactly,
        // but should be near.
        var p = t2[0, 0];
        Assert.InRange(p.R, 192, 208);
        Assert.InRange(p.G, 96, 104);
        Assert.InRange(p.B, 40, 56);
    }

    [Fact]
    public void TypedImage_RejectsBgr565OnUCharImage()
    {
        // Bgr565 needs UShort BandFormat. Wrapping it over a UChar image must throw.
        var src = new TypedImage<L8>(2, 2);
        var img = src.AsVipsImage();
        Assert.Throws<InvalidOperationException>(() => new TypedImage<Bgr565>(img));
    }
}
