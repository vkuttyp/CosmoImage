using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CosmoImagePdf.Shared.Pdf;

namespace CosmoImage.Loaders;

public static class VipsPdfLoader
{
    public static async ValueTask<bool> IsPdfAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        var sniff = await source.SniffAsync(4, cancellationToken);
        if (sniff.Length < 4) return false;

        var span = sniff.Span;
        return span[0] == (byte)'%' && span[1] == (byte)'P' && span[2] == (byte)'D' && span[3] == (byte)'F';
    }

    /// <summary>
    /// Render PDF pages to pixels.
    /// </summary>
    /// <param name="page">First page to render (0-based).</param>
    /// <param name="n">Page count: 1 (default) = single page, -1 = all from
    /// <paramref name="page"/> to end, N = exactly N pages. Multi-page output
    /// uses the same tall-buffer layout as the other multi-frame loaders
    /// (n-pages, page-height in <see cref="VipsImage.Metadata"/>) so it can be
    /// re-saved as multi-page TIFF, animated GIF, etc. Default is 1 to avoid
    /// unintentionally rendering 100-page documents into RAM.</param>
    /// <param name="dpi">Render resolution. Pixel dimensions scale linearly.</param>
    public static async ValueTask<VipsImage?> LoadAsync(IVipsSource source, int page = 0, int n = 1, double dpi = 72, CancellationToken cancellationToken = default)
    {
        if (!await IsPdfAsync(source, cancellationToken))
            return null;

        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            int readCount = await source.ReadAsync(buffer, cancellationToken);
            if (readCount == 0) break;
            ms.Write(buffer, 0, readCount);
        }

        var pdfBytes = ms.ToArray();
        double scalingFactor = dpi / 72.0;
        var rasterizer = new PdfDocumentRasterizer(pdfBytes);
        int totalPages = rasterizer.PageCount;
        if (page < 0 || page >= totalPages) return null;

        int pagesToLoad = n switch
        {
            -1 => totalPages - page,
            > 0 => Math.Min(n, totalPages - page),
            _ => 1,
        };
        if (pagesToLoad <= 0) return null;

        if (!rasterizer.Support.IsSupported)
        {
            throw new NotSupportedException($"pdf: shared rasterizer cannot safely render this document ({rasterizer.Support.Summary})");
        }

        var firstPage = rasterizer.RenderPage(page, scalingFactor);
        int width = firstPage.Width;
        int pageHeight = firstPage.Height;
        int totalHeight = pageHeight * pagesToLoad;
        int firstPageIndex = page;
        const int bands = 4;

        var image = new VipsImage
        {
            Width = width,
            Height = totalHeight,
            Bands = bands,
            BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            Coding = VipsCoding.None,
            XRes = dpi / 25.4,
            YRes = dpi / 25.4,
            PixelsLazy = new Lazy<byte[]>(() =>
            {
                int frameSize = width * pageHeight * bands;
                var pixels = new byte[frameSize * pagesToLoad];

                for (int p = 0; p < pagesToLoad; p++)
                {
                    var renderedPage = p == 0
                        ? firstPage
                        : rasterizer.RenderPage(firstPageIndex + p, scalingFactor);
                    WritePageFrame(pixels, p * frameSize, renderedPage, width, pageHeight);
                }

                return pixels;
            })
        };

        if (pagesToLoad > 1)
        {
            image.Metadata["n-pages"] = pagesToLoad.ToString();
            image.Metadata["page-height"] = pageHeight.ToString();
        }
        image.Metadata["pdf-n-pages"] = totalPages.ToString();
        image.Metadata["pdf-renderer"] = "shared";

        return image;
    }

    private static void WritePageFrame(byte[] destination, int destinationOffset, PdfRasterPage page, int targetWidth, int targetHeight)
    {
        int frameLength = targetWidth * targetHeight * 4;
        for (int i = destinationOffset; i < destinationOffset + frameLength; i += 4)
        {
            destination[i + 0] = 255;
            destination[i + 1] = 255;
            destination[i + 2] = 255;
            destination[i + 3] = 255;
        }

        int copyWidth = Math.Min(targetWidth, page.Width);
        int copyHeight = Math.Min(targetHeight, page.Height);
        for (int y = 0; y < copyHeight; y++)
        {
            int sourceRow = y * page.Width * 3;
            int destinationRow = destinationOffset + (y * targetWidth * 4);
            for (int x = 0; x < copyWidth; x++)
            {
                int sourceIndex = sourceRow + (x * 3);
                int destinationIndex = destinationRow + (x * 4);
                destination[destinationIndex + 0] = page.RgbPixels[sourceIndex + 0];
                destination[destinationIndex + 1] = page.RgbPixels[sourceIndex + 1];
                destination[destinationIndex + 2] = page.RgbPixels[sourceIndex + 2];
                destination[destinationIndex + 3] = 255;
            }
        }
    }
}
