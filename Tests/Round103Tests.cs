using System;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Threading.Tasks;
using CosmoImage.Loaders;
using Xunit;

namespace CosmoImage.Tests;

public class Round103Tests
{
    /// <summary>
    /// Build a multi-frame RGBA image — N stacked frames, each
    /// canvasH rows of distinct content. Sets the n-pages /
    /// page-height / animation-delays metadata that VipsApngSaver
    /// expects.
    /// </summary>
    private static VipsImage MultiFrameRgba(int w, int canvasH, int frameCount, int delayCs = 10)
    {
        int totalH = canvasH * frameCount;
        var img = new VipsImage
        {
            Width = w, Height = totalH, Bands = 4, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? bb, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    int gy = reg.Valid.Top + y;
                    int frame = gy / canvasH;
                    int yInFrame = gy % canvasH;
                    var addr = reg.GetAddress(reg.Valid.Left, gy);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        int gx = reg.Valid.Left + x;
                        // Per-frame distinguishable content.
                        addr[x * 4 + 0] = (byte)((frame * 60 + gx * 3) & 0xFF);
                        addr[x * 4 + 1] = (byte)((yInFrame * 5) & 0xFF);
                        addr[x * 4 + 2] = (byte)((frame * 30 + yInFrame * 7) & 0xFF);
                        addr[x * 4 + 3] = 255;
                    }
                }
                return 0;
            }
        };
        img.Metadata["n-pages"] = frameCount.ToString();
        img.Metadata["page-height"] = canvasH.ToString();
        img.Metadata["animation-delays"] = string.Join(",", Enumerable.Repeat(delayCs.ToString(), frameCount));
        return img;
    }

    private static async Task<byte[]> SaveApngAsync(VipsImage img)
    {
        using var ms = new MemoryStream();
        var writer = PipeWriter.Create(ms);
        await VipsApngSaver.SaveAsync(img, writer);
        await writer.CompleteAsync();
        return ms.ToArray();
    }

    private static byte[] ReadPel(VipsImage img, int x, int y)
    {
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, img.Width, img.Height));
        return reg.GetAddress(x, y).Slice(0, img.Bands).ToArray();
    }

    // ---- Direct PureApngDecoder ----

    [Fact]
    public async Task PureApngDecoder_TwoFrameRgba_DecodesBoth()
    {
        var src = MultiFrameRgba(8, 6, 2);
        var bytes = await SaveApngAsync(src);
        var result = PureApngDecoder.TryDecode(bytes);
        Assert.NotNull(result);
        Assert.Equal(8, result!.CanvasWidth);
        Assert.Equal(6, result.CanvasHeight);
        Assert.Equal(2, result.FrameCount);
        Assert.Equal(2, result.DelaysCentiseconds.Count);
        // Stacked frames buffer: 8 × (6 × 2) × 4 bytes.
        Assert.Equal(8 * 6 * 2 * 4, result.Pixels.Length);
    }

    [Fact]
    public async Task PureApngDecoder_DelaysSurfaced()
    {
        // Magick.NET's APNG saver uses centisecond delays; we should
        // see them survive the round-trip (within 1cs rounding).
        var src = MultiFrameRgba(4, 4, 3, delayCs: 25);
        var bytes = await SaveApngAsync(src);
        var result = PureApngDecoder.TryDecode(bytes);
        Assert.NotNull(result);
        Assert.Equal(3, result!.FrameCount);
        foreach (var d in result.DelaysCentiseconds)
            Assert.InRange(d, 24, 26);  // tolerance for 1/100s rounding
    }

    [Fact]
    public void PureApngDecoder_StaticPng_ReturnsNull()
    {
        // A static PNG (no acTL) should not be claimed by the APNG decoder.
        // Synthesise a minimal PNG signature; chunk parser will see no acTL.
        var bytes = new byte[] {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,  // signature
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,  // IHDR length=13, type
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,  // 1×1
            0x08, 0x06, 0x00, 0x00, 0x00,                     // bit=8 colorType=6 etc
            0x00, 0x00, 0x00, 0x00,                           // CRC
        };
        Assert.Null(PureApngDecoder.TryDecode(bytes));
    }

    [Fact]
    public void PureApngDecoder_NullOrShort_ReturnsNull()
    {
        Assert.Null(PureApngDecoder.TryDecode(null!));
        Assert.Null(PureApngDecoder.TryDecode(Array.Empty<byte>()));
        Assert.Null(PureApngDecoder.TryDecode(new byte[7]));
    }

    // ---- LoadAsync end-to-end ----

    [Fact]
    public async Task LoadAsync_ApngBytes_ReturnsMultiFrameImage()
    {
        // Save an animated image, load via VipsPngLoader.LoadAsync, verify
        // the multi-frame metadata is preserved + image dimensions match.
        var src = MultiFrameRgba(16, 12, 4);
        var bytes = await SaveApngAsync(src);

        await using var source = new PipeVipsSource(PipeReader.Create(new MemoryStream(bytes)));
        var loaded = await VipsPngLoader.LoadAsync(source);

        Assert.NotNull(loaded);
        Assert.Equal(16, loaded!.Width);
        Assert.Equal(12 * 4, loaded.Height);
        Assert.Equal(4, loaded.Bands);
        Assert.Equal("4", loaded.Metadata["n-pages"]);
        Assert.Equal("12", loaded.Metadata["page-height"]);
        Assert.True(loaded.Metadata.ContainsKey("animation-delays"));
    }

    [Fact]
    public async Task LoadAsync_ApngFrames_HaveDistinctContent()
    {
        // Each frame in our generator has frame-dependent colour. Verify
        // that the loaded multi-frame image has DIFFERENT pixels at the
        // same (x, yInFrame) coordinate for different frames.
        var src = MultiFrameRgba(8, 6, 3);
        var bytes = await SaveApngAsync(src);
        await using var source = new PipeVipsSource(PipeReader.Create(new MemoryStream(bytes)));
        var loaded = await VipsPngLoader.LoadAsync(source);
        Assert.NotNull(loaded);

        // Sample one pixel per frame at (4, 3 within the frame).
        var f0 = ReadPel(loaded!, 4, 3);
        var f1 = ReadPel(loaded, 4, 6 + 3);
        var f2 = ReadPel(loaded, 4, 12 + 3);

        Assert.NotEqual((f0[0], f0[1], f0[2]), (f1[0], f1[1], f1[2]));
        Assert.NotEqual((f1[0], f1[1], f1[2]), (f2[0], f2[1], f2[2]));
    }

    // ---- Single-frame edge case ----

    [Fact]
    public async Task LoadAsync_SingleFrameApng_StillDecodes()
    {
        // VipsApngSaver writes single-frame as plain PNG (no acTL),
        // so this exercises the static-PNG path. Verify that's what happens.
        var src = MultiFrameRgba(8, 8, 1);
        var bytes = await SaveApngAsync(src);
        await using var source = new PipeVipsSource(PipeReader.Create(new MemoryStream(bytes)));
        var loaded = await VipsPngLoader.LoadAsync(source);
        Assert.NotNull(loaded);
        Assert.Equal(8, loaded!.Width);
        Assert.Equal(8, loaded.Height);
    }

    [Fact]
    public async Task LoadAsync_NonAnimatedPng_StillWorks()
    {
        // Sanity check: a regular non-animated PNG (saved through
        // VipsPngSaver, not the APNG saver) round-trips through the
        // existing static-PNG decode path unchanged.
        var src = new VipsImage
        {
            Width = 8, Height = 8, Bands = 3, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? bb, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width * 3; x++)
                        addr[x] = (byte)((reg.Valid.Left + reg.Valid.Top + x) & 0xFF);
                }
                return 0;
            }
        };
        using var ms = new MemoryStream();
        var w = PipeWriter.Create(ms);
        await VipsImageOps.SavePngAsync(src, w);
        await w.CompleteAsync();

        await using var source = new PipeVipsSource(PipeReader.Create(new MemoryStream(ms.ToArray())));
        var loaded = await VipsPngLoader.LoadAsync(source);
        Assert.NotNull(loaded);
        Assert.Equal(8, loaded!.Width);
        Assert.Equal(8, loaded.Height);
        Assert.Equal(3, loaded.Bands);
        // Should NOT have n-pages metadata (static PNG).
        Assert.False(loaded.Metadata.ContainsKey("n-pages"));
    }
}
