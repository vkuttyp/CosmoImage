using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ImageMagick;

namespace CosmoImage.Loaders;

public static class VipsSvgLoader
{
    public static async ValueTask<bool> IsSvgAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        var sniff = await source.SniffAsync(1024, cancellationToken);
        if (sniff.Length < 10) return false;

        string content = System.Text.Encoding.ASCII.GetString(sniff.Span);
        return content.Contains("<svg", StringComparison.OrdinalIgnoreCase);
    }

    public static async ValueTask<VipsImage?> LoadAsync(IVipsSource source, int width = 0, int height = 0, CancellationToken cancellationToken = default)
    {
        if (!await IsSvgAsync(source, cancellationToken))
            return null;

        var ms = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            ms.Write(buffer, 0, read);
        }

        var svgBytes = ms.ToArray();
        
        var readSettings = new MagickReadSettings
        {
            Format = MagickFormat.Svg
        };

        if (width > 0 && height > 0)
        {
            readSettings.Width = (uint)width;
            readSettings.Height = (uint)height;
        }

        // Render once eagerly to extract dimensions for the header. We reuse
        // the same MagickReadSettings inside the lazy materializer so the
        // re-render uses identical parameters.
        int finalWidth, finalHeight;
        using (var probe = new MagickImage(svgBytes, readSettings))
        {
            finalWidth = (int)probe.Width;
            finalHeight = (int)probe.Height;
        }
        const int bands = 4; // SVG always rendered with Alpha

        var image = new VipsImage
        {
            Width = finalWidth,
            Height = finalHeight,
            Bands = bands,
            BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            Coding = VipsCoding.None,
            XRes = 1.0,
            YRes = 1.0,
            PixelsLazy = new Lazy<byte[]>(() =>
            {
                using var magickImage = new MagickImage(svgBytes, readSettings);
                using var pixels = magickImage.GetPixels();
                return pixels.ToByteArray(0, 0, (uint)finalWidth, (uint)finalHeight, "RGBA")
                    ?? throw new InvalidOperationException("SVG rasterization returned null");
            })
        };

        return image;
    }

    /// <summary>
    /// Streaming SVG load: rasterizes once via Magick.NET reading directly
    /// from the source, returns a memory-backed VipsImage. The encoded SVG
    /// text is never materialized as a separate byte[].
    /// </summary>
    public static async ValueTask<VipsImage?> LoadStreamingAsync(IVipsSource source, int width = 0, int height = 0, CancellationToken cancellationToken = default)
    {
        if (!await IsSvgAsync(source, cancellationToken)) return null;
        await Task.Yield();

        var readSettings = new MagickReadSettings { Format = MagickFormat.Svg };
        if (width > 0 && height > 0)
        {
            readSettings.Width = (uint)width;
            readSettings.Height = (uint)height;
        }

        try
        {
            using var stream = source.AsStream();
            using var img = new MagickImage(stream, readSettings);

            int finalWidth = (int)img.Width;
            int finalHeight = (int)img.Height;
            const int bands = 4;

            using var pixels = img.GetPixels();
            var buf = pixels.ToByteArray(0, 0, (uint)finalWidth, (uint)finalHeight, "RGBA")
                ?? throw new InvalidOperationException("SVG streaming rasterization returned null");

            return new VipsImage
            {
                Width = finalWidth,
                Height = finalHeight,
                Bands = bands,
                BandFormat = VipsBandFormat.UChar,
                Interpretation = VipsInterpretation.RGB,
                Coding = VipsCoding.None,
                XRes = 1.0,
                YRes = 1.0,
                PixelsLazy = new Lazy<byte[]>(() => buf),
            };
        }
        catch
        {
            return null;
        }
    }
}
