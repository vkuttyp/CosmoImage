using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Loaders;
using CosmoImage.Savers;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 199 — <see cref="PureJpegEncoder"/>: pure-C# baseline JPEG
/// encoder. Phase 2 of the JpegLibrary drop. Tests verify that the
/// encoder produces valid baseline JPEGs by decoding them through
/// <see cref="PureJpegDecoder"/> (round 198) and checking pixel
/// content within JPEG quantization tolerance.
/// </summary>
public class Round199Tests
{
    private static byte[] GreyscaleGradient(int w, int h)
    {
        var bytes = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                bytes[y * w + x] = (byte)(((x * 256) / w + (y * 128) / h) & 0xFF);
        return bytes;
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
                bytes[o + 2] = 128;
            }
        return bytes;
    }

    [Fact]
    public void Encode_Greyscale_DecodesViaPureDecoder()
    {
        // 32×24 greyscale gradient → encode → decode via PureJpegDecoder.
        // Pixels should round-trip within JPEG quantization noise.
        var src = GreyscaleGradient(32, 24);
        var jpeg = PureJpegEncoder.Encode(src, 32, 24, channels: 1, quality: 90);

        // Verify it's a real JPEG.
        Assert.Equal(0xFF, jpeg[0]);
        Assert.Equal(0xD8, jpeg[1]); // SOI
        Assert.Equal(0xFF, jpeg[^2]);
        Assert.Equal(0xD9, jpeg[^1]); // EOI

        var decoded = PureJpegDecoder.TryDecode(jpeg, out int w, out int h, out int channels);
        Assert.NotNull(decoded);
        Assert.Equal(32, w);
        Assert.Equal(24, h);
        Assert.Equal(1, channels);

        // JPEG quantization at quality 90 gives ~1-2 LSB of noise on
        // most pixels, with occasional ±10-15 spikes on block-boundary
        // samples. Match round 198's tolerance envelope.
        for (int y = 0; y < 24; y++)
            for (int x = 0; x < 32; x++)
            {
                int diff = decoded![y * 32 + x] - src[y * 32 + x];
                Assert.InRange(diff, -15, 15);
            }
    }

    [Fact]
    public void Encode_Rgb_DecodesViaPureDecoder()
    {
        // 32×32 RGB gradient → encode → decode + ConvertColorSpace.
        // 4:2:0 chroma subsampling means chroma values are averaged
        // across 2×2 luma neighbourhoods, so tolerance is wider.
        var src = RgbGradient(32, 32);
        var jpeg = PureJpegEncoder.Encode(src, 32, 32, channels: 3, quality: 90);

        var decoded = PureJpegDecoder.TryDecode(jpeg, out int w, out int h, out int channels);
        Assert.NotNull(decoded);
        Assert.Equal(32, w);
        Assert.Equal(32, h);
        Assert.Equal(3, channels);

        // Decoder returns YCbCr; convert to RGB for comparison.
        VipsJpegLoader.ConvertColorSpace(decoded!, w, h, channels, VipsJpegLoader.JpegColorSpace.YCbCr);

        // Spot-check: pixels at 4-px intervals, ±25 LSB envelope.
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
    public void Encode_QualityRange_AffectsFileSize()
    {
        // Higher quality → larger file (more bits preserved).
        var src = RgbGradient(32, 32);
        var lowQ = PureJpegEncoder.Encode(src, 32, 32, channels: 3, quality: 30);
        var hiQ = PureJpegEncoder.Encode(src, 32, 32, channels: 3, quality: 95);
        Assert.True(hiQ.Length > lowQ.Length, $"q95 ({hiQ.Length}) should be larger than q30 ({lowQ.Length})");
    }

    [Fact]
    public void Encode_QualityClamped()
    {
        var src = GreyscaleGradient(8, 8);
        // Out-of-range quality clamped to [1, 100] without throwing.
        var lo = PureJpegEncoder.Encode(src, 8, 8, channels: 1, quality: -5);
        var hi = PureJpegEncoder.Encode(src, 8, 8, channels: 1, quality: 200);
        Assert.True(lo.Length > 0);
        Assert.True(hi.Length > 0);
    }

    [Fact]
    public void Encode_ContainsRequiredMarkers()
    {
        var src = RgbGradient(16, 16);
        var jpeg = PureJpegEncoder.Encode(src, 16, 16, channels: 3, quality: 75);

        // Walk markers and verify SOF0 + SOS + at least 2 DQT + 4 DHT.
        int sof0 = 0, sos = 0, dqt = 0, dht = 0;
        int p = 2;
        while (p + 1 < jpeg.Length)
        {
            if (jpeg[p] != 0xFF) break;
            byte m = jpeg[p + 1];
            p += 2;
            if (m == 0xD9) break; // EOI
            if (m == 0xC0) sof0++;
            else if (m == 0xDA) sos++;
            else if (m == 0xDB) dqt++;
            else if (m == 0xC4) dht++;
            int len = (jpeg[p] << 8) | jpeg[p + 1];
            p += len;
            if (m == 0xDA)
            {
                // Skip entropy-coded data until next marker.
                while (p < jpeg.Length - 1)
                {
                    if (jpeg[p] == 0xFF && jpeg[p + 1] != 0x00 && jpeg[p + 1] != 0xFF)
                        break;
                    p++;
                }
            }
        }
        Assert.Equal(1, sof0);
        Assert.Equal(1, sos);
        Assert.Equal(2, dqt); // luminance + chrominance
        Assert.Equal(4, dht); // 2 DC + 2 AC
    }

    [Fact]
    public async Task EndToEnd_VipsJpegSaverUsesPurePath()
    {
        // VipsJpegSaver routes 1/3-band through PureJpegEncoder. Verify
        // a synthetic image saves and loads back with sensible content.
        var src = new VipsImage
        {
            Width = 32, Height = 32, Bands = 3, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) =>
            {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        addr[x * 3 + 0] = (byte)((reg.Valid.Left + x) * 8);
                        addr[x * 3 + 1] = (byte)((reg.Valid.Top + y) * 8);
                        addr[x * 3 + 2] = 128;
                    }
                }
                return 0;
            }
        };

        using var ms = new MemoryStream();
        var writer = PipeWriter.Create(ms);
        await VipsJpegSaver.SaveAsync(src, writer, quality: 85);

        var loadSrc = new PipeVipsSource(PipeReader.Create(new MemoryStream(ms.ToArray())));
        var loaded = await VipsJpegLoader.LoadAsync(loadSrc);
        Assert.NotNull(loaded);
        Assert.Equal(32, loaded!.Width);
        Assert.Equal(32, loaded.Height);
        Assert.Equal(3, loaded.Bands);

        // Sanity: roughly the same gradient survived.
        using var reg = new VipsRegion(loaded);
        reg.Prepare(new VipsRect(0, 0, 32, 32));
        var addr = reg.GetAddress(16, 16);
        Assert.InRange(addr[0], 100, 156); // ~128 ± quantization
        Assert.InRange(addr[1], 100, 156);
    }
}
