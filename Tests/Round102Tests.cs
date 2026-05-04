using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Loaders;
using Xunit;

namespace CosmoImage.Tests;

public class Round102Tests
{
    /// <summary>RGB image with a sliding gradient — distinct R / G / B per pixel.</summary>
    private static VipsImage RgbDiagonal(int w, int h)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 3, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? bb, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        int gx = reg.Valid.Left + x;
                        int gy = reg.Valid.Top + y;
                        addr[x * 3 + 0] = (byte)((gx * 7) & 0xFF);
                        addr[x * 3 + 1] = (byte)((gy * 11) & 0xFF);
                        addr[x * 3 + 2] = (byte)(((gx + gy) * 13) & 0xFF);
                    }
                }
                return 0;
            }
        };

    private static VipsImage RgbaDiagonal(int w, int h)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 4, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? bb, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        int gx = reg.Valid.Left + x;
                        int gy = reg.Valid.Top + y;
                        addr[x * 4 + 0] = (byte)((gx * 7) & 0xFF);
                        addr[x * 4 + 1] = (byte)((gy * 11) & 0xFF);
                        addr[x * 4 + 2] = (byte)(((gx + gy) * 13) & 0xFF);
                        addr[x * 4 + 3] = (byte)((gx ^ gy) & 0xFF);
                    }
                }
                return 0;
            }
        };

    private static VipsImage GreyscaleGradient(int w, int h)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? bb, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                        addr[x] = (byte)(((reg.Valid.Left + x) * 5 + (reg.Valid.Top + y) * 3) & 0xFF);
                }
                return 0;
            }
        };

    private static async Task<byte[]> SavePngAsync(VipsImage img)
    {
        using var ms = new MemoryStream();
        var writer = PipeWriter.Create(ms);
        await VipsImageOps.SavePngAsync(img, writer);
        await writer.CompleteAsync();
        return ms.ToArray();
    }

    private static byte[] ReadPel(VipsImage img, int x, int y)
    {
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, img.Width, img.Height));
        return reg.GetAddress(x, y).Slice(0, img.Bands).ToArray();
    }

    private static byte[] CollectPixels(VipsImage img)
    {
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, img.Width, img.Height));
        var bytes = new byte[img.Width * img.Height * img.Bands];
        for (int y = 0; y < img.Height; y++)
        {
            var addr = reg.GetAddress(0, y);
            for (int x = 0; x < img.Width * img.Bands; x++)
                bytes[y * img.Width * img.Bands + x] = addr[x];
        }
        return bytes;
    }

    // ---- Pure decoder direct ----

    [Fact]
    public async Task PureDecoder_RgbRoundTrip()
    {
        var src = RgbDiagonal(16, 12);
        var pngBytes = await SavePngAsync(src);
        var decoded = PurePngDecoder.TryDecode(pngBytes, out int channels);
        Assert.NotNull(decoded);
        Assert.Equal(3, channels);
        // Compare to expected pixels.
        var expected = CollectPixels(src);
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], decoded![i]);
    }

    [Fact]
    public async Task PureDecoder_RgbaRoundTrip()
    {
        var src = RgbaDiagonal(8, 8);
        var pngBytes = await SavePngAsync(src);
        var decoded = PurePngDecoder.TryDecode(pngBytes, out int channels);
        Assert.NotNull(decoded);
        Assert.Equal(4, channels);
        var expected = CollectPixels(src);
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], decoded![i]);
    }

    [Fact]
    public async Task PureDecoder_GreyscaleRoundTrip()
    {
        var src = GreyscaleGradient(20, 10);
        var pngBytes = await SavePngAsync(src);
        var decoded = PurePngDecoder.TryDecode(pngBytes, out int channels);
        Assert.NotNull(decoded);
        Assert.Equal(1, channels);
        var expected = CollectPixels(src);
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], decoded![i]);
    }

    [Fact]
    public void PureDecoder_BadSignature_ReturnsNull()
    {
        var bytes = new byte[64];
        // No PNG signature.
        var decoded = PurePngDecoder.TryDecode(bytes, out int channels);
        Assert.Null(decoded);
        Assert.Equal(0, channels);
    }

    [Fact]
    public void PureDecoder_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(PurePngDecoder.TryDecode(null!, out _));
        Assert.Null(PurePngDecoder.TryDecode(Array.Empty<byte>(), out _));
        Assert.Null(PurePngDecoder.TryDecode(new byte[7], out _));  // shorter than signature
    }

    // ---- LoadAsync end-to-end uses the pure path ----

    [Fact]
    public async Task LoadAsync_PngBytes_DecodesCorrectly()
    {
        // End-to-end: save a synthetic image, load via LoadAsync (which
        // routes through the pure decoder), verify pixel exact match.
        var src = RgbaDiagonal(32, 16);
        var pngBytes = await SavePngAsync(src);

        await using var source = new PipeVipsSource(PipeReader.Create(new MemoryStream(pngBytes)));
        var loaded = await VipsPngLoader.LoadAsync(source);
        Assert.NotNull(loaded);
        Assert.Equal(32, loaded!.Width);
        Assert.Equal(16, loaded.Height);
        Assert.Equal(4, loaded.Bands);

        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 32; x++)
            {
                var orig = ReadPel(src, x, y);
                var got = ReadPel(loaded, x, y);
                Assert.Equal(orig[0], got[0]);
                Assert.Equal(orig[1], got[1]);
                Assert.Equal(orig[2], got[2]);
                Assert.Equal(orig[3], got[3]);
            }
    }

    // ---- Filter coverage: build PNGs with each filter type by varying
    // image content so the encoder picks different filters per row. The
    // encoder's heuristic picks whichever filter compresses best, so a
    // single test image with diverse content exercises multiple filters.

    [Fact]
    public async Task PureDecoder_LargeImageManyFilterTypes_RoundTrips()
    {
        var src = RgbDiagonal(128, 128);
        var pngBytes = await SavePngAsync(src);
        var decoded = PurePngDecoder.TryDecode(pngBytes, out int channels);
        Assert.NotNull(decoded);
        Assert.Equal(3, channels);
        var expected = CollectPixels(src);
        Assert.Equal(expected.Length, decoded!.Length);
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], decoded[i]);
    }

    // ---- Solid colour edge case ----

    [Fact]
    public async Task PureDecoder_SolidColor_RoundTrips()
    {
        // Solid colour exercises filter-None or filter-Sub since deltas are zero.
        var src = new VipsImage
        {
            Width = 10, Height = 10, Bands = 3, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? bb, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        addr[x * 3 + 0] = 100;
                        addr[x * 3 + 1] = 150;
                        addr[x * 3 + 2] = 200;
                    }
                }
                return 0;
            }
        };
        var pngBytes = await SavePngAsync(src);
        var decoded = PurePngDecoder.TryDecode(pngBytes, out int channels);
        Assert.NotNull(decoded);
        Assert.Equal(3, channels);
        Assert.Equal(100, decoded![0]);
        Assert.Equal(150, decoded[1]);
        Assert.Equal(200, decoded[2]);
    }
}
