using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using CosmoImage.Core;

namespace CosmoImage.Savers;

/// <summary>
/// FITS writer. Emits a SIMPLE / BITPIX / NAXIS / NAXISn / END header
/// padded to a 2880-byte block, followed by big-endian raw pixel data.
/// Multi-band input writes NAXIS = 3 with NAXIS3 = bands and a planar
/// layout (one full plane after another) — VipsImage's interleaved
/// internal storage is transposed on the way out.
///
/// <para>BITPIX is picked from the input <see cref="VipsImage.BandFormat"/>:
/// UChar → 8, Float → -32, others fall back to Float for safety. BSCALE
/// and BZERO are written as 1.0 / 0.0 (no value transform on output).</para>
///
/// <para>Any <c>Metadata["fits:KEY"] = value</c> entries surface as
/// header cards before END so a load → save round-trip preserves WCS
/// keywords / observation logs / etc. without our needing to interpret them.</para>
/// </summary>
public static class VipsFitsSaver
{
    private const int BlockSize = 2880;
    private const int CardSize = 80;

    public static async Task SaveAsync(VipsImage image, PipeWriter writer, CancellationToken cancellationToken = default)
    {
        if (image == null) throw new ArgumentNullException(nameof(image));
        if (image.Bands < 1 || image.Bands > 4)
            throw new NotSupportedException($"FITS save needs 1..4 bands; got {image.Bands}");

        // Decide BITPIX. UChar passes through unchanged; everything else
        // round-trips through Float to keep the saver simple.
        int bitpix = image.BandFormat == VipsBandFormat.UChar ? 8 : -32;
        bool outFloat = bitpix == -32;
        VipsImage src = image.BandFormat is VipsBandFormat.UChar or VipsBandFormat.Float
            ? image
            : VipsImageOps.CastFloat(image);

        // Materialize so the planar transpose can index pixels by (y, x, b).
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

        var stream = writer.AsStream();
        await WriteHeaderAsync(stream, src, bitpix, cancellationToken);

        // Planar pixel write — for each plane, walk row-major. Big-endian
        // serialization on the wire (FITS spec) regardless of host byte order.
        int W = src.Width;
        int H = src.Height;
        int planes = src.Bands;
        int inSizePel = src.SizeOfPel;
        int bytesPerSample = outFloat ? 4 : 1;
        long dataLen = (long)W * H * planes * bytesPerSample;
        var sampleBuf = new byte[bytesPerSample];

        for (int p = 0; p < planes; p++)
        {
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int srcOff = (y * W + x) * inSizePel + p * bytesPerSample;
                    if (outFloat)
                    {
                        // Re-read as little-endian (the in-memory format),
                        // re-emit as big-endian.
                        float v = BinaryPrimitives.ReadSingleLittleEndian(pixels.AsSpan(srcOff, 4));
                        BinaryPrimitives.WriteSingleBigEndian(sampleBuf, v);
                    }
                    else
                    {
                        sampleBuf[0] = pixels[srcOff];
                    }
                    await stream.WriteAsync(sampleBuf, cancellationToken);
                }
            }
        }

        // Pad pixel data to next 2880 boundary with zeros.
        long pad = (BlockSize - (dataLen % BlockSize)) % BlockSize;
        if (pad > 0)
        {
            var padBuf = new byte[pad];
            await stream.WriteAsync(padBuf, cancellationToken);
        }

        await writer.FlushAsync(cancellationToken);
        await writer.CompleteAsync();
    }

    private static async Task WriteHeaderAsync(Stream stream, VipsImage image, int bitpix, CancellationToken cancellationToken)
    {
        // Build cards, append END, pad to 2880-byte block. Each card is
        // exactly 80 bytes wide (space-padded). Standard FITS keyword =
        // value lives at fixed columns: KEYWORD (8 chars) + "= " + value
        // right-justified in cols 10–30.
        var cards = new System.Collections.Generic.List<string>
        {
            FormatBoolCard("SIMPLE", true, "/ Standard FITS"),
            FormatIntCard("BITPIX", bitpix, "/ Bits per sample"),
            FormatIntCard("NAXIS", image.Bands == 1 ? 2 : 3, "/ Number of axes"),
            FormatIntCard("NAXIS1", image.Width, "/ Image width"),
            FormatIntCard("NAXIS2", image.Height, "/ Image height"),
        };
        if (image.Bands > 1)
            cards.Add(FormatIntCard("NAXIS3", image.Bands, "/ Number of bands (planar)"));
        // BSCALE/BZERO at 1.0/0.0 means "no transform" — the de-facto default.
        cards.Add(FormatFloatCard("BSCALE", 1.0, "/ Pixel scale (no transform)"));
        cards.Add(FormatFloatCard("BZERO",  0.0, "/ Pixel offset (no transform)"));

        // Round-trip preserved cards from Metadata["fits:*"] without
        // interpreting them. Skip any that would conflict with the
        // structural keywords we just wrote.
        foreach (var kv in image.Metadata)
        {
            if (!kv.Key.StartsWith("fits:")) continue;
            var key = kv.Key.Substring("fits:".Length).ToUpperInvariant();
            if (key.Length > 8) continue; // can't fit; skip
            if (key is "SIMPLE" or "BITPIX" or "NAXIS" or "NAXIS1" or "NAXIS2" or "NAXIS3"
                or "BSCALE" or "BZERO" or "END")
                continue;
            cards.Add(FormatStringCard(key, kv.Value));
        }
        cards.Add("END".PadRight(CardSize));

        // Pad cards out to a full 2880-byte block.
        int pad = (BlockSize - ((cards.Count * CardSize) % BlockSize)) % BlockSize;
        int padCards = pad / CardSize;
        for (int i = 0; i < padCards; i++) cards.Add(new string(' ', CardSize));

        foreach (var card in cards)
        {
            // ASCII per spec; clamp width defensively.
            var line = card.Length == CardSize ? card : card.PadRight(CardSize).Substring(0, CardSize);
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(line), cancellationToken);
        }
    }

    private static string FormatBoolCard(string keyword, bool value, string comment)
    {
        // Value goes in column 30 (1-indexed) — i.e. card[29] = 'T' or 'F'.
        var key = keyword.PadRight(8);
        var v = value ? "T" : "F";
        var fixedField = " " + v.PadLeft(20);
        var card = key + "= " + fixedField + " " + comment;
        return card.PadRight(CardSize).Substring(0, CardSize);
    }

    private static string FormatIntCard(string keyword, int value, string comment)
    {
        var key = keyword.PadRight(8);
        var v = value.ToString(CultureInfo.InvariantCulture).PadLeft(20);
        var card = key + "= " + v + " " + comment;
        return card.PadRight(CardSize).Substring(0, CardSize);
    }

    private static string FormatFloatCard(string keyword, double value, string comment)
    {
        var key = keyword.PadRight(8);
        // 20-column right-justified, scientific notation if needed.
        var v = value.ToString("0.00000000E+00", CultureInfo.InvariantCulture).PadLeft(20);
        var card = key + "= " + v + " " + comment;
        return card.PadRight(CardSize).Substring(0, CardSize);
    }

    private static string FormatStringCard(string keyword, string value)
    {
        // Quoted string per FITS spec, padded with trailing spaces inside the
        // quotes to the standard min-length (8) so common parsers accept it.
        var key = keyword.PadRight(8);
        // Trim to fit 80-char card after quotes/structure (max ~67 chars).
        var truncated = value.Length > 67 ? value.Substring(0, 67) : value;
        var quoted = "'" + truncated.Replace("'", "''").PadRight(8) + "'";
        var card = key + "= " + quoted;
        return card.PadRight(CardSize).Substring(0, CardSize);
    }
}
