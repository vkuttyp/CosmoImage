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
    public async Task Tiff_StreamingLoad_MatchesByteBufferedLoad()
    {
        var src = SyntheticRgb(16, 16, fill: 175);
        var bytes = await SaveToBytesAsync(w => VipsTiffSaver.SaveAsync(src, w));

        var lazy = await VipsTiffLoader.LoadAsync(SourceFromBytes(bytes));
        var streamed = await VipsTiffLoader.LoadStreamingAsync(SourceFromBytes(bytes));
        Assert.NotNull(lazy);
        Assert.NotNull(streamed);
        Assert.Equal(lazy!.Width, streamed!.Width);
        Assert.Equal(lazy.Height, streamed.Height);

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
