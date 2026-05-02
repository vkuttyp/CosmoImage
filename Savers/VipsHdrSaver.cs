using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using CosmoImage.Core;

namespace CosmoImage.Savers;

/// <summary>
/// Radiance HDR (<c>.hdr</c>) writer. Encodes a 3-band Float input as RGBE
/// with new-style per-scanline RLE compression. UChar input is auto-cast
/// to Float first (no auto-normalisation, matching the libvips Cast
/// convention used elsewhere in this port).
///
/// <para>Header is the canonical "<c>#?RADIANCE</c>" form with
/// <c>FORMAT=32-bit_rle_rgbe</c> and the standard "<c>-Y h +X w</c>"
/// resolution string. RLE is applied when width is in <c>[8, 0x7FFF]</c>;
/// outside that range the spec mandates plain uncompressed scanlines.</para>
/// </summary>
public static class VipsHdrSaver
{
    public static async Task SaveAsync(VipsImage image, PipeWriter writer, CancellationToken cancellationToken = default)
    {
        if (image == null) throw new ArgumentNullException(nameof(image));
        if (image.Bands != 3)
            throw new NotSupportedException($"HDR save needs 3 bands; got {image.Bands}");

        // Cast UChar to Float so the encoder always reads 4-byte samples.
        var floatImage = image.BandFormat == VipsBandFormat.Float
            ? image
            : VipsImageOps.CastFloat(image);

        // Materialize the whole image — RGBE output is row-major, no tiling
        // benefit, and the Float buffer is what we encode from.
        byte[] pixels;
        if (floatImage.Pixels is { } existing)
        {
            pixels = existing;
        }
        else
        {
            var sink = new MemorySink(floatImage);
            await sink.RunAsync(cancellationToken);
            pixels = sink.Pixels;
        }

        int width = floatImage.Width;
        int height = floatImage.Height;
        var stream = writer.AsStream();

        // ---- Header ----
        var header = "#?RADIANCE\n" +
                     "FORMAT=32-bit_rle_rgbe\n" +
                     "\n" +
                     $"-Y {height} +X {width}\n";
        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(header), cancellationToken);

        // ---- Scanlines ----
        var scanline = new byte[width * 4]; // RGBE per pixel
        bool useRle = width >= 8 && width <= 0x7FFF;
        var rleBuffer = useRle ? new byte[width * 5 + 4] : null; // worst-case RLE size

        for (int y = 0; y < height; y++)
        {
            int srcBase = y * width * 12; // 3 bands × 4 bytes per band
            for (int x = 0; x < width; x++)
            {
                float r = BinaryPrimitives.ReadSingleLittleEndian(pixels.AsSpan(srcBase + x * 12 + 0, 4));
                float g = BinaryPrimitives.ReadSingleLittleEndian(pixels.AsSpan(srcBase + x * 12 + 4, 4));
                float b = BinaryPrimitives.ReadSingleLittleEndian(pixels.AsSpan(srcBase + x * 12 + 8, 4));
                FloatToRgbe(r, g, b, scanline, x * 4);
            }

            if (useRle)
            {
                int len = EncodeRle(scanline, width, rleBuffer!);
                // 4-byte marker: 0x02 0x02 (width >> 8) (width & 0xFF)
                var marker = new byte[] { 0x02, 0x02, (byte)((width >> 8) & 0xFF), (byte)(width & 0xFF) };
                await stream.WriteAsync(marker, cancellationToken);
                await stream.WriteAsync(rleBuffer.AsMemory(0, len), cancellationToken);
            }
            else
            {
                await stream.WriteAsync(scanline, cancellationToken);
            }
        }

        await writer.FlushAsync(cancellationToken);
        await writer.CompleteAsync();
    }

    /// <summary>
    /// Pack one (r, g, b) Float pixel into 4 RGBE bytes at the given offset.
    /// Picks the largest of the three channels to drive the shared exponent.
    /// </summary>
    private static void FloatToRgbe(float r, float g, float b, byte[] dst, int off)
    {
        float max = Math.Max(r, Math.Max(g, b));
        if (max < 1e-32f)
        {
            dst[off + 0] = 0; dst[off + 1] = 0; dst[off + 2] = 0; dst[off + 3] = 0;
            return;
        }
        // mantissa in [0.5, 1), exponent picked so max*256 fits in [128, 255].
        int e;
        double m = Frexp(max, out e);
        // Greg Ward's reference encoder: m = mantissa, then scale = m * 256.0 / max.
        double scale = m * 256.0 / max;
        dst[off + 0] = (byte)Math.Clamp(r * scale, 0, 255);
        dst[off + 1] = (byte)Math.Clamp(g * scale, 0, 255);
        dst[off + 2] = (byte)Math.Clamp(b * scale, 0, 255);
        dst[off + 3] = (byte)Math.Clamp(e + 128, 0, 255);
    }

    /// <summary>
    /// .NET equivalent of C99 <c>frexp</c>. Returns mantissa in [0.5, 1)
    /// and writes the binary exponent so <c>x = mantissa * 2^exp</c>.
    /// </summary>
    private static double Frexp(double x, out int exp)
    {
        if (x == 0.0) { exp = 0; return 0.0; }
        long bits = BitConverter.DoubleToInt64Bits(x);
        int e = (int)((bits >> 52) & 0x7FF) - 1022;
        long m = (bits & ~(0x7FFL << 52)) | (1022L << 52);
        exp = e;
        return BitConverter.Int64BitsToDouble(m) * Math.Sign(x);
    }

    /// <summary>
    /// New-style per-component RLE. The 4 RGBE bands are emitted as 4
    /// separate streams; each uses the same scheme — control byte greater
    /// than 128 means "run of (ctrl - 128) copies of the next byte", control
    /// byte ≤ 128 means "literal run of <c>ctrl</c> bytes follows".
    /// </summary>
    private static int EncodeRle(byte[] scanline, int width, byte[] dst)
    {
        int len = 0;
        for (int comp = 0; comp < 4; comp++)
        {
            int i = 0;
            while (i < width)
            {
                // Look for a run of identical bytes >= 4 long. Below that
                // the literal encoding is shorter.
                int runStart = i;
                int runLen = 1;
                while (runStart + runLen < width
                    && runLen < 127
                    && scanline[(runStart + runLen) * 4 + comp] == scanline[runStart * 4 + comp])
                {
                    runLen++;
                }

                if (runLen >= 4)
                {
                    dst[len++] = (byte)(128 + runLen);
                    dst[len++] = scanline[runStart * 4 + comp];
                    i += runLen;
                }
                else
                {
                    // Literal run up to 128 bytes, ending when we encounter
                    // a stretch of 4+ identical bytes (which then becomes
                    // its own RLE run on the next iteration).
                    int litStart = i;
                    int litLen = 0;
                    while (litStart + litLen < width && litLen < 128)
                    {
                        // Stop the literal early if the next 4 bytes form a
                        // run worth encoding compactly.
                        if (litStart + litLen + 3 < width
                            && scanline[(litStart + litLen + 0) * 4 + comp] == scanline[(litStart + litLen + 1) * 4 + comp]
                            && scanline[(litStart + litLen + 1) * 4 + comp] == scanline[(litStart + litLen + 2) * 4 + comp]
                            && scanline[(litStart + litLen + 2) * 4 + comp] == scanline[(litStart + litLen + 3) * 4 + comp])
                        {
                            break;
                        }
                        litLen++;
                    }
                    if (litLen == 0) litLen = 1; // safety; shouldn't happen
                    dst[len++] = (byte)litLen;
                    for (int k = 0; k < litLen; k++)
                        dst[len++] = scanline[(litStart + k) * 4 + comp];
                    i += litLen;
                }
            }
        }
        return len;
    }
}
