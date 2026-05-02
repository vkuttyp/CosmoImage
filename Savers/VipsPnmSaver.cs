using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using ImageMagick;
using CosmoImage.Core;

namespace CosmoImage.Savers;

public enum VipsPnmVariant
{
    /// <summary>Auto: pick PBM/PGM/PPM by band count.</summary>
    Auto = 0,
    Pbm = 1, // bitmap (1 band, binarized)
    Pgm = 2, // grayscale
    Ppm = 3, // RGB
    Pam = 4, // arbitrary-band (preserves alpha)
}

/// <summary>
/// Netpbm writer. Pure-C# emitter for PBM (P4 binary), PGM (P5 binary),
/// PPM (P6 binary) — the standard variants. PAM (P7) still goes through
/// Magick.NET because of its more elaborate header layout.
///
/// <para>Auto mode picks PGM/PPM by band count; alpha-bearing inputs (2 or
/// 4 bands) route to PAM through Magick since the binary PGM/PPM variants
/// have no alpha channel. PBM (P4) auto-binarizes a 1-band input at the
/// midpoint when the caller explicitly requests it.</para>
/// </summary>
public static class VipsPnmSaver
{
    public static async Task SaveAsync(VipsImage image, PipeWriter writer, VipsPnmVariant variant = VipsPnmVariant.Auto, CancellationToken cancellationToken = default)
    {
        if (image == null) throw new ArgumentNullException(nameof(image));

        // Resolve auto. Alpha-bearing inputs (2 or 4 bands) need PAM since
        // PGM/PPM are alpha-less; we keep that path on Magick.
        var resolved = variant;
        if (resolved == VipsPnmVariant.Auto)
        {
            resolved = image.Bands switch
            {
                1 => VipsPnmVariant.Pgm,
                2 or 4 => VipsPnmVariant.Pam,
                _ => VipsPnmVariant.Ppm,
            };
        }

        if (resolved == VipsPnmVariant.Pam)
        {
            await VipsMagickWrapSaver.SaveAsync(image, writer, MagickFormat.Pam, cancellationToken);
            return;
        }

        // Materialize. For Float/etc. inputs, cast to UChar first so the
        // single-byte-per-sample binary variants emit correctly.
        var src = image.BandFormat == VipsBandFormat.UChar
            ? image
            : VipsImageOps.CastUChar(image);

        byte[] pixels;
        if (src.Pixels is { } existing)
        {
            pixels = existing;
        }
        else
        {
            var sink = new MemorySink(src);
            await sink.RunAsync(cancellationToken);
            pixels = sink.Pixels;
        }

        int width = src.Width;
        int height = src.Height;
        int bands = src.Bands;
        var stream = writer.AsStream();

        switch (resolved)
        {
            case VipsPnmVariant.Pbm:
                await WritePbmAsync(stream, pixels, width, height, bands, cancellationToken);
                break;
            case VipsPnmVariant.Pgm:
                await WritePgmAsync(stream, pixels, width, height, bands, cancellationToken);
                break;
            case VipsPnmVariant.Ppm:
                await WritePpmAsync(stream, pixels, width, height, bands, cancellationToken);
                break;
            default:
                throw new InvalidOperationException($"Unhandled PNM variant {resolved}");
        }

        await writer.FlushAsync(cancellationToken);
        await writer.CompleteAsync();
    }

    private static async Task WritePbmAsync(Stream stream, byte[] pixels, int width, int height, int bands, CancellationToken ct)
    {
        // Binarize: any byte ≥ 128 in the first band → '1' (black per PBM spec → 0 in our buffer = white in pipeline).
        // Pixel buffer convention here is "0 = black, 255 = white" (the
        // VipsImage convention); PBM stores "1 = black, 0 = white".
        var header = System.Text.Encoding.ASCII.GetBytes($"P4\n{width} {height}\n");
        await stream.WriteAsync(header, ct);

        int rowBytes = (width + 7) / 8;
        var row = new byte[rowBytes];
        for (int y = 0; y < height; y++)
        {
            Array.Clear(row, 0, rowBytes);
            for (int x = 0; x < width; x++)
            {
                byte v = pixels[(y * width + x) * bands]; // first band only
                if (v < 128)
                {
                    int byteIdx = x >> 3;
                    int bit = 7 - (x & 7);
                    row[byteIdx] |= (byte)(1 << bit);
                }
            }
            await stream.WriteAsync(row, ct);
        }
    }

    private static async Task WritePgmAsync(Stream stream, byte[] pixels, int width, int height, int bands, CancellationToken ct)
    {
        var header = System.Text.Encoding.ASCII.GetBytes($"P5\n{width} {height}\n255\n");
        await stream.WriteAsync(header, ct);

        if (bands == 1)
        {
            // Direct 1-band passthrough.
            await stream.WriteAsync(pixels.AsMemory(0, width * height), ct);
            return;
        }

        // Multi-band → take the luminance via Rec.709 weights so the gray
        // output matches what Greyscale() / Saturate(0) would produce.
        var row = new byte[width];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int baseIdx = (y * width + x) * bands;
                int gray;
                if (bands >= 3)
                {
                    gray = (int)(pixels[baseIdx] * 0.2126 + pixels[baseIdx + 1] * 0.7152 + pixels[baseIdx + 2] * 0.0722);
                }
                else
                {
                    gray = pixels[baseIdx];
                }
                row[x] = (byte)Math.Clamp(gray, 0, 255);
            }
            await stream.WriteAsync(row, ct);
        }
    }

    private static async Task WritePpmAsync(Stream stream, byte[] pixels, int width, int height, int bands, CancellationToken ct)
    {
        var header = System.Text.Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n");
        await stream.WriteAsync(header, ct);

        if (bands == 3)
        {
            // Direct 3-band passthrough.
            await stream.WriteAsync(pixels.AsMemory(0, width * height * 3), ct);
            return;
        }

        // 1-band → replicate to RGB; 4-band → drop alpha.
        var row = new byte[width * 3];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int srcBase = (y * width + x) * bands;
                if (bands == 1)
                {
                    byte g = pixels[srcBase];
                    row[x * 3] = g; row[x * 3 + 1] = g; row[x * 3 + 2] = g;
                }
                else // bands == 4
                {
                    row[x * 3] = pixels[srcBase];
                    row[x * 3 + 1] = pixels[srcBase + 1];
                    row[x * 3 + 2] = pixels[srcBase + 2];
                }
            }
            await stream.WriteAsync(row, ct);
        }
    }
}
