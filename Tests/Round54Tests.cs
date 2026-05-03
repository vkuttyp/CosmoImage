using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Loaders;
using Xunit;

namespace CosmoImage.Tests;

public class Round54Tests
{
    private static VipsImage UCharSolid(int w, int h, int bands, byte v)
        => new VipsImage
        {
            Width = w, Height = h, Bands = bands, BandFormat = VipsBandFormat.UChar,
            Interpretation = bands == 1 ? VipsInterpretation.BW : VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int i = 0; i < reg.Valid.Width * bands; i++) addr[i] = v;
                }
                return 0;
            }
        };

    /// <summary>Round-trip a synthesised image to a real format then through Identify/Load.</summary>
    private static async Task<MemoryStream> SaveToMemoryAsync(VipsImage img,
        Func<VipsImage, PipeWriter, Task> saver)
    {
        var ms = new MemoryStream();
        var writer = PipeWriter.Create(ms, new StreamPipeWriterOptions(leaveOpen: true));
        await saver(img, writer);
        ms.Position = 0;
        return ms;
    }

    // ---- Identify ----

    [Fact]
    public async Task Identify_DetectsPngStream()
    {
        var src = UCharSolid(8, 8, 3, 100);
        using var ms = await SaveToMemoryAsync(src,
            (img, w) => VipsImageOps.SavePngAsync(img, w));
        var result = await VipsImageOps.IdentifyAsync(ms);
        Assert.Equal(VipsImageFormat.Png, result.Format);
        Assert.NotNull(result.Header);
        Assert.Equal(8, result.Header!.Width);
        Assert.Equal(8, result.Header.Height);
    }

    [Fact]
    public async Task Identify_DetectsJpegStream()
    {
        var src = UCharSolid(8, 8, 3, 100);
        using var ms = await SaveToMemoryAsync(src,
            (img, w) => VipsImageOps.SaveJpegAsync(img, w));
        var result = await VipsImageOps.IdentifyAsync(ms);
        Assert.Equal(VipsImageFormat.Jpeg, result.Format);
        Assert.NotNull(result.Header);
    }

    [Fact]
    public async Task Identify_HandcraftedQoiHeader()
    {
        // QOI header: "qoif" (0x71 0x6F 0x69 0x66) then big-endian
        // width / height (4 bytes each), 1 byte channels, 1 byte colorspace,
        // then RGB payload (here just a few zero pixels), and 8-byte end marker.
        var bytes = new byte[]
        {
            0x71, 0x6F, 0x69, 0x66, // "qoif"
            0x00, 0x00, 0x00, 0x02, // W = 2
            0x00, 0x00, 0x00, 0x02, // H = 2
            0x03,                   // channels = 3 (RGB)
            0x00,                   // colorspace = sRGB
            // 4 pixels of QOP-RGB: tag 0xFE = full RGB next 3 bytes.
            0xFE, 0, 0, 0,
            0xFE, 0, 0, 0,
            0xFE, 0, 0, 0,
            0xFE, 0, 0, 0,
            // End marker: 7 zero bytes + 0x01.
            0, 0, 0, 0, 0, 0, 0, 0x01,
        };
        using var ms = new MemoryStream(bytes);
        var result = await VipsImageOps.IdentifyAsync(ms);
        Assert.Equal(VipsImageFormat.Qoi, result.Format);
    }

    [Fact]
    public async Task Identify_HandcraftedGifHeader()
    {
        // "GIF89a" + minimal logical screen descriptor.
        var bytes = new byte[]
        {
            (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a',
            0x10, 0x00, 0x10, 0x00, // 16×16
            0x00, 0x00, 0x00, // GCT, bg, aspect
        };
        using var ms = new MemoryStream(bytes);
        var result = await VipsImageOps.IdentifyAsync(ms);
        Assert.Equal(VipsImageFormat.Gif, result.Format);
    }

    [Fact]
    public async Task Identify_UnknownStream_ReturnsUnknown()
    {
        // Random bytes that match no magic (avoid TGA's weak heuristic too).
        var bytes = new byte[256];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i + 1);
        // Override critical TGA fields to something invalid.
        bytes[1] = 99; bytes[2] = 99;
        using var ms = new MemoryStream(bytes);
        var result = await VipsImageOps.IdentifyAsync(ms);
        Assert.Equal(VipsImageFormat.Unknown, result.Format);
        Assert.Null(result.Header);
    }

    // ---- LoadAsync ----

    [Fact]
    public async Task LoadAsync_RoundTripsThroughPng()
    {
        var src = UCharSolid(4, 4, 3, 200);
        using var ms = await SaveToMemoryAsync(src,
            (img, w) => VipsImageOps.SavePngAsync(img, w));
        var loaded = await VipsImageOps.LoadAsync(ms);
        Assert.NotNull(loaded);
        Assert.Equal(4, loaded!.Width);
        Assert.Equal(4, loaded.Height);
        Assert.Equal(3, loaded.Bands);
        // Verify the pixel value round-tripped.
        using var reg = new VipsRegion(loaded);
        reg.Prepare(new VipsRect(0, 0, 4, 4));
        Assert.Equal(200, reg.GetAddress(2, 2)[0]);
    }

    [Fact]
    public async Task LoadAsync_RoundTripsThroughJpeg()
    {
        var src = UCharSolid(8, 8, 3, 100);
        using var ms = await SaveToMemoryAsync(src,
            (img, w) => VipsImageOps.SaveJpegAsync(img, w));
        var loaded = await VipsImageOps.LoadAsync(ms);
        Assert.NotNull(loaded);
        Assert.Equal(8, loaded!.Width);
        Assert.Equal(8, loaded.Height);
        // JPEG is lossy — value will be near 100 but not exact.
        using var reg = new VipsRegion(loaded);
        reg.Prepare(new VipsRect(0, 0, 8, 8));
        Assert.InRange(reg.GetAddress(4, 4)[0], 90, 110);
    }

    [Fact]
    public async Task LoadAsync_UnknownStream_Throws()
    {
        var bytes = new byte[256];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i + 1);
        bytes[1] = 99; bytes[2] = 99;
        using var ms = new MemoryStream(bytes);
        await Assert.ThrowsAsync<NotSupportedException>(
            async () => await VipsImageOps.LoadAsync(ms));
    }
}
