using System.IO.Pipelines;
using System.Text;

namespace CosmoImage.Tests;

public class PdfLoaderTests
{
    [Fact]
    public async Task IsPdfAsync_ValidPdfHeader_ReturnsTrue()
    {
        byte[] pdfBytes = Encoding.ASCII.GetBytes("%PDF-1.7\n");
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(pdfBytes);
        await pipe.Writer.CompleteAsync();

        await using var source = new PipeVipsSource(pipe.Reader);

        bool isPdf = await VipsPdfLoader.IsPdfAsync(source);

        Assert.True(isPdf);
    }

    [Fact]
    public async Task LoadAsync_RendersSinglePage()
    {
        var pdfBytes = BuildPdf((20, 12, "1 0 0 rg 2 2 8 6 re f"));
        await using var source = CreateSource(pdfBytes);

        using var image = await VipsPdfLoader.LoadAsync(source, dpi: 72);

        Assert.NotNull(image);
        Assert.Equal(20, image!.Width);
        Assert.Equal(12, image.Height);
        Assert.Equal(4, image.Bands);
        Assert.Equal("1", image.Metadata["pdf-n-pages"]);
        Assert.True(ContainsPixel(image, p => p.R > 200 && p.G < 50 && p.B < 50 && p.A == 255));
    }

    [Fact]
    public async Task LoadAsync_LoadsMultiplePagesIntoTallBuffer()
    {
        var pdfBytes = BuildPdf(
            (10, 10, "1 0 0 rg 1 1 8 8 re f"),
            (10, 10, "0 1 0 rg 1 1 8 8 re f"));
        await using var source = CreateSource(pdfBytes);

        using var image = await VipsPdfLoader.LoadAsync(source, page: 0, n: -1, dpi: 72);

        Assert.NotNull(image);
        Assert.Equal(10, image!.Width);
        Assert.Equal(20, image.Height);
        Assert.Equal("2", image.Metadata["n-pages"]);
        Assert.Equal("10", image.Metadata["page-height"]);
        Assert.Equal("2", image.Metadata["pdf-n-pages"]);
        Assert.True(ContainsPixel(image, p => p.Y < 10 && p.R > 200 && p.G < 50 && p.B < 50));
        Assert.True(ContainsPixel(image, p => p.Y >= 10 && p.G > 200 && p.R < 50 && p.B < 50));
    }

    [Fact]
    public async Task LoadAsync_RendersDctDecodeJpegImageXObject()
    {
        var pdfBytes = BuildPdfWithPlacedJpegImage();
        await using var source = CreateSource(pdfBytes);

        using var image = await VipsPdfLoader.LoadAsync(source, dpi: 72);

        Assert.NotNull(image);
        Assert.Equal(40, image!.Width);
        Assert.Equal(30, image.Height);
        Assert.True(ContainsPixel(image, p => p.B > 200 && p.R < 100 && p.G < 120 && p.A == 255));
        Assert.True(ContainsPixel(image, p => p.R > 130 && p.G < 80 && p.B < 60 && p.A == 255));
    }

    [Fact]
    public async Task LoadAsync_FallsBackForUnsupportedPdfFeatures()
    {
        var pdfBytes = BuildPdfWithPatternResource();
        await using var source = CreateSource(pdfBytes);

        var ex = await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            using var _ = await VipsPdfLoader.LoadAsync(source, dpi: 72);
        });

        Assert.Contains("pattern resources", ex.Message);
    }

    private static PipeVipsSource CreateSource(byte[] bytes)
    {
        var pipe = new Pipe();
        pipe.Writer.WriteAsync(bytes).AsTask().GetAwaiter().GetResult();
        pipe.Writer.CompleteAsync().AsTask().GetAwaiter().GetResult();
        return new PipeVipsSource(pipe.Reader);
    }

    private static bool ContainsPixel(VipsImage image, Func<(int X, int Y, byte R, byte G, byte B, byte A), bool> predicate)
    {
        using var region = new VipsRegion(image);
        region.Prepare(new VipsRect(0, 0, image.Width, image.Height));
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                var pixel = region.GetAddress(x, y);
                if (predicate((x, y, pixel[0], pixel[1], pixel[2], pixel[3])))
                    return true;
            }
        }

        return false;
    }

    private static byte[] BuildPdf(params (int Width, int Height, string Content)[] pages)
    {
        using var ms = new MemoryStream();
        var offsets = new Dictionary<int, long>();
        int maxObjectNumber = 2 + (pages.Length * 2);

        WriteAscii(ms, "%PDF-1.4\n");

        var pageObjectNumbers = new int[pages.Length];
        var contentObjectNumbers = new int[pages.Length];
        for (int i = 0; i < pages.Length; i++)
        {
            pageObjectNumbers[i] = 3 + (i * 2);
            contentObjectNumbers[i] = 4 + (i * 2);
        }

        WriteObject(ms, offsets, 1, "<< /Type /Catalog /Pages 2 0 R >>");
        WriteObject(ms, offsets, 2, $"<< /Type /Pages /Count {pages.Length} /Kids [{string.Join(' ', pageObjectNumbers.Select(n => $"{n} 0 R"))}] >>");

        for (int i = 0; i < pages.Length; i++)
        {
            var page = pages[i];
            WriteObject(ms, offsets, pageObjectNumbers[i],
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {page.Width} {page.Height}] /Contents {contentObjectNumbers[i]} 0 R >>");
            WriteStreamObject(ms, offsets, contentObjectNumbers[i], Encoding.ASCII.GetBytes(page.Content));
        }

        long xrefOffset = ms.Position;
        WriteAscii(ms, $"xref\n0 {maxObjectNumber + 1}\n");
        WriteAscii(ms, "0000000000 65535 f \n");
        for (int objectNumber = 1; objectNumber <= maxObjectNumber; objectNumber++)
            WriteAscii(ms, $"{offsets[objectNumber]:0000000000} 00000 n \n");

        WriteAscii(ms, $"trailer\n<< /Size {maxObjectNumber + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF");
        return ms.ToArray();
    }

    private static byte[] BuildPdfWithPlacedJpegImage()
    {
        using var ms = new MemoryStream();
        var offsets = new Dictionary<int, long>();
        byte[] jpeg = Convert.FromBase64String("/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQH/2wBDAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQH/wAARCAABAAgDAREAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDwf/gnn/zV7/uQP/d1r/AP6Sv/ADRf/dx/+8E/UP8ATUP+dav/AHuP/wDCrH//2Q==");
        const int maxObjectNumber = 5;

        WriteAscii(ms, "%PDF-1.4\n");
        WriteObject(ms, offsets, 1, "<< /Type /Catalog /Pages 2 0 R >>");
        WriteObject(ms, offsets, 2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>");
        WriteObject(ms, offsets, 3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 40 30] /Contents 4 0 R /Resources << /XObject << /Im0 5 0 R >> >> >>");
        WriteStreamObject(ms, offsets, 4, Encoding.ASCII.GetBytes("q 20 0 0 10 10 5 cm /Im0 Do Q"));
        WriteStreamObject(ms, offsets, 5, "<< /Type /XObject /Subtype /Image /Width 8 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode >>", jpeg);

        long xrefOffset = ms.Position;
        WriteAscii(ms, $"xref\n0 {maxObjectNumber + 1}\n");
        WriteAscii(ms, "0000000000 65535 f \n");
        for (int objectNumber = 1; objectNumber <= maxObjectNumber; objectNumber++)
            WriteAscii(ms, $"{offsets[objectNumber]:0000000000} 00000 n \n");

        WriteAscii(ms, $"trailer\n<< /Size {maxObjectNumber + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF");
        return ms.ToArray();
    }

    private static byte[] BuildPdfWithPatternResource()
    {
        using var ms = new MemoryStream();
        var offsets = new Dictionary<int, long>();
        const int maxObjectNumber = 4;

        WriteAscii(ms, "%PDF-1.4\n");
        WriteObject(ms, offsets, 1, "<< /Type /Catalog /Pages 2 0 R >>");
        WriteObject(ms, offsets, 2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>");
        WriteObject(ms, offsets, 3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 20 12] /Contents 4 0 R /Resources << /Pattern << >> >> >>");
        WriteStreamObject(ms, offsets, 4, Encoding.ASCII.GetBytes("1 0 0 rg 2 2 8 6 re f"));

        long xrefOffset = ms.Position;
        WriteAscii(ms, $"xref\n0 {maxObjectNumber + 1}\n");
        WriteAscii(ms, "0000000000 65535 f \n");
        for (int objectNumber = 1; objectNumber <= maxObjectNumber; objectNumber++)
            WriteAscii(ms, $"{offsets[objectNumber]:0000000000} 00000 n \n");

        WriteAscii(ms, $"trailer\n<< /Size {maxObjectNumber + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF");
        return ms.ToArray();
    }

    private static void WriteObject(MemoryStream ms, Dictionary<int, long> offsets, int objectNumber, string body)
    {
        offsets[objectNumber] = ms.Position;
        WriteAscii(ms, $"{objectNumber} 0 obj\n{body}\nendobj\n");
    }

    private static void WriteStreamObject(MemoryStream ms, Dictionary<int, long> offsets, int objectNumber, byte[] content)
    {
        offsets[objectNumber] = ms.Position;
        WriteAscii(ms, $"{objectNumber} 0 obj\n<< /Length {content.Length} >>\nstream\n");
        ms.Write(content, 0, content.Length);
        WriteAscii(ms, "\nendstream\nendobj\n");
    }

    private static void WriteStreamObject(MemoryStream ms, Dictionary<int, long> offsets, int objectNumber, string dictionaryBody, byte[] content)
    {
        offsets[objectNumber] = ms.Position;
        WriteAscii(ms, $"{objectNumber} 0 obj\n{dictionaryBody[..^2]} /Length {content.Length} >>\nstream\n");
        ms.Write(content, 0, content.Length);
        WriteAscii(ms, "\nendstream\nendobj\n");
    }

    private static void WriteAscii(Stream stream, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        stream.Write(bytes, 0, bytes.Length);
    }
}
