using System;
using System.Runtime.InteropServices;
using CosmoImage.Core;
using Xunit;

namespace CosmoImage.Tests;

public class Round59Tests
{
    // ---- Byte4 / Short2 / Short4 ----

    [Fact]
    public void Byte4_Descriptor()
    {
        Assert.Equal(4, Byte4.BandCount);
        Assert.Equal(VipsBandFormat.UChar, Byte4.BandFormat);
        Assert.Equal(4, Marshal.SizeOf<Byte4>());
    }

    [Fact]
    public void Short2_RoundTripsSignedValues()
    {
        var t = new TypedImage<Short2>(2, 2);
        t[0, 0] = new Short2(-30000, 30000);
        Assert.Equal(-30000, t[0, 0].X);
        Assert.Equal(30000, t[0, 0].Y);
    }

    [Fact]
    public void Short4_DescriptorAndSize()
    {
        Assert.Equal(4, Short4.BandCount);
        Assert.Equal(VipsBandFormat.Short, Short4.BandFormat);
        Assert.Equal(8, Marshal.SizeOf<Short4>());
    }

    // ---- NormalizedByte2 / NormalizedByte4 ----

    [Fact]
    public void NormalizedByte2_EndpointsRoundTrip()
    {
        var p = new NormalizedByte2(1.0f, -1.0f);
        Assert.Equal(1.0f, p.X, 2);
        Assert.Equal(-1.0f, p.Y, 2);
    }

    [Fact]
    public void NormalizedByte2_ZeroIsZero()
    {
        var p = new NormalizedByte2(0f, 0f);
        Assert.Equal(0f, p.X);
        Assert.Equal(0f, p.Y);
    }

    [Fact]
    public void NormalizedByte4_StoresFourSignedBytes()
    {
        var p = new NormalizedByte4(0.5f, -0.5f, 1.0f, -1.0f);
        Assert.Equal(0.5f, p.X, 1);
        Assert.Equal(-0.5f, p.Y, 1);
        Assert.Equal(1.0f, p.Z, 2);
        Assert.Equal(-1.0f, p.W, 2);
    }

    // ---- NormalizedShort2 / NormalizedShort4 ----

    [Fact]
    public void NormalizedShort2_HighPrecisionEndpoints()
    {
        var p = new NormalizedShort2(1f, -1f);
        Assert.Equal(1f, p.X, 4);
        Assert.Equal(-1f, p.Y, 4);
    }

    [Fact]
    public void NormalizedShort4_PreservesMidpoint()
    {
        var p = new NormalizedShort4(0.25f, 0.5f, 0.75f, 1.0f);
        Assert.Equal(0.25f, p.X, 3);
        Assert.Equal(0.5f, p.Y, 3);
        Assert.Equal(0.75f, p.Z, 3);
        Assert.Equal(1.0f, p.W, 3);
    }

    // ---- Rgba1010102 ----

    [Fact]
    public void Rgba1010102_DescriptorIsSingleUInt()
    {
        Assert.Equal(1, Rgba1010102.BandCount);
        Assert.Equal(VipsBandFormat.UInt, Rgba1010102.BandFormat);
        Assert.Equal(4, Marshal.SizeOf<Rgba1010102>());
    }

    [Fact]
    public void Rgba1010102_EndpointsClamp()
    {
        var p = new Rgba1010102(r: 255, g: 255, b: 255, a: 255);
        Assert.Equal(255, p.R);
        Assert.Equal(255, p.G);
        Assert.Equal(255, p.B);
        Assert.Equal(255, p.A);
    }

    [Fact]
    public void Rgba1010102_AlphaIs2Bit()
    {
        // Alpha is quantised to 4 levels (0/85/170/255).
        var quartersOfAlpha = new[] { 0, 64, 128, 192, 255 };
        foreach (var aIn in quartersOfAlpha)
        {
            var p = new Rgba1010102(r: 0, g: 0, b: 0, a: (byte)aIn);
            // Output must be one of the four canonical 2-bit-replicated values.
            Assert.Contains(p.A, new byte[] { 0, 85, 170, 255 });
        }
    }

    [Fact]
    public void Rgba1010102_PureRedDoesNotLeakIntoOtherChannels()
    {
        var p = new Rgba1010102(r: 200, g: 0, b: 0, a: 255);
        Assert.True(p.R > 190);
        Assert.Equal(0, p.G);
        Assert.Equal(0, p.B);
    }

    // ---- TypedImage round-trips with the larger formats ----

    [Fact]
    public void TypedImage_NormalizedShort4_RoundTripsThroughVipsImage()
    {
        var src = new TypedImage<NormalizedShort4>(2, 2);
        src[0, 0] = new NormalizedShort4(0.5f, -0.5f, 1f, -1f);
        var img = src.AsVipsImage();
        Assert.Equal(VipsBandFormat.Short, img.BandFormat);
        Assert.Equal(4, img.Bands);

        var t2 = new TypedImage<NormalizedShort4>(img);
        Assert.Equal(0.5f, t2[0, 0].X, 3);
        Assert.Equal(-0.5f, t2[0, 0].Y, 3);
    }

    [Fact]
    public void TypedImage_Rgba1010102_RoundTripsThroughVipsImage()
    {
        var src = new TypedImage<Rgba1010102>(2, 2);
        src[0, 0] = new Rgba1010102(r: 200, g: 100, b: 50, a: 255);
        var img = src.AsVipsImage();
        Assert.Equal(VipsBandFormat.UInt, img.BandFormat);
        Assert.Equal(1, img.Bands);

        var t2 = new TypedImage<Rgba1010102>(img);
        var p = t2[0, 0];
        // 10-bit precision means R/G/B are within ~1 of input.
        Assert.InRange(p.R, 199, 201);
        Assert.Equal(255, p.A);
    }
}
