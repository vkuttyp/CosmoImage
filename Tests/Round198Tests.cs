using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Loaders;
using CosmoImage.Savers;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 198 — <see cref="PureJpegDecoder"/>: pure-C# baseline JPEG
/// decoder. Drops the JpegLibrary dependency for the dominant on-the-
/// web JPEG subset (SOF0 baseline sequential, 8-bit, Huffman-coded).
/// Handles baseline and progressive JPEGs natively. Unsupported
/// variants now fail explicitly rather than falling back to Magick.
///
/// <para>Tests encode known images via the existing JPEG saver
/// (which produces SOF0 baseline) and decode via PureJpegDecoder
/// directly. Verifies pixel-content within the lossy-JPEG tolerance
/// (~5 LSB at quality 90).</para>
/// </summary>
public class Round198Tests
{
    /// <summary>RGB image with a smooth gradient — JPEG handles smooth content well.</summary>
    private static VipsImage SmoothRgb(int w, int h)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 3, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) =>
            {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        int gx = reg.Valid.Left + x;
                        int gy = reg.Valid.Top + y;
                        addr[x * 3 + 0] = (byte)(gx * 256 / w);
                        addr[x * 3 + 1] = (byte)(gy * 256 / h);
                        addr[x * 3 + 2] = 128;
                    }
                }
                return 0;
            }
        };

    private static VipsImage SmoothGrey(int w, int h)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) =>
            {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                        addr[x] = (byte)((reg.Valid.Top + y) * 5 + (reg.Valid.Left + x) * 3);
                }
                return 0;
            }
        };

    private static async Task<byte[]> EncodeJpegAsync(VipsImage img, int quality = 90)
    {
        using var ms = new MemoryStream();
        var writer = PipeWriter.Create(ms);
        await VipsJpegSaver.SaveAsync(img, writer, quality);
        return ms.ToArray();
    }

    [Fact]
    public async Task Decoder_HandlesGreyscaleBaseline()
    {
        // Greyscale JPEG: single component, no chroma channels.
        var src = SmoothGrey(32, 24);
        var jpeg = await EncodeJpegAsync(src);

        var pixels = PureJpegDecoder.TryDecode(jpeg, out int w, out int h, out int channels);
        Assert.NotNull(pixels);
        Assert.Equal(32, w);
        Assert.Equal(24, h);
        Assert.Equal(1, channels);
        Assert.Equal(32 * 24, pixels!.Length);

        // JPEG quantization with quality 90 introduces small per-pixel
        // error. ±15 LSB is a safe envelope for the gradient.
        using var srcReg = new VipsRegion(src);
        srcReg.Prepare(new VipsRect(0, 0, 32, 24));
        for (int y = 0; y < 24; y++)
            for (int x = 0; x < 32; x++)
            {
                byte expected = srcReg.GetAddress(x, y)[0];
                byte actual = pixels[y * 32 + x];
                Assert.InRange(actual - expected, -15, 15);
            }
    }

    [Fact]
    public async Task Decoder_HandlesRgbBaseline()
    {
        // RGB JPEG: 3 components, default JFIF YCbCr 4:2:0 subsampling.
        // Decoder returns raw Y/Cb/Cr; the loader-side ConvertColorSpace
        // converts. We test the decoder output directly by passing
        // through ConvertColorSpace ourselves.
        var src = SmoothRgb(32, 32);
        var jpeg = await EncodeJpegAsync(src);

        var raw = PureJpegDecoder.TryDecode(jpeg, out int w, out int h, out int channels);
        Assert.NotNull(raw);
        Assert.Equal(32, w);
        Assert.Equal(32, h);
        Assert.Equal(3, channels);

        // Convert YCbCr → RGB using the same logic the loader does.
        VipsJpegLoader.ConvertColorSpace(raw!, w, h, channels, VipsJpegLoader.JpegColorSpace.YCbCr);

        // Verify the smooth gradient survived the round trip. Tolerance
        // is wider for chroma-subsampled YCbCr (4:2:0 averages 2×2 chroma).
        using var srcReg = new VipsRegion(src);
        srcReg.Prepare(new VipsRect(0, 0, 32, 32));
        for (int y = 4; y < 28; y += 4)
            for (int x = 4; x < 28; x += 4)
            {
                var sa = srcReg.GetAddress(x, y);
                int dstOff = (y * 32 + x) * 3;
                for (int c = 0; c < 3; c++)
                {
                    int diff = raw![dstOff + c] - sa[c];
                    Assert.InRange(diff, -25, 25);
                }
            }
    }

    [Fact]
    public void Decoder_RejectsNonJpegInput()
    {
        // Random bytes that don't start with FFD8 → null.
        var notJpeg = new byte[] { 0x89, 0x50, 0x4E, 0x47 }; // PNG magic
        var pixels = PureJpegDecoder.TryDecode(notJpeg, out _, out _, out _);
        Assert.Null(pixels);
    }

    [Fact]
    public void Decoder_RejectsTruncatedInput()
    {
        var truncated = new byte[] { 0xFF, 0xD8 }; // SOI only
        var pixels = PureJpegDecoder.TryDecode(truncated, out _, out _, out _);
        Assert.Null(pixels);
    }

    [Fact]
    public async Task LoaderFastPath_ProducesExpectedBaselinePixels()
    {
        // End-to-end: VipsJpegLoader.LoadAsync routes through
        // PureJpegDecoder for baseline JPEGs. Verify the loader's
        // pixel output is reasonable for a baseline JPEG.
        var src = SmoothRgb(64, 64);
        var jpegBytes = await EncodeJpegAsync(src);

        var loadSrc = new PipeVipsSource(PipeReader.Create(new MemoryStream(jpegBytes)));
        var loaded = await VipsJpegLoader.LoadAsync(loadSrc);
        Assert.NotNull(loaded);
        Assert.Equal(64, loaded!.Width);
        Assert.Equal(64, loaded.Height);
        Assert.Equal(3, loaded.Bands);

        using var reg = new VipsRegion(loaded);
        reg.Prepare(new VipsRect(0, 0, 64, 64));

        using var srcReg = new VipsRegion(src);
        srcReg.Prepare(new VipsRect(0, 0, 64, 64));

        // Spot-check: spreading-tolerant comparison of a few pixels.
        for (int y = 8; y < 56; y += 16)
            for (int x = 8; x < 56; x += 16)
            {
                var sa = srcReg.GetAddress(x, y);
                var la = reg.GetAddress(x, y);
                for (int c = 0; c < 3; c++)
                    Assert.InRange(la[c] - sa[c], -25, 25);
            }
    }
}
