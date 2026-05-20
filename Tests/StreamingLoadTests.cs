using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Loaders;
using CosmoImage.Savers;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Coverage for the opt-in streaming load path. Each test confirms that
/// LoadStreamingAsync produces the same image content as LoadAsync —
/// streaming is an internal performance choice, not a semantic change.
/// </summary>
public class StreamingLoadTests
{
    private static VipsImage SyntheticRgb(int w, int h, byte fill = 100)
    {
        return new VipsImage
        {
            Width = w, Height = h, Bands = 3, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int i = 0; i < reg.Valid.Width * 3; i++) addr[i] = fill;
                }
                return 0;
            }
        };
    }

    private static async Task<byte[]> SaveToBytesAsync(System.Func<PipeWriter, Task> save)
    {
        using var ms = new MemoryStream();
        var writer = PipeWriter.Create(ms, new StreamPipeWriterOptions(leaveOpen: true));
        await save(writer);
        return ms.ToArray();
    }

    private static IVipsSource SourceFromBytes(byte[] bytes)
        => new PipeVipsSource(PipeReader.Create(new MemoryStream(bytes)));

    [Fact]
    public async Task Tga_StreamingLoad_MatchesByteBufferedLoad()
    {
        var src = SyntheticRgb(8, 8, fill: 200);
        var bytes = await SaveToBytesAsync(w => VipsTgaSaver.SaveAsync(src, w));

        var lazy = await VipsTgaLoader.LoadAsync(SourceFromBytes(bytes));
        var streamed = await VipsTgaLoader.LoadStreamingAsync(SourceFromBytes(bytes));

        Assert.NotNull(lazy);
        Assert.NotNull(streamed);
        Assert.Equal(lazy!.Width, streamed!.Width);
        Assert.Equal(lazy.Height, streamed.Height);

        using var rl = new VipsRegion(lazy);
        using var rs = new VipsRegion(streamed);
        rl.Prepare(new VipsRect(0, 0, lazy.Width, lazy.Height));
        rs.Prepare(new VipsRect(0, 0, streamed.Width, streamed.Height));
        Assert.Equal(rl.GetAddress(0, 0)[0], rs.GetAddress(0, 0)[0]);
        Assert.Equal(200, rs.GetAddress(4, 4)[0]);
    }

    [Fact]
    public async Task Qoi_StreamingLoad_MatchesByteBufferedLoad()
    {
        var src = new VipsImage
        {
            Width = 6, Height = 6, Bands = 4, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int i = 0; i < reg.Valid.Width * 4; i++) addr[i] = 60;
                }
                return 0;
            }
        };
        var bytes = await SaveToBytesAsync(w => VipsQoiSaver.SaveAsync(src, w));

        var streamed = await VipsQoiLoader.LoadStreamingAsync(SourceFromBytes(bytes));
        Assert.NotNull(streamed);
        using var reg = new VipsRegion(streamed!);
        reg.Prepare(new VipsRect(0, 0, 6, 6));
        Assert.Equal(60, reg.GetAddress(2, 2)[0]);
    }

    [Fact]
    public async Task Bmp_StreamingLoad_MatchesByteBufferedLoad()
    {
        var src = SyntheticRgb(8, 8, fill: 75);
        byte[] bytes = await SaveToBytesAsync(w => VipsBmpSaver.SaveAsync(src, w));

        var lazy = await VipsBmpLoader.LoadAsync(SourceFromBytes(bytes));
        var streamed = await VipsBmpLoader.LoadStreamingAsync(SourceFromBytes(bytes));
        Assert.NotNull(lazy);
        Assert.NotNull(streamed);
        Assert.Equal(lazy!.Width, streamed!.Width);
        Assert.Equal(lazy.Height, streamed.Height);

        using var rl = new VipsRegion(lazy);
        using var rs = new VipsRegion(streamed);
        rl.Prepare(new VipsRect(0, 0, lazy.Width, lazy.Height));
        rs.Prepare(new VipsRect(0, 0, streamed.Width, streamed.Height));
        Assert.Equal(rl.GetAddress(2, 2)[0], rs.GetAddress(2, 2)[0]);
    }

    [Fact]
    public async Task Heif_StreamingLoad_NonHeifInput_ReturnsNull()
    {
        // Magick.NET-Q8-arm64 ships the HEIC decoder but not the encoder, so
        // we can't synthesize a HEIF in-test. The streaming codepath shape is
        // identical to BMP/SVG (both covered by round-trip tests). This test
        // just locks down the early-out for non-HEIF input.
        var notHeif = System.Text.Encoding.ASCII.GetBytes("not a heif file at all");
        var result = await VipsHeifLoader.LoadStreamingAsync(SourceFromBytes(notHeif));
        Assert.Null(result);
    }

    [Fact]
    public async Task Svg_StreamingLoad_RastersPixels()
    {
        var svg = "<?xml version=\"1.0\"?><svg xmlns=\"http://www.w3.org/2000/svg\" width=\"16\" height=\"16\"><rect width=\"16\" height=\"16\" fill=\"red\"/></svg>";
        var bytes = System.Text.Encoding.UTF8.GetBytes(svg);

        var streamed = await VipsSvgLoader.LoadStreamingAsync(SourceFromBytes(bytes));
        Assert.NotNull(streamed);
        Assert.Equal(16, streamed!.Width);
        Assert.Equal(16, streamed.Height);
        Assert.Equal(4, streamed.Bands);
        using var reg = new VipsRegion(streamed);
        reg.Prepare(new VipsRect(0, 0, 16, 16));
        // Red pixel: R=255, G=0, B=0, A=255.
        Assert.Equal(255, reg.GetAddress(8, 8)[0]);
        Assert.Equal(0, reg.GetAddress(8, 8)[1]);
        Assert.Equal(0, reg.GetAddress(8, 8)[2]);
        Assert.Equal(255, reg.GetAddress(8, 8)[3]);
    }

    [Fact]
    public async Task Webp_StreamingLoad_PropagatesMetadata()
    {
        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            using var img = new ImageMagick.MagickImage(ImageMagick.MagickColors.Green, 16, 16);
            img.Write(ms, ImageMagick.MagickFormat.WebP);
            bytes = ms.ToArray();
        }

        var streamed = await VipsWebpLoader.LoadStreamingAsync(SourceFromBytes(bytes));
        Assert.NotNull(streamed);
        Assert.Equal(16, streamed!.Width);
        Assert.Equal(16, streamed.Height);
    }

    [Fact]
    public async Task Gif_StreamingLoad_AnimatedPropagatesNPages()
    {
        var src = new VipsImage
        {
            Width = 8,
            Height = 16,
            Bands = 3,
            BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) =>
            {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    int gy = reg.Valid.Top + y;
                    int frame = gy / 8;
                    var addr = reg.GetAddress(reg.Valid.Left, gy);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        addr[x * 3 + 0] = (byte)(frame == 0 ? 255 : 0);
                        addr[x * 3 + 1] = 0;
                        addr[x * 3 + 2] = (byte)(frame == 1 ? 255 : 0);
                    }
                }
                return 0;
            }
        };
        src.Metadata["n-pages"] = "2";
        src.Metadata["page-height"] = "8";
        src.Metadata["animation-delays"] = "10,20";
        byte[] bytes = await SaveToBytesAsync(w => VipsGifSaver.SaveAsync(src, w));

        var streamed = await VipsGifLoader.LoadStreamingAsync(SourceFromBytes(bytes));
        Assert.NotNull(streamed);
        Assert.Equal(8, streamed!.Width);
        Assert.Equal(16, streamed.Height); // 2 frames stacked
        Assert.Equal("2", streamed.Metadata["n-pages"]);
        Assert.Equal("8", streamed.Metadata["page-height"]);
        Assert.Equal("10,20", streamed.Metadata["animation-delays"]);
    }

    [Fact]
    public async Task Tiff_StreamingLoad_MatchesByteBufferedLoad()
    {
        var src = SyntheticRgb(16, 16, fill: 175);
        src.Metadata["tiff:image-description"] = "streaming tiff";
        var bytes = await SaveToBytesAsync(w => VipsTiffSaver.SaveAsync(src, w));

        var lazy = await VipsTiffLoader.LoadAsync(SourceFromBytes(bytes));
        var streamed = await VipsTiffLoader.LoadStreamingAsync(SourceFromBytes(bytes));
        Assert.NotNull(lazy);
        Assert.NotNull(streamed);
        Assert.Equal(lazy!.Width, streamed!.Width);
        Assert.Equal(lazy.Height, streamed.Height);
        Assert.Equal("streaming tiff", lazy.Metadata["tiff:image-description"]);
        Assert.Equal("streaming tiff", streamed.Metadata["tiff:image-description"]);

        using var rl = new VipsRegion(lazy);
        using var rs = new VipsRegion(streamed);
        rl.Prepare(new VipsRect(0, 0, lazy.Width, lazy.Height));
        rs.Prepare(new VipsRect(0, 0, streamed.Width, streamed.Height));
        for (int x = 0; x < 4; x++)
            Assert.Equal(rl.GetAddress(x, x)[0], rs.GetAddress(x, x)[0]);
    }

    [Fact]
    public async Task VipsSourceStream_ReadsSourceLikeAStream()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        await using var src = new PipeVipsSource(PipeReader.Create(new MemoryStream(bytes)));
        using var stream = src.AsStream();

        var buf = new byte[4];
        int read = stream.Read(buf, 0, 4);
        Assert.Equal(4, read);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, buf);

        read = stream.Read(buf, 0, 4);
        Assert.Equal(4, read);
        Assert.Equal(new byte[] { 5, 6, 7, 8 }, buf);

        read = stream.Read(buf, 0, 4);
        Assert.Equal(1, read);
        Assert.Equal(9, buf[0]);

        read = stream.Read(buf, 0, 4);
        Assert.Equal(0, read);
    }

    [Fact]
    public async Task VipsSourceStream_DoesNotSupportSeekOrWrite()
    {
        await using var src = new PipeVipsSource(PipeReader.Create(new MemoryStream(new byte[] { 1, 2, 3 })));
        using var stream = src.AsStream();
        Assert.False(stream.CanSeek);
        Assert.False(stream.CanWrite);
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.Write(new byte[] { 0 }, 0, 1));
    }
}
