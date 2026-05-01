using System;
using System.IO.Pipelines;
using System.Threading.Tasks;
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
}
