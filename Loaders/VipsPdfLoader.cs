using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Docnet.Core;
using Docnet.Core.Models;
using Docnet.Core.Readers;

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

        var ms = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            int readCount = await source.ReadAsync(buffer, cancellationToken);
            if (readCount == 0) break;
            ms.Write(buffer, 0, readCount);
        }

        var pdfBytes = ms.ToArray();
        double scalingFactor = dpi / 72.0;

        int totalPages, width, pageHeight;
        using (var docReader = DocLib.Instance.GetDocReader(pdfBytes, new PageDimensions(scalingFactor)))
        {
            totalPages = docReader.GetPageCount();
            if (page < 0 || page >= totalPages) return null;
            using var firstReader = docReader.GetPageReader(page);
            width = firstReader.GetPageWidth();
            pageHeight = firstReader.GetPageHeight();
        }

        int pagesToLoad = n switch
        {
            -1 => totalPages - page,
            > 0 => Math.Min(n, totalPages - page),
            _ => 1,
        };
        if (pagesToLoad <= 0) return null;

        int totalHeight = pageHeight * pagesToLoad;
        int firstPage = page;
        double capturedScale = scalingFactor;
        const int bands = 4;

        var image = new VipsImage
        {
            Width = width,
            Height = totalHeight,
            Bands = bands, // PDFium renders to BGRA → we'll swap to RGBA
            BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            Coding = VipsCoding.None,
            XRes = dpi / 25.4,
            YRes = dpi / 25.4,
            PixelsLazy = new Lazy<byte[]>(() =>
            {
                int frameSize = width * pageHeight * bands;
                var buf = new byte[frameSize * pagesToLoad];

                using var docReader = DocLib.Instance.GetDocReader(pdfBytes, new PageDimensions(capturedScale));
                for (int p = 0; p < pagesToLoad; p++)
                {
                    using var pageReader = docReader.GetPageReader(firstPage + p);
                    var bgra = pageReader.GetImage();
                    int pageBase = p * frameSize;

                    // BGRA → RGBA per pixel.
                    for (int i = 0; i < frameSize; i += 4)
                    {
                        buf[pageBase + i + 0] = bgra[i + 2];
                        buf[pageBase + i + 1] = bgra[i + 1];
                        buf[pageBase + i + 2] = bgra[i + 0];
                        buf[pageBase + i + 3] = bgra[i + 3];
                    }
                }
                return buf;
            })
        };

        // Loaded-image multi-frame metadata (matches GIF/WebP/TIFF convention).
        if (pagesToLoad > 1)
        {
            image.Metadata["n-pages"] = pagesToLoad.ToString();
            image.Metadata["page-height"] = pageHeight.ToString();
        }
        // PDF-specific: total page count in the source document, regardless of
        // how many we actually loaded. Useful for callers that want to iterate.
        image.Metadata["pdf-n-pages"] = totalPages.ToString();

        return image;
    }
}
