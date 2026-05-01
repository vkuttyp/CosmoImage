using System;
using System.IO.Pipelines;
using System.Threading.Tasks;
using Xunit;

namespace CosmoImage.Tests;

public class JpegLoaderTests
{
    [Fact]
    public async Task LoadHeaderAsync_ValidJpeg_ReturnsCorrectMetadata()
    {
        // Arrange: Minimal valid JPEG header (SOF0)
        // SOI + APP0 + SOF0 (64x48, 3 bands) + EOI
        byte[] jpegBytes = {
            0xFF, 0xD8, // SOI
            0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x01, 0x00, 0x60, 0x00, 0x60, 0x00, 0x00, // APP0
            0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x40, 0x00, 0x30, 0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01, // SOF0
            0xFF, 0xD9 // EOI
        };

        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(jpegBytes);
        await pipe.Writer.CompleteAsync();

        await using var source = new PipeVipsSource(pipe.Reader);

        // Act
        var image = await VipsJpegLoader.LoadHeaderAsync(source);

        // Assert
        Assert.NotNull(image);
        Assert.Equal(48, image.Width);
        Assert.Equal(64, image.Height);
        Assert.Equal(3, image.Bands);
        Assert.Equal(VipsBandFormat.UChar, image.BandFormat);
        Assert.Equal(VipsInterpretation.RGB, image.Interpretation);
    }

    [Fact]
    public async Task IsJpegAsync_ValidJpeg_ReturnsTrue()
    {
        // Arrange
        byte[] jpegBytes = { 0xFF, 0xD8, 0xFF, 0xE0 };
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(jpegBytes);
        await pipe.Writer.CompleteAsync();

        await using var source = new PipeVipsSource(pipe.Reader);

        // Act
        bool isJpeg = await VipsJpegLoader.IsJpegAsync(source);

        // Assert
        Assert.True(isJpeg);
    }
}
