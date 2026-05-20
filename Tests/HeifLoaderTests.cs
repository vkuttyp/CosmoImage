using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Core;
using CosmoImage.Loaders;
using CosmoImage.Savers;
using Xunit;

namespace CosmoImage.Tests;

public class HeifLoaderTests
{
    [Fact]
    public async Task LoadHeaderAsync_ValidHeic_ReturnsCorrectMetadata()
    {
        // Arrange: Minimal valid HEIC (ftyp + meta + iprp + ipco + ispe)
        // ftyp (20 bytes) + meta (12 bytes) + iprp (8 bytes) + ipco (8 bytes) + ispe (20 bytes)
        byte[] heicBytes = {
            0x00, 0x00, 0x00, 0x14, 0x66, 0x74, 0x79, 0x70, 0x68, 0x65, 0x69, 0x63, 0x00, 0x00, 0x00, 0x00, 0x68, 0x65, 0x69, 0x63, // ftyp heic
            0x00, 0x00, 0x00, 0x40, 0x6D, 0x65, 0x74, 0x61, 0x00, 0x00, 0x00, 0x00, // meta (size 64)
            0x00, 0x00, 0x00, 0x30, 0x69, 0x70, 0x72, 0x70, // iprp (size 48)
            0x00, 0x00, 0x00, 0x28, 0x69, 0x70, 0x63, 0x6F, // ipco (size 40)
            0x00, 0x00, 0x00, 0x14, 0x69, 0x73, 0x70, 0x65, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x20, 0x00, 0x00, 0x02, 0x58 // ispe (800x600)
        };

        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(heicBytes);
        await pipe.Writer.CompleteAsync();

        await using var source = new PipeVipsSource(pipe.Reader);

        // Act
        var image = await VipsHeifLoader.LoadHeaderAsync(source);

        // Assert
        Assert.NotNull(image);
        Assert.Equal(800, image.Width);
        Assert.Equal(600, image.Height);
        Assert.Equal(3, image.Bands);
    }

    [Fact]
    public async Task IsHeifAsync_ValidHeic_ReturnsTrue()
    {
        // Arrange
        byte[] heicBytes = { 0x00, 0x00, 0x00, 0x14, 0x66, 0x74, 0x79, 0x70, 0x68, 0x65, 0x69, 0x63 };
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(heicBytes);
        await pipe.Writer.CompleteAsync();

        await using var source = new PipeVipsSource(pipe.Reader);

        // Act
        bool isHeif = await VipsHeifLoader.IsHeifAsync(source);

        // Assert
        Assert.True(isHeif);
    }

    [Fact]
    public async Task LoadAsync_ValidHeic_ReturnsNull()
    {
        // Contract: HEIF decoding is not implemented (see CONTRIBUTING.md).
        // LoadAsync returns null even for a valid HEIC bitstream, so the
        // dispatch layer falls through to the next loader.
        byte[] heicBytes = { 0x00, 0x00, 0x00, 0x14, 0x66, 0x74, 0x79, 0x70, 0x68, 0x65, 0x69, 0x63 };
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(heicBytes);
        await pipe.Writer.CompleteAsync();
        await using var source = new PipeVipsSource(pipe.Reader);

        var result = await VipsHeifLoader.LoadAsync(source);
        Assert.Null(result);
    }

    [Fact]
    public async Task LoadStreamingAsync_ValidHeic_ReturnsNull()
    {
        byte[] heicBytes = { 0x00, 0x00, 0x00, 0x14, 0x66, 0x74, 0x79, 0x70, 0x68, 0x65, 0x69, 0x63 };
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(heicBytes);
        await pipe.Writer.CompleteAsync();
        await using var source = new PipeVipsSource(pipe.Reader);

        var result = await VipsHeifLoader.LoadStreamingAsync(source);
        Assert.Null(result);
    }

    [Fact]
    public async Task SaveHeifAsync_Throws()
    {
        var img = MakeMinimalImage();
        using var ms = new MemoryStream();
        var writer = PipeWriter.Create(ms);
        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => VipsHeifSaver.SaveHeifAsync(img, writer));
        Assert.Contains("HEVC", ex.Message);
    }

    [Fact]
    public async Task SaveAvifAsync_Throws()
    {
        var img = MakeMinimalImage();
        using var ms = new MemoryStream();
        var writer = PipeWriter.Create(ms);
        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => VipsHeifSaver.SaveAvifAsync(img, writer));
        Assert.Contains("AV1", ex.Message);
    }

    private static VipsImage MakeMinimalImage() => new VipsImage
    {
        Width = 4, Height = 4, Bands = 3,
        BandFormat = VipsBandFormat.UChar,
        Interpretation = VipsInterpretation.RGB,
        Coding = VipsCoding.None,
        XRes = 1.0, YRes = 1.0,
        PixelsLazy = new Lazy<byte[]>(() => new byte[48]),
    };
}
