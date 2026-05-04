using System;
using System.Runtime.InteropServices;
using CosmoImage.Core;
using Xunit;

namespace CosmoImage.Tests;

public class Round97Tests
{
    // ---- Static IPixel descriptors ----

    [Fact]
    public void RgbaVector_HasFourFloatBands()
    {
        Assert.Equal(4, RgbaVector.BandCount);
        Assert.Equal(VipsBandFormat.Float, RgbaVector.BandFormat);
    }

    [Fact]
    public void RgbVector_HasThreeFloatBands()
    {
        Assert.Equal(3, RgbVector.BandCount);
        Assert.Equal(VipsBandFormat.Float, RgbVector.BandFormat);
    }

    [Fact]
    public void LFloat_HasSingleFloatBand()
    {
        Assert.Equal(1, LFloat.BandCount);
        Assert.Equal(VipsBandFormat.Float, LFloat.BandFormat);
    }

    [Fact]
    public void LaVector_HasTwoFloatBands()
    {
        Assert.Equal(2, LaVector.BandCount);
        Assert.Equal(VipsBandFormat.Float, LaVector.BandFormat);
    }

    // ---- Memory layout ----

    [Fact]
    public void RgbaVector_LayoutIs16BytesContiguous()
    {
        Assert.Equal(16, Marshal.SizeOf<RgbaVector>());  // 4 × float
    }

    [Fact]
    public void RgbVector_LayoutIs12Bytes()
    {
        Assert.Equal(12, Marshal.SizeOf<RgbVector>());
    }

    [Fact]
    public void LFloat_LayoutIs4Bytes()
    {
        Assert.Equal(4, Marshal.SizeOf<LFloat>());
    }

    [Fact]
    public void LaVector_LayoutIs8Bytes()
    {
        Assert.Equal(8, Marshal.SizeOf<LaVector>());
    }

    // ---- Constructor ----

    [Fact]
    public void RgbaVector_StoresChannels()
    {
        var p = new RgbaVector(0.1f, 0.5f, 0.9f, 0.7f);
        Assert.Equal(0.1f, p.R);
        Assert.Equal(0.5f, p.G);
        Assert.Equal(0.9f, p.B);
        Assert.Equal(0.7f, p.A);
    }

    [Fact]
    public void RgbVector_StoresChannels()
    {
        var p = new RgbVector(0.2f, 0.4f, 0.8f);
        Assert.Equal(0.2f, p.R);
        Assert.Equal(0.4f, p.G);
        Assert.Equal(0.8f, p.B);
    }

    // ---- Marshal as Span over byte buffer ----

    [Fact]
    public void RgbaVector_CanBeReinterpretedFromByteBuffer()
    {
        // Build a byte buffer that represents 2 RgbaVector pixels.
        // sRGB red and green at full opacity in linear floats.
        var pixels = new RgbaVector[] {
            new RgbaVector(1.0f, 0.0f, 0.0f, 1.0f),
            new RgbaVector(0.0f, 1.0f, 0.0f, 1.0f),
        };
        var byteBuf = new byte[2 * Marshal.SizeOf<RgbaVector>()];
        MemoryMarshal.AsBytes(pixels.AsSpan()).CopyTo(byteBuf);
        var pels = MemoryMarshal.Cast<byte, RgbaVector>(byteBuf);
        Assert.Equal(2, pels.Length);
        Assert.Equal(1.0f, pels[0].R);
        Assert.Equal(1.0f, pels[1].G);
    }

    // ---- Out-of-range float values are valid ----

    [Fact]
    public void RgbaVector_AcceptsHdrValues()
    {
        // HDR / linear-light values can exceed 1.0.
        var hdr = new RgbaVector(2.5f, 5.7f, 0.0f, 1.0f);
        Assert.Equal(2.5f, hdr.R);
        Assert.Equal(5.7f, hdr.G);
    }
}
