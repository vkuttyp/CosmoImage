using System;
using System.IO.Pipelines;
using System.Threading.Tasks;
using Xunit;

namespace CosmoImage.Tests;

public class Jp2kLoaderTests
{
    [Fact]
    public async Task LoadHeaderAsync_ValidJp2_ReturnsCorrectMetadata()
    {
        // Arrange: Minimal valid JP2 container with ihdr box
        // Signature (12) + ftyp (12) + jp2h (8) + ihdr (8 + 14)
        byte[] jp2Bytes = {
            0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A, // Signature
            0x00, 0x00, 0x00, 0x0C, 0x66, 0x74, 0x79, 0x70, 0x6A, 0x70, 0x32, 0x20, // ftyp
            0x00, 0x00, 0x00, 0x1E, 0x6A, 0x70, 0x32, 0x68, // jp2h (Length 30)
            0x00, 0x00, 0x00, 0x16, 0x69, 0x68, 0x64, 0x72, // ihdr (Length 22)
            0x00, 0x00, 0x02, 0x00, // Height 512
            0x00, 0x00, 0x01, 0x00, // Width 256
            0x00, 0x03, // NC 3
            0x07, // BPC (8-bit)
            0x07, 0x01, 0x00 // C, UnkC, IPR
        };

        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(jp2Bytes);
        await pipe.Writer.CompleteAsync();

        await using var source = new PipeVipsSource(pipe.Reader);

        // Act
        var image = await VipsJp2kLoader.LoadHeaderAsync(source);

        // Assert
        Assert.NotNull(image);
        Assert.Equal(256, image.Width);
        Assert.Equal(512, image.Height);
        Assert.Equal(3, image.Bands);
        Assert.Equal(VipsBandFormat.UChar, image.BandFormat);
        Assert.Equal(VipsInterpretation.RGB, image.Interpretation);
    }

    [Fact]
    public async Task LoadHeaderAsync_ValidJ2k_ReturnsCorrectMetadata()
    {
        // Arrange: Minimal valid J2K codestream (SOC + SIZ)
        byte[] j2kBytes = {
            0xFF, 0x4F, // SOC
            0xFF, 0x51, 0x00, 0x2F, // SIZ (Length 47)
            0x00, 0x00, // Capabilities
            0x00, 0x00, 0x01, 0x00, // Width 256
            0x00, 0x00, 0x02, 0x00, // Height 512
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // Offset X, Offset Y
            0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x02, 0x00, // Tile Width, Tile Height
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // Tile Offset X, Tile Offset Y
            0x00, 0x03, // NC 3
            0x07, 0x01, 0x01, // Component 0: BPC, DX, DY
            0x07, 0x01, 0x01, // Component 1
            0x07, 0x01, 0x01  // Component 2
        };

        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(j2kBytes);
        await pipe.Writer.CompleteAsync();

        await using var source = new PipeVipsSource(pipe.Reader);

        // Act
        var image = await VipsJp2kLoader.LoadHeaderAsync(source);

        // Assert
        Assert.NotNull(image);
        Assert.Equal(256, image.Width);
        Assert.Equal(512, image.Height);
        Assert.Equal(3, image.Bands);
    }

    [Fact]
    public async Task IsJp2kAsync_ValidJp2_ReturnsTrue()
    {
        // Arrange
        byte[] jp2Bytes = { 0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A };
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(jp2Bytes);
        await pipe.Writer.CompleteAsync();

        await using var source = new PipeVipsSource(pipe.Reader);

        // Act
        bool isJp2k = await VipsJp2kLoader.IsJp2kAsync(source);

        // Assert
        Assert.True(isJp2k);
    }
}
