using System;
using System.IO.Pipelines;
using System.Threading.Tasks;
using Xunit;

namespace CosmoImage.Tests;

public class TiffLoaderTests
{
    [Fact]
    public async Task LoadHeaderAsync_ValidTiff_ReturnsCorrectMetadata()
    {
        // Arrange: Minimal valid TIFF
        // Tags: Width(256), Height(257), BPS(258), Compression(259), Photometric(262), StripOffsets(273), SPP(277), RowsPerStrip(278), StripByteCounts(279)
        byte[] tiffBytes = {
            0x49, 0x49, 0x2A, 0x00, // II*
            0x08, 0x00, 0x00, 0x00, // IFD offset 8
            0x09, 0x00, // Entry count 9
            0x00, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, 0x80, 0x00, 0x00, 0x00, // Width 128
            0x01, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, // Height 256
            0x02, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, // BPS 8
            0x03, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, // Compression None
            0x06, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, // Photometric RGB
            0x11, 0x01, 0x04, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // StripOffsets (dummy)
            0x15, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, // SPP 3
            0x16, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, // RowsPerStrip 256
            0x17, 0x01, 0x04, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x80, 0x00, 0x00, // StripByteCounts (128*256*3?) No, for dummy we use a value
            0x00, 0x00, 0x00, 0x00 // Next IFD offset 0
        };

        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(tiffBytes);
        await pipe.Writer.CompleteAsync();

        await using var source = new PipeVipsSource(pipe.Reader);

        // Act
        var image = await VipsTiffLoader.LoadHeaderAsync(source);

        // Assert
        Assert.NotNull(image);
        Assert.Equal(128, image.Width);
        Assert.Equal(256, image.Height);
        Assert.Equal(3, image.Bands);
        Assert.Equal(VipsBandFormat.UChar, image.BandFormat);
        Assert.Equal(VipsInterpretation.RGB, image.Interpretation);
    }

    [Fact]
    public async Task IsTiffAsync_ValidTiff_ReturnsTrue()
    {
        // Arrange
        byte[] tiffBytes = { 0x49, 0x49, 0x2A, 0x00 };
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(tiffBytes);
        await pipe.Writer.CompleteAsync();

        await using var source = new PipeVipsSource(pipe.Reader);

        // Act
        bool isTiff = await VipsTiffLoader.IsTiffAsync(source);

        // Assert
        Assert.True(isTiff);
    }
}
