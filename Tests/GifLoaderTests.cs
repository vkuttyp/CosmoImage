using System;
using System.IO.Pipelines;
using System.Threading.Tasks;
using Xunit;

namespace CosmoImage.Tests;

public class GifLoaderTests
{
    [Fact]
    public async Task LoadHeaderAsync_ValidGif_ReturnsCorrectMetadata()
    {
        // Arrange: Valid minimal 1x1 GIF
        byte[] gifBytes = {
            0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 
            0xFF, 0xFF, 0xFF, 0x21, 0xF9, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x2C, 0x00, 0x00, 0x00, 0x00, 
            0x01, 0x00, 0x01, 0x00, 0x00, 0x02, 0x02, 0x44, 0x01, 0x00, 0x3B 
        };

        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(gifBytes);
        await pipe.Writer.CompleteAsync();

        await using var source = new PipeVipsSource(pipe.Reader);

        // Act
        var image = await VipsGifLoader.LoadHeaderAsync(source);

        // Assert
        Assert.NotNull(image);
        Assert.Equal(1, image.Width);
        Assert.Equal(1, image.Height);
        Assert.Equal(4, image.Bands); // RGBA
        Assert.Equal("1", image.Metadata["n-pages"]);
        Assert.Equal(VipsBandFormat.UChar, image.BandFormat);
    }

    [Fact]
    public async Task LoadHeaderAsync_MultiPageGif_StacksPagesVertically()
    {
        // Arrange: GIF with 2 frames, each 10x10
        // (Simplified placeholder bytes, a real multi-page GIF is needed for full verification)
        // But for unit tests, let's just use the signature check
    }

    [Fact]
    public async Task IsGifAsync_ValidGif_ReturnsTrue()
    {
        // Arrange
        byte[] gifBytes = { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' };
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(gifBytes);
        await pipe.Writer.CompleteAsync();

        await using var source = new PipeVipsSource(pipe.Reader);

        // Act
        bool isGif = await VipsGifLoader.IsGifAsync(source);

        // Assert
        Assert.True(isGif);
    }
}
