using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CosmoImage.Loaders;

public static class VipsGifLoader
{
    public static async ValueTask<bool> IsGifAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        var sniff = await source.SniffAsync(4, cancellationToken);
        if (sniff.Length < 4) return false;

        var span = sniff.Span;
        return span[0] == 'G' && span[1] == 'I' && span[2] == 'F' && span[3] == '8';
    }

    public static async ValueTask<VipsImage?> LoadHeaderAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        // For GIF, we use LoadAsync for header as well so we accurately get page counts.
        return await LoadAsync(source, cancellationToken);
    }

    public static async ValueTask<VipsImage?> LoadAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        if (!await IsGifAsync(source, cancellationToken))
            return null;

        var ms = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            int readCount = await source.ReadAsync(buffer, cancellationToken);
            if (readCount == 0) break;
            ms.Write(buffer, 0, readCount);
        }

        var imageBytes = ms.ToArray();

        var pure = PureGifDecoder.TryDecode(imageBytes);
        if (pure == null)
            return null;

        int totalH = pure.CanvasHeight * pure.FrameCount;
        const int bands = 4;
        var image = new VipsImage
        {
            Width = pure.CanvasWidth,
            Height = totalH,
            Bands = bands,
            BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            Coding = VipsCoding.None,
            XRes = 1.0,
            YRes = 1.0,
            PixelsLazy = new Lazy<byte[]>(() => pure.Pixels),
        };
        image.Metadata["n-pages"] = pure.FrameCount.ToString();
        image.Metadata["page-height"] = pure.CanvasHeight.ToString();
        image.Metadata["animation-delays"] = string.Join(",", pure.DelaysCentiseconds);
        if (pure.Comment != null) image.Metadata["comment"] = pure.Comment;
        return image;
    }

    /// <summary>Streaming GIF load shares the same eager-buffered pure decoder as <see cref="LoadAsync"/>.</summary>
    public static ValueTask<VipsImage?> LoadStreamingAsync(IVipsSource source, CancellationToken cancellationToken = default)
        => LoadAsync(source, cancellationToken);
}
