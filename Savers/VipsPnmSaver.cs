using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
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
/// Netpbm writer. Pure-C# emitter for the entire family: PBM (P4 binary),
/// PGM (P5 binary), PPM (P6 binary), PAM (P7).
///
/// <para>Auto mode picks PGM/PPM by band count; alpha-bearing inputs (2 or
/// 4 bands) route to PAM since the binary PGM/PPM variants have no alpha
/// channel. PBM (P4) auto-binarizes a 1-band input at the midpoint when
/// the caller explicitly requests it.</para>
///
/// <para>UShort inputs to PGM / PPM emit native 16-bit binary
/// (maxval=65535, big-endian samples per spec). PAM supports both 8-bit
/// (UChar) and 16-bit (UShort) directly; depth = bands.</para>
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

        // Materialize. UShort inputs emit native 16-bit binary (P5/P6/P7 with
        // maxval=65535); UChar passes through; everything else casts to UChar
        // first since we don't have lossless rescaling for Float/Int formats.
        bool sixteenBit = image.BandFormat == VipsBandFormat.UShort &&
                          (resolved == VipsPnmVariant.Pgm || resolved == VipsPnmVariant.Ppm
                           || resolved == VipsPnmVariant.Pam);
        var src = (image.BandFormat == VipsBandFormat.UChar || sixteenBit)
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
                if (sixteenBit) await WritePgm16Async(stream, pixels, width, height, bands, cancellationToken);
                else await WritePgmAsync(stream, pixels, width, height, bands, cancellationToken);
                break;
            case VipsPnmVariant.Ppm:
                if (sixteenBit) await WritePpm16Async(stream, pixels, width, height, bands, cancellationToken);
                else await WritePpmAsync(stream, pixels, width, height, bands, cancellationToken);
                break;
            case VipsPnmVariant.Pam:
                await WritePamAsync(stream, pixels, width, height, bands, sixteenBit, cancellationToken);
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

    /// <summary>
    /// 16-bit PGM (P5 with maxval 65535). Per spec the binary samples are
    /// big-endian; we host-store little-endian (UShort convention in
    /// <see cref="VipsImage"/>) so we byte-swap on the way out.
    /// </summary>
    private static async Task WritePgm16Async(Stream stream, byte[] pixels, int width, int height, int bands, CancellationToken ct)
    {
        var header = System.Text.Encoding.ASCII.GetBytes($"P5\n{width} {height}\n65535\n");
        await stream.WriteAsync(header, ct);

        var row = new byte[width * 2];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int srcOff = (y * width + x) * bands * 2;
                int sampleLo, sampleHi;
                if (bands >= 3)
                {
                    // Luminance via Rec.709 weights, mirroring the 8-bit path.
                    int rs = (pixels[srcOff + 1] << 8) | pixels[srcOff + 0];
                    int gs = (pixels[srcOff + 3] << 8) | pixels[srcOff + 2];
                    int bs = (pixels[srcOff + 5] << 8) | pixels[srcOff + 4];
                    int gray = (int)(rs * 0.2126 + gs * 0.7152 + bs * 0.0722);
                    sampleLo = gray & 0xFF;
                    sampleHi = (gray >> 8) & 0xFF;
                }
                else
                {
                    sampleLo = pixels[srcOff + 0];
                    sampleHi = pixels[srcOff + 1];
                }
                row[x * 2 + 0] = (byte)sampleHi; // big-endian per spec
                row[x * 2 + 1] = (byte)sampleLo;
            }
            await stream.WriteAsync(row, ct);
        }
    }

    /// <summary>
    /// PAM (P7) — line-oriented header followed by binary samples. Header
    /// is WIDTH/HEIGHT/DEPTH/MAXVAL[/TUPLTYPE] lines terminated by
    /// <c>ENDHDR</c>. Depth = bands (any band count); samples are
    /// big-endian for 16-bit. TUPLTYPE is a hint; we set it from the band
    /// count to match common readers.
    /// </summary>
    private static async Task WritePamAsync(Stream stream, byte[] pixels, int width, int height,
        int bands, bool sixteenBit, CancellationToken ct)
    {
        string tupletype = bands switch
        {
            1 => "GRAYSCALE",
            2 => "GRAYSCALE_ALPHA",
            3 => "RGB",
            4 => "RGB_ALPHA",
            _ => "RGB", // best-effort hint; depth field remains authoritative
        };
        int maxval = sixteenBit ? 65535 : 255;
        var header = System.Text.Encoding.ASCII.GetBytes(
            $"P7\nWIDTH {width}\nHEIGHT {height}\nDEPTH {bands}\nMAXVAL {maxval}\nTUPLTYPE {tupletype}\nENDHDR\n");
        await stream.WriteAsync(header, ct);

        if (!sixteenBit)
        {
            // 8-bit: pixels are already in row-major interleaved layout.
            await stream.WriteAsync(pixels.AsMemory(0, width * height * bands), ct);
            return;
        }

        // 16-bit: byte-swap to big-endian per spec.
        var row = new byte[width * bands * 2];
        for (int y = 0; y < height; y++)
        {
            int srcRow = y * width * bands * 2;
            for (int s = 0; s < width * bands; s++)
            {
                byte lo = pixels[srcRow + s * 2 + 0];
                byte hi = pixels[srcRow + s * 2 + 1];
                row[s * 2 + 0] = hi;
                row[s * 2 + 1] = lo;
            }
            await stream.WriteAsync(row, ct);
        }
    }

    /// <summary>16-bit PPM (P6 with maxval 65535) — RGB, big-endian samples.</summary>
    private static async Task WritePpm16Async(Stream stream, byte[] pixels, int width, int height, int bands, CancellationToken ct)
    {
        var header = System.Text.Encoding.ASCII.GetBytes($"P6\n{width} {height}\n65535\n");
        await stream.WriteAsync(header, ct);

        var row = new byte[width * 6];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int srcBase = (y * width + x) * bands * 2;
                if (bands == 3 || bands == 4)
                {
                    // Take RGB; drop alpha (4-band) by ignoring it.
                    for (int c = 0; c < 3; c++)
                    {
                        int srcOff = srcBase + c * 2;
                        row[x * 6 + c * 2 + 0] = pixels[srcOff + 1]; // big-endian
                        row[x * 6 + c * 2 + 1] = pixels[srcOff + 0];
                    }
                }
                else // bands == 1: replicate grey to RGB
                {
                    byte hi = pixels[srcBase + 1];
                    byte lo = pixels[srcBase + 0];
                    for (int c = 0; c < 3; c++)
                    {
                        row[x * 6 + c * 2 + 0] = hi;
                        row[x * 6 + c * 2 + 1] = lo;
                    }
                }
            }
            await stream.WriteAsync(row, ct);
        }
    }
}
