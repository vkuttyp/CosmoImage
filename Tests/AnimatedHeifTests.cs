using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Loaders;
using ImageMagick;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Coverage for animated HEIF/AVIF sequence loading. We synthesise via AVIF
/// because Magick.NET-Q8-arm64 ships the AVIF encoder; HEIC encode is not
/// available on this build (probe confirmed). The HEIF and AVIF code paths
/// in VipsHeifLoader are identical — both just go through MagickImageCollection
/// — so AVIF coverage exercises the same code as a HEIC sequence would.
/// </summary>
public class AnimatedHeifTests
{
    private static IVipsSource SourceFromBytes(byte[] bytes)
        => new PipeVipsSource(PipeReader.Create(new MemoryStream(bytes)));

    private static byte[] SynthesizeAvifSequence(int width, int height, int frames)
    {
        using var col = new MagickImageCollection();
        // Distinct per-frame fill so a frame-ordering bug shows up clearly.
        var palette = new[] { MagickColors.Red, MagickColors.Green, MagickColors.Blue, MagickColors.Yellow };
        for (int i = 0; i < frames; i++)
        {
            var frame = new MagickImage(palette[i % palette.Length], (uint)width, (uint)height);
            frame.AnimationDelay = (uint)(10 + i); // unique per frame for ordering check
            col.Add(frame);
        }
        using var ms = new MemoryStream();
        col.Write(ms, MagickFormat.Avif);
        return ms.ToArray();
    }

    [Fact]
    public async Task SingleFrameAvif_LoadsAsSingleFrame()
    {
        var bytes = SynthesizeAvifSequence(8, 8, frames: 1);
        var img = await VipsHeifLoader.LoadAsync(SourceFromBytes(bytes));
        Assert.NotNull(img);
        Assert.Equal(8, img!.Height);
        Assert.False(img.Metadata.ContainsKey("n-pages"));
    }

    [Fact]
    public async Task MultiFrameAvif_LoadAsync_StacksFramesIntoTallBuffer()
    {
        const int W = 8, H = 8, N = 3;
        var bytes = SynthesizeAvifSequence(W, H, frames: N);

        var img = await VipsHeifLoader.LoadAsync(SourceFromBytes(bytes));
        Assert.NotNull(img);
        Assert.Equal(W, img!.Width);
        Assert.Equal(H * N, img.Height); // tall buffer = page-height × n-pages
        Assert.Equal(N.ToString(), img.Metadata["n-pages"]);
        Assert.Equal(H.ToString(), img.Metadata["page-height"]);
    }

    [Fact]
    public async Task MultiFrameAvif_LoadStreamingAsync_MatchesLoadAsync()
    {
        const int W = 8, H = 8, N = 2;
        var bytes = SynthesizeAvifSequence(W, H, frames: N);

        var lazy = await VipsHeifLoader.LoadAsync(SourceFromBytes(bytes));
        var streamed = await VipsHeifLoader.LoadStreamingAsync(SourceFromBytes(bytes));
        Assert.NotNull(lazy);
        Assert.NotNull(streamed);
        Assert.Equal(lazy!.Width, streamed!.Width);
        Assert.Equal(lazy.Height, streamed.Height);
        Assert.Equal(lazy.Metadata["n-pages"], streamed.Metadata["n-pages"]);
        Assert.Equal(lazy.Metadata["page-height"], streamed.Metadata["page-height"]);
    }

    [Fact]
    public async Task MultiFrameAvif_FrameOrderIsPreserved()
    {
        // Distinct fill per frame: the tall buffer should contain Red rows
        // first, then Green, then Blue. Sample the centre of each band.
        const int W = 16, H = 16, N = 3;
        var bytes = SynthesizeAvifSequence(W, H, frames: N);
        var img = await VipsHeifLoader.LoadAsync(SourceFromBytes(bytes));
        Assert.NotNull(img);

        using var reg = new VipsRegion(img!);
        reg.Prepare(new VipsRect(0, 0, W, H * N));

        // AVIF is lossy so don't compare exact RGB values; check which
        // channel dominates per frame band.
        AssertDominantChannel(reg.GetAddress(8, 8), expectedDominant: 0);              // Red frame
        AssertDominantChannel(reg.GetAddress(8, H + 8), expectedDominant: 1);          // Green frame
        AssertDominantChannel(reg.GetAddress(8, 2 * H + 8), expectedDominant: 2);      // Blue frame
    }

    private static void AssertDominantChannel(System.Span<byte> pixel, int expectedDominant)
    {
        // pixel layout: R, G, B, [A]. Dominant = highest of R/G/B.
        int max = 0;
        for (int i = 1; i < 3; i++) if (pixel[i] > pixel[max]) max = i;
        Assert.Equal(expectedDominant, max);
    }
}
