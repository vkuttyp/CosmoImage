using System;
using System.IO.Pipelines;
using System.Threading.Tasks;
using Xunit;

namespace CosmoImage.Tests;

public class SvgLoaderTests
{
    [Fact]
    public async Task LoadAsync_ValidSvg_RendersImage()
    {
        // Arrange: Minimal valid SVG
        string svg = "<svg width=\"100\" height=\"100\"><rect width=\"100\" height=\"100\" fill=\"red\"/></svg>";
        byte[] svgBytes = System.Text.Encoding.ASCII.GetBytes(svg);

        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(svgBytes);
        await pipe.Writer.CompleteAsync();

        await using var source = new PipeVipsSource(pipe.Reader);

        // Act
        var image = await VipsSvgLoader.LoadAsync(source);

        // Assert
        Assert.NotNull(image);
        Assert.Equal(100, image.Width);
        Assert.Equal(100, image.Height);
        Assert.Equal(4, image.Bands); // RGBA

        using var outRegion = new VipsRegion(image);
        outRegion.Prepare(new VipsRect(0, 0, 1, 1));
        var pixel = outRegion.GetAddress(0, 0);
        // Pure red should be (255, 0, 0, 255)
        Assert.Equal(255, pixel[0]);
        Assert.Equal(0, pixel[1]);
        Assert.Equal(0, pixel[2]);
        Assert.Equal(255, pixel[3]);
    }

    [Fact]
    public async Task IsSvgAsync_ValidSvg_ReturnsTrue()
    {
        // Arrange
        byte[] svgBytes = System.Text.Encoding.ASCII.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(svgBytes);
        await pipe.Writer.CompleteAsync();

        await using var source = new PipeVipsSource(pipe.Reader);

        // Act
        bool isSvg = await VipsSvgLoader.IsSvgAsync(source);

        // Assert
        Assert.True(isSvg);
    }
}
