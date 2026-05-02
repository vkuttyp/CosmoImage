using System;
using System.Globalization;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace CosmoImage.Loaders;

/// <summary>
/// Netpbm family loader. Pure-C# parser for the standard variants:
/// PBM (P1 ASCII / P4 binary — bitmap), PGM (P2 ASCII / P5 binary — grayscale),
/// PPM (P3 ASCII / P6 binary — RGB). PAM (P7) and 16-bit-per-sample variants
/// still route through Magick.NET — PAM has a much more elaborate header
/// format, and 16-bit support inflates the parser without much real-world
/// payoff in our UChar pipeline. First "drop more Magick.NET" item.
///
/// <para>Header is ASCII tokens separated by whitespace; <c>#</c> introduces
/// a line comment. After the header, exactly one whitespace byte separates
/// the header from binary pixel data (P4/P5/P6). Sample values for P2/P3/P5/P6
/// are scaled to the canonical 0..255 UChar range using the file's
/// <c>maxval</c>; P1/P4 bits use the spec's <c>1 = black</c> convention
/// inverted to <c>0 = black</c> so it composes with the rest of the pipeline.</para>
/// </summary>
public static class VipsPnmLoader
{
    public static async ValueTask<bool> IsPnmAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        var sniff = await source.SniffAsync(2, cancellationToken);
        if (sniff.Length < 2) return false;
        var s = sniff.Span;
        if (s[0] != (byte)'P') return false;
        return s[1] >= (byte)'1' && s[1] <= (byte)'7';
    }

    public static async ValueTask<VipsImage?> LoadAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        if (!await IsPnmAsync(source, cancellationToken)) return null;

        var ms = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            ms.Write(buffer, 0, read);
        }
        var bytes = ms.ToArray();
        return Parse(bytes, cancellationToken);
    }

    /// <summary>Streaming variant — same shape since the loader already eagerly materializes the byte buffer.</summary>
    public static ValueTask<VipsImage?> LoadStreamingAsync(IVipsSource source, CancellationToken cancellationToken = default)
        => LoadAsync(source, cancellationToken);

    private static VipsImage? Parse(byte[] bytes, CancellationToken cancellationToken)
    {
        if (bytes.Length < 2 || bytes[0] != 'P') return null;
        char variant = (char)bytes[1];

        // PAM (P7) has a more elaborate header (WIDTH/HEIGHT/DEPTH/MAXVAL/
        // TUPLTYPE/ENDHDR lines); delegate it to the Magick-backed wrapper.
        if (variant == '7')
            return MagickFallback(bytes, cancellationToken);

        if (variant < '1' || variant > '6') return null;

        int pos = 2;
        bool hasMaxval = variant != '1' && variant != '4';
        bool isBinary = variant == '4' || variant == '5' || variant == '6';

        // Header tokens: width, height, [maxval]. Whitespace and # comments
        // are valid separators throughout.
        if (!ReadHeaderToken(bytes, ref pos, isBinary, out int width)) return null;
        if (!ReadHeaderToken(bytes, ref pos, isBinary, out int height)) return null;
        int maxval = 1; // PBM has implicit maxval=1
        if (hasMaxval)
        {
            if (!ReadHeaderToken(bytes, ref pos, isBinary, out maxval)) return null;
            if (maxval <= 0) return null;
        }
        if (width <= 0 || height <= 0) return null;

        // Single whitespace byte separates the header from binary data.
        if (isBinary && pos < bytes.Length && IsWhitespace(bytes[pos])) pos++;

        // 16-bit per sample (maxval > 255) is rare and inflates the parser;
        // route those through Magick to keep this implementation compact.
        if (maxval > 255)
            return MagickFallback(bytes, cancellationToken);

        int bands = (variant == '3' || variant == '6') ? 3 : 1;
        var pixels = new byte[width * height * bands];

        switch (variant)
        {
            case '1': // ASCII bits — '1' = black per spec, inverted to 0 here
                for (int i = 0; i < pixels.Length; i++)
                {
                    if (!ReadAsciiByte(bytes, ref pos, out int v)) return null;
                    pixels[i] = v == 1 ? (byte)0 : (byte)255;
                }
                break;

            case '2': // ASCII grayscale
            case '3': // ASCII RGB
                for (int i = 0; i < pixels.Length; i++)
                {
                    if (!ReadAsciiByte(bytes, ref pos, out int v)) return null;
                    pixels[i] = (byte)Math.Clamp(v * 255 / maxval, 0, 255);
                }
                break;

            case '4': // Binary packed bits, MSB-first per byte, row-padded
                int rowBytes = (width + 7) / 8;
                if (pos + rowBytes * height > bytes.Length) return null;
                for (int y = 0; y < height; y++)
                {
                    int rowOffset = pos + y * rowBytes;
                    for (int x = 0; x < width; x++)
                    {
                        byte byt = bytes[rowOffset + (x >> 3)];
                        int bit = (byt >> (7 - (x & 7))) & 1;
                        pixels[y * width + x] = bit == 1 ? (byte)0 : (byte)255;
                    }
                }
                break;

            case '5': // Binary grayscale
            case '6': // Binary RGB
                int dataLen = pixels.Length;
                if (pos + dataLen > bytes.Length) return null;
                if (maxval == 255)
                {
                    Buffer.BlockCopy(bytes, pos, pixels, 0, dataLen);
                }
                else
                {
                    // Rescale 0..maxval → 0..255. Cheap LUT for the common
                    // case where maxval differs but stays in 8-bit range.
                    var lut = new byte[maxval + 1];
                    for (int i = 0; i <= maxval; i++)
                        lut[i] = (byte)Math.Clamp(i * 255 / maxval, 0, 255);
                    for (int i = 0; i < dataLen; i++)
                    {
                        int b = bytes[pos + i];
                        pixels[i] = b <= maxval ? lut[b] : (byte)255;
                    }
                }
                break;
        }

        return new VipsImage
        {
            Width = width,
            Height = height,
            Bands = bands,
            BandFormat = VipsBandFormat.UChar,
            Interpretation = bands == 1 ? VipsInterpretation.BW : VipsInterpretation.RGB,
            Coding = VipsCoding.None,
            XRes = 1.0,
            YRes = 1.0,
            PixelsLazy = new Lazy<byte[]>(() => pixels),
        };
    }

    private static VipsImage? MagickFallback(byte[] bytes, CancellationToken cancellationToken)
    {
        var src = new PipeVipsSource(PipeReader.Create(new MemoryStream(bytes)));
        return VipsMagickWrapLoader.LoadAsync(src, cancellationToken).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Read one whitespace-separated header token, skipping <c>#</c>
    /// line comments. The <paramref name="binary"/> flag matters at the
    /// final delimiter: in binary variants we stop after one whitespace
    /// byte so subsequent reads land on the pixel data.
    /// </summary>
    private static bool ReadHeaderToken(byte[] bytes, ref int pos, bool binary, out int value)
    {
        SkipHeaderWhitespace(bytes, ref pos);
        int start = pos;
        while (pos < bytes.Length && !IsWhitespace(bytes[pos])) pos++;
        var s = System.Text.Encoding.ASCII.GetString(bytes, start, pos - start);
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Read one ASCII numeric token from the data section. Same shape as header tokens minus the comment handling.</summary>
    private static bool ReadAsciiByte(byte[] bytes, ref int pos, out int value)
    {
        while (pos < bytes.Length && IsWhitespace(bytes[pos])) pos++;
        int start = pos;
        while (pos < bytes.Length && !IsWhitespace(bytes[pos])) pos++;
        var s = System.Text.Encoding.ASCII.GetString(bytes, start, pos - start);
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Skip whitespace and <c>#</c>-introduced line comments.</summary>
    private static void SkipHeaderWhitespace(byte[] bytes, ref int pos)
    {
        while (pos < bytes.Length)
        {
            byte b = bytes[pos];
            if (IsWhitespace(b)) { pos++; continue; }
            if (b == (byte)'#')
            {
                while (pos < bytes.Length && bytes[pos] != (byte)'\n') pos++;
                continue;
            }
            return;
        }
    }

    private static bool IsWhitespace(byte b)
        => b == (byte)' ' || b == (byte)'\t' || b == (byte)'\n' || b == (byte)'\r' || b == (byte)'\v' || b == (byte)'\f';
}
