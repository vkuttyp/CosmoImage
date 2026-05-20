using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace CosmoImage.Savers;

/// <summary>
/// WebP encoder, pure-managed (no native deps). Backed by
/// <see cref="PureWebpLosslessEncoder"/>, which implements VP8L lossless
/// per the WebP Lossless Bitstream Specification.
///
/// <para>Scope: <b>VP8L lossless only</b>. Lossy (VP8) encode requires a
/// full VP8 codec implementation, which is out of scope for now. Callers
/// asking for lossy encoding get a <see cref="NotSupportedException"/>
/// with a clear message rather than silently downgrading to lossless.</para>
///
/// <para>Phase-1 limitations (all surface as NotSupportedException with
/// specific messages so the caller knows exactly which feature is missing):
/// <list type="bullet">
///   <item>Lossy (VP8) encode</item>
///   <item>Animated WebP (multi-frame ANMF/ANIM)</item>
/// </list>
/// </para>
/// <para>EXIF / XMP / ICCP metadata embed is supported via
/// <see cref="WebpRiffMux"/>.</para>
/// </summary>
public static class VipsWebpSaver
{
    public static async Task SaveAsync(
        VipsImage image,
        PipeWriter writer,
        int quality = 75,
        bool lossless = false,
        CancellationToken cancellationToken = default)
    {
        if (!lossless)
            throw new NotSupportedException(
                "WebP save: only lossless encoding is currently supported (pass lossless: true). " +
                "Lossy VP8 encoding requires a full VP8 codec implementation which has not yet been ported. " +
                "The `quality` parameter is unused in lossless mode.");

        int width = image.Width;
        int height = image.Height;
        int bands = image.Bands;

        if (bands != 1 && bands != 3 && bands != 4)
            throw new NotSupportedException($"WebP save needs 1, 3, or 4 bands; got {bands}");

        // Detect animated layout. If present, refuse — animated VP8L encode
        // needs RIFF ANMF framing + the libwebpmux-equivalent assembly logic,
        // which is a follow-on task.
        if (image.Metadata.TryGetValue("n-pages", out var npStr) &&
            int.TryParse(npStr, out int nP) && nP > 1)
        {
            throw new NotSupportedException(
                $"WebP save: animated WebP encode (n-pages={nP}) is not yet supported. " +
                "Pass a single-frame image (strip animation metadata first).");
        }

        // Materialize pixels.
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

        // VP8L encoder takes RGBA. Expand 1- and 3-band input.
        byte[] rgba = bands switch
        {
            4 => pixels,
            3 => ExpandRgbToRgba(pixels, width, height),
            1 => ExpandGrayToRgba(pixels, width, height),
            _ => throw new InvalidOperationException()
        };

        byte[] webpBytes = PureWebpLosslessEncoder.Encode(rgba, width, height);

        // Attach EXIF / XMP / ICCP via the RIFF mux if any are present.
        image.MetadataBlobs.TryGetValue("exif", out var exif);
        image.MetadataBlobs.TryGetValue("xmp",  out var xmp);
        image.MetadataBlobs.TryGetValue("icc",  out var icc);
        if (exif != null || xmp != null || icc != null)
            webpBytes = WebpRiffMux.Wrap(webpBytes, exif, xmp, icc);

        await writer.WriteAsync(webpBytes, cancellationToken);
        await writer.FlushAsync(cancellationToken);
        await writer.CompleteAsync();
    }

    private static byte[] ExpandRgbToRgba(byte[] rgb, int width, int height)
    {
        var rgba = new byte[width * height * 4];
        for (int p = 0, j = 0; p < width * height; p++, j += 4)
        {
            int s = p * 3;
            rgba[j + 0] = rgb[s + 0];
            rgba[j + 1] = rgb[s + 1];
            rgba[j + 2] = rgb[s + 2];
            rgba[j + 3] = 255;
        }
        return rgba;
    }

    private static byte[] ExpandGrayToRgba(byte[] gray, int width, int height)
    {
        var rgba = new byte[width * height * 4];
        for (int p = 0, j = 0; p < gray.Length; p++, j += 4)
        {
            byte g = gray[p];
            rgba[j + 0] = g;
            rgba[j + 1] = g;
            rgba[j + 2] = g;
            rgba[j + 3] = 255;
        }
        return rgba;
    }
}
