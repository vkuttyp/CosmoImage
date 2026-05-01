using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using ImageMagick;

namespace CosmoImage.Savers;

public static class VipsWebpSaver
{
    public static async Task SaveAsync(VipsImage image, PipeWriter writer, int quality = 75, bool lossless = false, CancellationToken cancellationToken = default)
    {
        int width = image.Width;
        int height = image.Height;
        int bands = image.Bands;

        if (bands != 1 && bands != 3 && bands != 4)
            throw new NotSupportedException($"WebP save needs 1, 3, or 4 bands; got {bands}");

        // Detect multi-frame layout for animated WebP. Single-frame is just
        // a 1-element collection — same code path either way.
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
            frame.Format = MagickFormat.WebP;
            frame.Quality = (uint)Math.Clamp(quality, 1, 100);
            if (lossless) frame.Settings.SetDefine(MagickFormat.WebP, "lossless", "true");
            if (nPages > 1) frame.AnimationDelay = delays[p];
            collection.Add(frame);
        }

        // Round-trip metadata via Magick's profile API. WebP RIFF EXIF/XMP/ICCP
        // chunks are written as a single set even for animated WebPs; attach to
        // the first frame.
        if (collection.Count > 0)
        {
            if (image.MetadataBlobs.TryGetValue("exif", out var exif))
                collection[0].SetProfile(new ImageProfile("exif", exif));
            if (image.MetadataBlobs.TryGetValue("xmp", out var xmp))
                collection[0].SetProfile(new ImageProfile("xmp", xmp));
            if (image.MetadataBlobs.TryGetValue("icc", out var icc))
                collection[0].SetProfile(new ColorProfile(icc));
        }

        using var ms = new MemoryStream();
        collection.Write(ms, MagickFormat.WebP);
        ms.Position = 0;
        await ms.CopyToAsync(writer.AsStream(), cancellationToken);

        await writer.FlushAsync(cancellationToken);
        await writer.CompleteAsync();
    }
}
