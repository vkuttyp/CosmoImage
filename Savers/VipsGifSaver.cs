using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using ImageMagick;

namespace CosmoImage.Savers;

/// <summary>
/// GIF writer. Handles single-frame and animated output. Animation comes from
/// the multi-frame convention used by <see cref="VipsGifLoader"/>: image height
/// is N × page-height, with <c>n-pages</c>, <c>page-height</c>, and
/// <c>animation-delays</c> in <see cref="VipsImage.Metadata"/>. The buffer is
/// split back into per-frame pixel data and assembled into a
/// <see cref="MagickImageCollection"/>.
/// </summary>
public static class VipsGifSaver
{
    public static async Task SaveAsync(VipsImage image, PipeWriter writer, CancellationToken cancellationToken = default)
    {
        int width = image.Width;
        int height = image.Height;
        int bands = image.Bands;

        if (bands != 1 && bands != 3 && bands != 4)
            throw new NotSupportedException($"GIF save needs 1, 3, or 4 bands; got {bands}");

        // Detect multi-frame layout. Default to single-frame.
        int nPages = 1;
        int pageHeight = height;
        if (image.Metadata.TryGetValue("n-pages", out var npStr) &&
            int.TryParse(npStr, out int nP) && nP > 0 &&
            image.Metadata.TryGetValue("page-height", out var phStr) &&
            int.TryParse(phStr, out int ph) && ph > 0 &&
            nP * ph == height)
        {
            nPages = nP;
            pageHeight = ph;
        }

        // Per-frame animation delay (1/100 sec units, matching GIF GCE field).
        // Default 10 = 100ms per frame when nothing is specified.
        var delays = new uint[nPages];
        Array.Fill(delays, 10u);
        if (image.Metadata.TryGetValue("animation-delays", out var dStr))
        {
            var parts = dStr.Split(',');
            for (int i = 0; i < Math.Min(parts.Length, nPages); i++)
                if (uint.TryParse(parts[i], out var d)) delays[i] = d;
        }

        // Materialize source pixels.
        byte[] pixels;
        if (image.Pixels is { } existing)
        {
            pixels = existing;
        }
        else
        {
            var sink = new MemorySink(image);
            await sink.RunAsync(cancellationToken);
            pixels = sink.Pixels;
        }

        var rawFormat = bands switch
        {
            1 => MagickFormat.Gray,
            3 => MagickFormat.Rgb,
            4 => MagickFormat.Rgba,
            _ => throw new InvalidOperationException()
        };

        int frameStride = width * bands;
        int frameSize = frameStride * pageHeight;

        using var collection = new MagickImageCollection();
        for (int p = 0; p < nPages; p++)
        {
            var frameBytes = new byte[frameSize];
            Buffer.BlockCopy(pixels, p * frameSize, frameBytes, 0, frameSize);

            var settings = new MagickReadSettings
            {
                Width = (uint)width,
                Height = (uint)pageHeight,
                Format = rawFormat,
                Depth = 8,
            };
            var frame = new MagickImage();
            frame.Read(frameBytes, settings);
            frame.AnimationDelay = delays[p];
            collection.Add(frame);
        }

        // Round-trip metadata via Magick's profile API. Profiles attach to
        // the first frame — typical reader convention for animated GIF.
        if (collection.Count > 0)
        {
            if (image.MetadataBlobs.TryGetValue("exif", out var exif))
                collection[0].SetProfile(new ImageProfile("exif", exif));
            if (image.MetadataBlobs.TryGetValue("xmp", out var xmp))
                collection[0].SetProfile(new ImageProfile("xmp", xmp));
            if (image.MetadataBlobs.TryGetValue("icc", out var icc))
                collection[0].SetProfile(new ColorProfile(icc));
        }

        // Magick.NET's Write isn't truly async; buffer to memory then forward.
        using var ms = new MemoryStream();
        collection.Write(ms, MagickFormat.Gif);
        ms.Position = 0;
        await ms.CopyToAsync(writer.AsStream(), cancellationToken);

        await writer.FlushAsync(cancellationToken);
        await writer.CompleteAsync();
    }
}
