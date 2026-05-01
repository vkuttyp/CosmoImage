using System;
using System.IO.Pipelines;
using System.Threading.Tasks;
using Xunit;

namespace CosmoImage.Tests;

public class BmpLoaderTests
{
    [Fact]
    public async Task LoadHeaderAsync_ValidBmp_ReturnsCorrectMetadata()
    {
        // Arrange: Minimal valid BMP (BITMAPINFOHEADER, 24-bit RGB)
        // 100x200
        byte[] bmpBytes = new byte[14 + 40];
        // File Header
        bmpBytes[0] = (byte)'B'; bmpBytes[1] = (byte)'M';
        BitConverter.TryWriteBytes(bmpBytes.AsSpan(2, 4), (uint)(14 + 40)); // Size
        BitConverter.TryWriteBytes(bmpBytes.AsSpan(10, 4), (uint)(14 + 40)); // Offset

        // DIB Header (BITMAPINFOHEADER)
        BitConverter.TryWriteBytes(bmpBytes.AsSpan(14, 4), (uint)40); // DIB Size
        BitConverter.TryWriteBytes(bmpBytes.AsSpan(18, 4), (int)100); // Width
        BitConverter.TryWriteBytes(bmpBytes.AsSpan(22, 4), (int)200); // Height
        BitConverter.TryWriteBytes(bmpBytes.AsSpan(26, 2), (ushort)1); // Planes
        BitConverter.TryWriteBytes(bmpBytes.AsSpan(28, 2), (ushort)24); // BPP

        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(bmpBytes);
        await pipe.Writer.CompleteAsync();

        await using var source = new PipeVipsSource(pipe.Reader);

        // Act
        var image = await VipsBmpLoader.LoadHeaderAsync(source);

        // Assert
        Assert.NotNull(image);
        Assert.Equal(100, image.Width);
        Assert.Equal(200, image.Height);
        Assert.Equal(3, image.Bands);
    }

    [Fact]
    public async Task IsBmpAsync_ValidBmp_ReturnsTrue()
    {
        // Arrange
        byte[] bmpBytes = { (byte)'B', (byte)'M' };
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(bmpBytes);
        await pipe.Writer.CompleteAsync();

        await using var source = new PipeVipsSource(pipe.Reader);

        // Act
        bool isBmp = await VipsBmpLoader.IsBmpAsync(source);

        // Assert
        Assert.True(isBmp);
    }
}
