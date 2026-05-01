using System;
using System.IO.Pipelines;
using System.Threading.Tasks;
using Xunit;

namespace CosmoImage.Tests;

public class PdfLoaderTests
{
    [Fact]
    public async Task IsPdfAsync_ValidPdfHeader_ReturnsTrue()
    {
        // Arrange
        byte[] pdfBytes = System.Text.Encoding.ASCII.GetBytes("%PDF-1.7\n");
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(pdfBytes);
        await pipe.Writer.CompleteAsync();

        await using var source = new PipeVipsSource(pipe.Reader);

        // Act
        bool isPdf = await VipsPdfLoader.IsPdfAsync(source);

        // Assert
        Assert.True(isPdf);
    }
}
