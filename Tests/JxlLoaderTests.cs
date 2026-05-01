using System;
using System.IO.Pipelines;
using System.Threading.Tasks;
using Xunit;

namespace CosmoImage.Tests;

public class JxlLoaderTests
{
    [Fact]
    public async Task IsJxlAsync_Codestream_ReturnsTrue()
    {
        // Arrange
        byte[] jxlBytes = { 0xFF, 0x0A, 0x00, 0x00 };
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(jxlBytes);
        await pipe.Writer.CompleteAsync();

        await using var source = new PipeVipsSource(pipe.Reader);

        // Act
        bool isJxl = await VipsJxlLoader.IsJxlAsync(source);

        // Assert
        Assert.True(isJxl);
    }

    [Fact]
    public async Task IsJxlAsync_Container_ReturnsTrue()
    {
        // Arrange
        byte[] jxlBytes = { 0x00, 0x00, 0x00, 0x0C, 0x4A, 0x58, 0x4C, 0x20, 0x0D, 0x0A, 0x87, 0x0A };
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(jxlBytes);
        await pipe.Writer.CompleteAsync();

        await using var source = new PipeVipsSource(pipe.Reader);

        // Act
        bool isJxl = await VipsJxlLoader.IsJxlAsync(source);

        // Assert
        Assert.True(isJxl);
    }
}
