using System;
using System.IO;
using ImageMagick;
using CosmoImage.Loaders;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 200 — progressive JPEG (SOF2) decode in
/// <see cref="PureJpegDecoder"/>. Phase 3 of the JpegLibrary drop.
/// The decoder now handles multi-scan progressive JPEGs, dispatching
/// each SOS by (Ss, Se, Ah, Al) into one of four entropy paths:
/// DC initial, DC refinement, AC initial, AC refinement.
///
/// <para>Test fixtures use Magick.NET as a sidecar to produce
/// progressive-encoded JPEGs (we don't have a progressive encoder
/// of our own); the pure decoder then reads them and the round-trip
/// is verified against the original pixels within JPEG quantization
/// tolerance.</para>
/// </summary>
public class Round200Tests
{
    private static byte[] EncodeProgressiveRgb(byte[] rgb, uint w, uint h, uint quality = 90)
    {
        var settings = new MagickReadSettings
        {
            Width = w, Height = h, Format = MagickFormat.Rgb, Depth = 8,
        };
        using var img = new MagickImage();
        img.Read(rgb, settings);
        img.Format = MagickFormat.Jpeg;
        img.Quality = quality;
        // Plane interlace = JPEG progressive mode.
        img.Settings.Interlace = Interlace.Plane;
        return img.ToByteArray();
    }

    private static byte[] EncodeProgressiveGrey(byte[] grey, uint w, uint h, uint quality = 90)
    {
        var settings = new MagickReadSettings
        {
            Width = w, Height = h, Format = MagickFormat.Gray, Depth = 8,
        };
        using var img = new MagickImage();
        img.Read(grey, settings);
        img.Format = MagickFormat.Jpeg;
        img.Quality = quality;
        img.Settings.Interlace = Interlace.Plane;
        return img.ToByteArray();
    }

    private static byte[] RgbGradient(int w, int h)
    {
        var bytes = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 3;
                bytes[o + 0] = (byte)((x * 256) / w);
                bytes[o + 1] = (byte)((y * 256) / h);
                bytes[o + 2] = 100;
            }
        return bytes;
    }

    private static byte[] GreyGradient(int w, int h)
    {
        var bytes = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                bytes[y * w + x] = (byte)(((x * 200) / w + (y * 56) / h));
        return bytes;
    }

    [Fact]
    public void ProgressiveRgb_DecodesToCorrectDims()
    {
        // 32×32 progressive RGB JPEG, multiple of the 16×16 MCU
        // boundary (4:2:0 subsampling). Non-MCU-aligned dims are a
        // known soft spot in the progressive path that the next
        // pass will tighten up.
        var src = RgbGradient(32, 32);
        var jpeg = EncodeProgressiveRgb(src, 32, 32);
        VerifyIsProgressive(jpeg);

        var decoded = PureJpegDecoder.TryDecode(jpeg, out int w, out int h, out int channels);
        Assert.NotNull(decoded);
        Assert.Equal(32, w);
        Assert.Equal(32, h);
        Assert.Equal(3, channels);
    }

    [Fact]
    public void ProgressiveRgb_PixelsRoundTripWithinTolerance()
    {
        // After decode + YCbCr→RGB conversion, pixels should match the
        // original gradient within JPEG quality-90 quantization noise.
        // 4:2:0 chroma subsampling widens the envelope on chroma channels.
        var src = RgbGradient(32, 32);
        var jpeg = EncodeProgressiveRgb(src, 32, 32);
        VerifyIsProgressive(jpeg);

        var decoded = PureJpegDecoder.TryDecode(jpeg, out int w, out int h, out int channels);
        Assert.NotNull(decoded);
        Assert.Equal(3, channels);

        VipsJpegLoader.ConvertColorSpace(decoded!, w, h, channels, VipsJpegLoader.JpegColorSpace.YCbCr);

        // Spot-check at 4-pixel grid intervals (away from MCU edges).
        for (int y = 4; y < 28; y += 4)
            for (int x = 4; x < 28; x += 4)
            {
                int o = (y * 32 + x) * 3;
                Assert.InRange(decoded![o + 0] - src[o + 0], -25, 25);
                Assert.InRange(decoded[o + 1]  - src[o + 1], -25, 25);
                Assert.InRange(decoded[o + 2]  - src[o + 2], -25, 25);
            }
    }

    [Fact]
    public void ProgressiveGreyscale_RoundTripsCorrectly()
    {
        // Single-component progressive JPEG (no chroma channels).
        // Magick may emit several SOS scans for the DC then the AC
        // bits — exercising the full progressive path.
        var src = GreyGradient(24, 16);
        var jpeg = EncodeProgressiveGrey(src, 24, 16);
        VerifyIsProgressive(jpeg);

        var decoded = PureJpegDecoder.TryDecode(jpeg, out int w, out int h, out int channels);
        Assert.NotNull(decoded);
        Assert.Equal(24, w);
        Assert.Equal(16, h);
        Assert.Equal(1, channels);

        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 24; x++)
            {
                int diff = decoded![y * 24 + x] - src[y * 24 + x];
                Assert.InRange(diff, -15, 15);
            }
    }

    [Fact]
    public void ProgressiveJpeg_BaselineStillWorks()
    {
        // Sanity: making sure the baseline path didn't regress while
        // the progressive code was bolted on.
        var src = RgbGradient(16, 16);
        var settings = new MagickReadSettings
        {
            Width = 16, Height = 16, Format = MagickFormat.Rgb, Depth = 8,
        };
        using var img = new MagickImage();
        img.Read(src, settings);
        img.Format = MagickFormat.Jpeg;
        img.Quality = 90;
        // No Interlace setting → baseline (the default).
        var jpeg = img.ToByteArray();

        var decoded = PureJpegDecoder.TryDecode(jpeg, out int w, out int h, out int channels);
        Assert.NotNull(decoded);
        Assert.Equal(16, w);
        Assert.Equal(16, h);
        Assert.Equal(3, channels);
    }

    /// <summary>
    /// Walk markers and assert the file uses SOF2 (progressive) rather
    /// than SOF0 (baseline). Catches a Magick API change that would
    /// silently emit baseline despite the Interlace = Plane setting.
    /// </summary>
    private static void VerifyIsProgressive(byte[] jpeg)
    {
        int p = 2;
        while (p + 1 < jpeg.Length)
        {
            if (jpeg[p] != 0xFF) break;
            byte m = jpeg[p + 1];
            p += 2;
            if (m == 0xC2) return; // SOF2 found
            if (m == 0xC0) Assert.Fail("Encoder produced baseline (SOF0); test needs SOF2 fixture.");
            if (m == 0xD9 || m == 0xDA) break;
            int len = (jpeg[p] << 8) | jpeg[p + 1];
            p += len;
        }
        Assert.Fail("No SOF2 marker found; cannot validate progressive decode.");
    }
}
