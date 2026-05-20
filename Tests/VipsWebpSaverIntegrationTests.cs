using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Core;
using CosmoImage.Loaders;
using CosmoImage.Savers;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// End-to-end tests for the pure-managed <see cref="VipsWebpSaver"/>: build
/// a VipsImage in memory, save through the public API, decode the resulting
/// bytes via <see cref="PureWebpLossless"/>, verify pixels round-trip.
/// </summary>
public class VipsWebpSaverIntegrationTests
{
    [Fact]
    public async Task SaveLossless_Rgba_RoundTrips()
    {
        const int W = 16, H = 8;
        var rgba = new byte[W * H * 4];
        for (int i = 0; i < rgba.Length; i++) rgba[i] = (byte)((i * 11) & 0xFF);
        // Force alpha so the "has alpha" path is unambiguous.
        for (int p = 0; p < W * H; p++) rgba[p * 4 + 3] = (byte)(p & 0xFF);

        var img = MakeImage(W, H, 4, rgba);
        var encoded = await EncodeViaSaver(img, lossless: true);

        var decoded = PureWebpLossless.TryDecode(encoded);
        Assert.NotNull(decoded);
        Assert.Equal(W, decoded!.Width);
        Assert.Equal(H, decoded.Height);
        var got = decoded.Pixels!;
        Assert.Equal(rgba, got);
    }

    [Fact]
    public async Task SaveLossless_Rgb_ExpandsToRgbaAndRoundTrips()
    {
        const int W = 8, H = 4;
        var rgb = new byte[W * H * 3];
        for (int i = 0; i < rgb.Length; i++) rgb[i] = (byte)((i * 13) & 0xFF);

        var img = MakeImage(W, H, 3, rgb);
        var encoded = await EncodeViaSaver(img, lossless: true);

        var decoded = PureWebpLossless.TryDecode(encoded);
        Assert.NotNull(decoded);
        Assert.Equal(4, decoded!.Bands);
        var got = decoded.Pixels!;

        // Decoded RGB matches input; alpha forced to 255.
        for (int p = 0; p < W * H; p++)
        {
            Assert.Equal(rgb[p * 3 + 0], got[p * 4 + 0]);
            Assert.Equal(rgb[p * 3 + 1], got[p * 4 + 1]);
            Assert.Equal(rgb[p * 3 + 2], got[p * 4 + 2]);
            Assert.Equal((byte)255, got[p * 4 + 3]);
        }
    }

    [Fact]
    public async Task SaveLossless_Gray_ExpandsToRgbaAndRoundTrips()
    {
        const int W = 6, H = 4;
        var gray = new byte[W * H];
        for (int i = 0; i < gray.Length; i++) gray[i] = (byte)(i * 17);

        var img = MakeImage(W, H, 1, gray);
        var encoded = await EncodeViaSaver(img, lossless: true);

        var decoded = PureWebpLossless.TryDecode(encoded);
        Assert.NotNull(decoded);
        var got = decoded!.Pixels!;
        for (int p = 0; p < W * H; p++)
        {
            byte g = gray[p];
            Assert.Equal(g, got[p * 4 + 0]);
            Assert.Equal(g, got[p * 4 + 1]);
            Assert.Equal(g, got[p * 4 + 2]);
            Assert.Equal((byte)255, got[p * 4 + 3]);
        }
    }

    [Fact]
    public async Task SaveLossy_Throws()
    {
        var img = MakeImage(4, 4, 4, new byte[64]);
        await Assert.ThrowsAsync<NotSupportedException>(
            () => EncodeViaSaver(img, lossless: false));
    }

    [Fact]
    public async Task SaveAnimated_Throws()
    {
        var img = MakeImage(4, 8, 4, new byte[128]);  // 4×8 means 2 frames of 4×4
        img.Metadata["n-pages"] = "2";
        img.Metadata["page-height"] = "4";
        await Assert.ThrowsAsync<NotSupportedException>(
            () => EncodeViaSaver(img, lossless: true));
    }

    [Fact]
    public async Task SaveWithIcc_RoundTripsThroughLoader()
    {
        var rgba = new byte[8 * 4 * 4];
        for (int i = 0; i < rgba.Length; i++) rgba[i] = (byte)((i * 5) & 0xFF);
        var img = MakeImage(8, 4, 4, rgba);

        var icc = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04, 0x05 };  // odd length to exercise padding
        img.MetadataBlobs["icc"] = icc;

        var encoded = await EncodeViaSaver(img, lossless: true);

        // Loader extracts the ICCP chunk back.
        var decoded = PureWebpLossless.TryDecode(encoded);
        Assert.NotNull(decoded);

        // Use the loader's metadata walker (LoadAsync would, but here we
        // invoke the same extraction by reading back via TryDecode plus
        // a hand-rolled RIFF walk that mirrors VipsWebpLoader.AttachWebpMetadata).
        var pulled = ExtractChunk(encoded, "ICCP");
        Assert.Equal(icc, pulled);
    }

    [Fact]
    public async Task SaveWithAllThreeMetadata_RoundTrips()
    {
        var img = MakeImage(8, 4, 4, new byte[128]);
        var exif = new byte[] { 1, 2, 3 };
        var xmp  = new byte[] { 4, 5, 6, 7 };
        var icc  = new byte[] { 8, 9, 10, 11, 12 };
        img.MetadataBlobs["exif"] = exif;
        img.MetadataBlobs["xmp"]  = xmp;
        img.MetadataBlobs["icc"]  = icc;

        var encoded = await EncodeViaSaver(img, lossless: true);

        Assert.Equal(exif, ExtractChunk(encoded, "EXIF"));
        Assert.Equal(xmp,  ExtractChunk(encoded, "XMP "));
        Assert.Equal(icc,  ExtractChunk(encoded, "ICCP"));

        // VP8X flag byte should announce all three (+ alpha since bands=4).
        int vp8xOff = FindChunkOffset(encoded, "VP8X");
        Assert.True(vp8xOff >= 0, "expected VP8X chunk in muxed output");
        byte flags = encoded[vp8xOff + 8];
        Assert.True((flags & 0x20) != 0, "ICCP flag bit");
        Assert.True((flags & 0x08) != 0, "EXIF flag bit");
        Assert.True((flags & 0x04) != 0, "XMP flag bit");
    }

    [Fact]
    public async Task SaveNoMetadata_DoesNotAddVp8x()
    {
        // The mux is a no-op when there's no metadata; output stays a
        // naked VP8L file.
        var img = MakeImage(4, 4, 4, new byte[64]);
        var encoded = await EncodeViaSaver(img, lossless: true);
        Assert.Equal(-1, FindChunkOffset(encoded, "VP8X"));
    }

    [Fact]
    public async Task SaveInvalidBands_Throws()
    {
        var img = MakeImage(4, 4, 2, new byte[32]);  // 2 bands not supported
        await Assert.ThrowsAsync<NotSupportedException>(
            () => EncodeViaSaver(img, lossless: true));
    }

    // ---- helpers ----

    private static VipsImage MakeImage(int width, int height, int bands, byte[] pixels)
    {
        return new VipsImage
        {
            Width = width,
            Height = height,
            Bands = bands,
            BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            Coding = VipsCoding.None,
            XRes = 1.0,
            YRes = 1.0,
            PixelsLazy = new Lazy<byte[]>(() => pixels),
        };
    }

    private static async Task<byte[]> EncodeViaSaver(VipsImage img, bool lossless)
    {
        using var ms = new MemoryStream();
        var writer = PipeWriter.Create(ms);
        await VipsWebpSaver.SaveAsync(img, writer, quality: 75, lossless: lossless);
        return ms.ToArray();
    }

    /// <summary>Walk RIFF chunks and return the payload of the named fourcc, or null.</summary>
    private static byte[]? ExtractChunk(byte[] webp, string fourcc)
    {
        int off = FindChunkOffset(webp, fourcc);
        if (off < 0) return null;
        int len = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(webp.AsSpan(off + 4, 4));
        return webp.AsSpan(off + 8, len).ToArray();
    }

    private static int FindChunkOffset(byte[] webp, string fourcc)
    {
        if (webp.Length < 12 + 8) return -1;
        int p = 12;
        while (p + 8 <= webp.Length)
        {
            bool match =
                webp[p + 0] == (byte)fourcc[0] &&
                webp[p + 1] == (byte)fourcc[1] &&
                webp[p + 2] == (byte)fourcc[2] &&
                webp[p + 3] == (byte)fourcc[3];
            int len = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(webp.AsSpan(p + 4, 4));
            if (match) return p;
            p += 8 + len + (len & 1);
        }
        return -1;
    }
}
