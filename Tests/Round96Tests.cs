using System;
using System.Collections.Generic;
using CosmoImage.Operations.Misc;
using Xunit;

namespace CosmoImage.Tests;

public class Round96Tests
{
    /// <summary>RGB image with a smooth horizontal gradient (256 unique colors per row).</summary>
    private static VipsImage Gradient256(int height = 4)
        => new VipsImage
        {
            Width = 256, Height = height, Bands = 3, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? bb, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        byte v = (byte)(reg.Valid.Left + x);
                        addr[x * 3 + 0] = v; addr[x * 3 + 1] = v; addr[x * 3 + 2] = v;
                    }
                }
                return 0;
            }
        };

    /// <summary>Counts unique RGB triples in an image.</summary>
    private static int CountUniqueColors(VipsImage img)
    {
        var seen = new HashSet<(byte, byte, byte)>();
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, img.Width, img.Height));
        for (int y = 0; y < img.Height; y++)
        {
            var addr = reg.GetAddress(0, y);
            for (int x = 0; x < img.Width; x++)
                seen.Add((addr[x * 3], addr[x * 3 + 1], addr[x * 3 + 2]));
        }
        return seen.Count;
    }

    // ---- MagickQuantizer ----

    [Fact]
    public void MagickQuantizer_ReducesUniqueColors()
    {
        var src = Gradient256(2);
        var quantizer = new MagickQuantizer { Colors = 16, Dither = false };
        var output = quantizer.Apply(src);
        int unique = CountUniqueColors(output);
        Assert.True(unique <= 16, $"expected ≤ 16 unique colors, got {unique}");
    }

    [Fact]
    public void MagickQuantizer_PreservesDimensions()
    {
        var src = Gradient256();
        var quantizer = new MagickQuantizer { Colors = 8 };
        var output = quantizer.Apply(src);
        Assert.Equal(src.Width, output.Width);
        Assert.Equal(src.Height, output.Height);
        Assert.Equal(src.Bands, output.Bands);
    }

    [Fact]
    public void MagickQuantizer_RejectsInvalidColors()
    {
        var src = Gradient256();
        Assert.Throws<ArgumentOutOfRangeException>(() => new MagickQuantizer { Colors = 1 }.Apply(src));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MagickQuantizer { Colors = 257 }.Apply(src));
    }

    [Fact]
    public void MagickQuantizer_RejectsBadBandCount()
    {
        var twoBand = new VipsImage
        {
            Width = 4, Height = 4, Bands = 2, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? bb, ref bool stop) => 0,
        };
        Assert.Throws<ArgumentException>(() => new MagickQuantizer().Apply(twoBand));
    }

    // ---- Custom quantizer plugin ----

    /// <summary>Test quantizer that records invocation and returns input unchanged.</summary>
    private sealed class PassThroughQuantizer : IVipsQuantizer
    {
        public int InvocationCount;
        public VipsImage? LastInput;
        public VipsImage Apply(VipsImage input)
        {
            InvocationCount++;
            LastInput = input;
            return input;
        }
    }

    [Fact]
    public void CustomQuantizer_GetsInvokedViaWrapper()
    {
        var src = Gradient256();
        var custom = new PassThroughQuantizer();
        var output = VipsImageOps.Quantize(src, custom);
        Assert.Equal(1, custom.InvocationCount);
        Assert.Same(src, custom.LastInput);
        Assert.Same(src, output);
    }

    [Fact]
    public void CustomQuantizer_ReducingTo2Colors()
    {
        // Custom thresholding quantizer: pure black or pure white.
        var src = Gradient256(1);
        var threshold = new ThresholdQuantizer();
        var output = threshold.Apply(src);
        // Only two unique colors should remain.
        Assert.Equal(2, CountUniqueColors(output));
    }

    /// <summary>Quantizer that rounds each pixel to {0, 255} based on luminance.</summary>
    private sealed class ThresholdQuantizer : IVipsQuantizer
    {
        public byte Threshold { get; init; } = 128;
        public VipsImage Apply(VipsImage input)
        {
            int w = input.Width, h = input.Height, b = input.Bands;
            byte[] inputPixels;
            if (input.Pixels is { } existing) inputPixels = existing;
            else
            {
                var sink = new MemorySink(input);
                sink.RunAsync().GetAwaiter().GetResult();
                inputPixels = sink.Pixels;
            }
            var outBuf = new byte[w * h * b];
            for (int i = 0; i < w * h; i++)
            {
                int lum = 0;
                for (int k = 0; k < b; k++) lum += inputPixels[i * b + k];
                lum /= b;
                byte v = lum >= Threshold ? (byte)255 : (byte)0;
                for (int k = 0; k < b; k++) outBuf[i * b + k] = v;
            }
            var output = new VipsImage
            {
                Width = w, Height = h, Bands = b, BandFormat = input.BandFormat,
                Interpretation = input.Interpretation,
                PixelsLazy = new Lazy<byte[]>(() => outBuf),
            };
            output.CopyMetadataFrom(input);
            return output;
        }
    }

    // ---- Wrapper validation ----

    [Fact]
    public void Quantize_NullQuantizer_Throws()
    {
        var src = Gradient256();
        Assert.Throws<ArgumentNullException>(() => VipsImageOps.Quantize(src, (IVipsQuantizer)null!));
    }

    // ---- Existing API still works ----

    [Fact]
    public void ExistingQuantizeApi_StillWorks()
    {
        // The simpler (image, colors, dither) overload should still work.
        var src = Gradient256(2);
        var output = VipsImageOps.Quantize(src, colors: 8, dither: false);
        Assert.True(CountUniqueColors(output) <= 8);
    }
}
