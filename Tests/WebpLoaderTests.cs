using System;
using System.IO.Pipelines;
using System.Threading.Tasks;
using Xunit;

namespace CosmoImage.Tests;

public class WebpLoaderTests
{
    [Fact]
    public async Task LoadHeaderAsync_ValidWebpLossy_ReturnsCorrectMetadata()
    {
        // Arrange: Minimal valid WebP VP8 (Lossy)
        // 128x256
        byte[] webpBytes = {
            0x52, 0x49, 0x46, 0x46, 0x22, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50, // RIFF WEBP
            0x56, 0x50, 0x38, 0x20, 0x16, 0x00, 0x00, 0x00, // VP8 
            0x00, 0x00, 0x00, 0x9D, 0x01, 0x2A, // Frame tag + Signature
            0x80, 0x00, 0x00, 0x01 // Width 128, Height 256
        };

        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(webpBytes);
        await pipe.Writer.CompleteAsync();

        await using var source = new PipeVipsSource(pipe.Reader);

        // Act
        var image = await VipsWebpLoader.LoadHeaderAsync(source);

        // Assert
        Assert.NotNull(image);
        Assert.Equal(128, image.Width);
        Assert.Equal(256, image.Height);
        Assert.Equal(3, image.Bands);
    }

    [Fact]
    public async Task LoadHeaderAsync_ValidWebpLossless_ReturnsCorrectMetadata()
    {
        // Arrange: Minimal valid WebP VP8L (Lossless)
        // 64x128, with alpha
        // Width = 64-1 = 63 (0x3F), Height = 128-1 = 127 (0x7F)
        // Bits: Alpha (1) | Height (14) | Width (14)
        // 0x1 | 0x007F << 14 | 0x003F = 0x101FC03F
        uint val = 0x101FC03F;
        byte[] webpBytes = {
            0x52, 0x49, 0x46, 0x46, 0x14, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50, // RIFF WEBP
            0x56, 0x50, 0x38, 0x4C, 0x05, 0x00, 0x00, 0x00, // VP8L
            0x2F, // Signature
            (byte)(val & 0xFF), (byte)((val >> 8) & 0xFF), (byte)((val >> 16) & 0xFF), (byte)((val >> 24) & 0xFF)
        };

        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(webpBytes);
        await pipe.Writer.CompleteAsync();

        await using var source = new PipeVipsSource(pipe.Reader);

        // Act
        var image = await VipsWebpLoader.LoadHeaderAsync(source);

        // Assert
        Assert.NotNull(image);
        Assert.Equal(64, image.Width);
        Assert.Equal(128, image.Height);
        Assert.Equal(4, image.Bands);
    }

    [Fact]
    public async Task IsWebpAsync_ValidWebp_ReturnsTrue()
    {
        // Arrange
        byte[] webpBytes = { 0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50 };
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(webpBytes);
        await pipe.Writer.CompleteAsync();

        await using var source = new PipeVipsSource(pipe.Reader);

        // Act
        bool isWebp = await VipsWebpLoader.IsWebpAsync(source);

        // Assert
        Assert.True(isWebp);
    }
}
