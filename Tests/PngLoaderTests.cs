using System;
using System.IO.Pipelines;
using System.Threading.Tasks;
using Xunit;

namespace CosmoImage.Tests;

public class PngLoaderTests
{
    [Fact]
    public async Task LoadHeaderAsync_ValidPng_ReturnsCorrectMetadata()
    {
        // Arrange: Minimal valid PNG IHDR
        // Signature + IHDR (100x200, 8-bit RGB, no interlace)
        byte[] pngBytes = {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, // Signature
            0x00, 0x00, 0x00, 0x0D, // Length 13
            0x49, 0x48, 0x44, 0x52, // "IHDR"
            0x00, 0x00, 0x00, 0x64, // Width 100
            0x00, 0x00, 0x00, 0xC8, // Height 200
            0x08, // 8-bit
            0x02, // RGB
            0x00, 0x00, 0x00, // Compression, Filter, Interlace
            0x00, 0x00, 0x00, 0x00 // CRC (dummy)
        };

        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(pngBytes);
        await pipe.Writer.CompleteAsync();

        await using var source = new PipeVipsSource(pipe.Reader);

        // Act
        var image = await VipsPngLoader.LoadHeaderAsync(source);

        // Assert
        Assert.NotNull(image);
        Assert.Equal(100, image.Width);
        Assert.Equal(200, image.Height);
        Assert.Equal(3, image.Bands);
        Assert.Equal(VipsBandFormat.UChar, image.BandFormat);
        Assert.Equal(VipsInterpretation.RGB, image.Interpretation);
    }

    [Fact]
    public async Task LoadHeaderAsync_PngWithPhys_ReturnsCorrectResolution()
    {
        // Arrange: PNG with pHYs chunk (1000 pixels per meter = 1.0 pixels per mm)
        byte[] pngBytes = {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x64, 0x00, 0x00, 0x00, 0x64, 0x08, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x09, 0x70, 0x48, 0x59, 0x73, 0x00, 0x00, 0x03, 0xE8, 0x00, 0x00, 0x03, 0xE8, 0x01, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82
        };

        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(pngBytes);
        await pipe.Writer.CompleteAsync();

        await using var source = new PipeVipsSource(pipe.Reader);

        // Act
        var image = await VipsPngLoader.LoadHeaderAsync(source);

        // Assert
        Assert.NotNull(image);
        Assert.Equal(1.0, image.XRes);
        Assert.Equal(1.0, image.YRes);
    }

    [Fact]
    public async Task IsPngAsync_ValidPng_ReturnsTrue()
    {
        // Arrange
        byte[] pngBytes = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(pngBytes);
        await pipe.Writer.CompleteAsync();

        await using var source = new PipeVipsSource(pipe.Reader);

        // Act
        bool isPng = await VipsPngLoader.IsPngAsync(source);

        // Assert
        Assert.True(isPng);
    }
}
