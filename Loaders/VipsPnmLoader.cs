using System;
using System.Globalization;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace CosmoImage.Loaders;

/// <summary>
/// Netpbm family loader. Pure-C# parser for the full Netpbm matrix:
/// <list type="bullet">
///   <item>PBM (P1 ASCII / P4 binary — bitmap)</item>
///   <item>PGM (P2 ASCII / P5 binary — grayscale)</item>
///   <item>PPM (P3 ASCII / P6 binary — RGB)</item>
///   <item>PAM (P7 — header lines: WIDTH / HEIGHT / DEPTH / MAXVAL /
///         TUPLTYPE / ENDHDR; arbitrary band count)</item>
/// </list>
/// 16-bit-per-sample variants (maxval > 255) output
/// <see cref="VipsBandFormat.UShort"/>; 8-bit and below are
/// <see cref="VipsBandFormat.UChar"/>.
///
/// <para>Header is ASCII tokens separated by whitespace; <c>#</c> introduces
/// a line comment. After the header, exactly one whitespace byte separates
/// the header from binary pixel data (P4/P5/P6/P7). Binary 16-bit samples
/// are big-endian per spec and get byte-swapped to host LE on read.
/// P1/P4 bits use the spec's <c>1 = black</c> convention inverted to
/// <c>0 = black</c> so it composes with the rest of the pipeline.</para>
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

        if (variant == '7') return ParsePam(bytes);
        if (variant < '1' || variant > '6') return null;

        int pos = 2;
        bool hasMaxval = variant != '1' && variant != '4';
        bool isBinary = variant == '4' || variant == '5' || variant == '6';

        if (!ReadHeaderToken(bytes, ref pos, out int width)) return null;
        if (!ReadHeaderToken(bytes, ref pos, out int height)) return null;
        int maxval = 1; // PBM has implicit maxval=1
        if (hasMaxval)
        {
            if (!ReadHeaderToken(bytes, ref pos, out maxval)) return null;
            if (maxval <= 0 || maxval > 65535) return null;
        }
        if (width <= 0 || height <= 0) return null;

        // Single whitespace byte separates the header from binary data.
        if (isBinary && pos < bytes.Length && IsWhitespace(bytes[pos])) pos++;

        int bands = (variant == '3' || variant == '6') ? 3 : 1;
        bool sixteenBit = maxval > 255;
        int bytesPerSample = sixteenBit ? 2 : 1;
        var pixels = new byte[width * height * bands * bytesPerSample];

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
                if (sixteenBit)
                {
                    for (int i = 0; i < width * height * bands; i++)
                    {
                        if (!ReadAsciiByte(bytes, ref pos, out int v)) return null;
                        ushort u = (ushort)Math.Clamp((long)v * 65535 / maxval, 0, 65535);
                        pixels[i * 2]     = (byte)u;
                        pixels[i * 2 + 1] = (byte)(u >> 8);
                    }
                }
                else
                {
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        if (!ReadAsciiByte(bytes, ref pos, out int v)) return null;
                        pixels[i] = (byte)Math.Clamp(v * 255 / maxval, 0, 255);
                    }
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
                int sampleCount = width * height * bands;
                int dataLen = sampleCount * bytesPerSample;
                if (pos + dataLen > bytes.Length) return null;
                if (sixteenBit)
                {
                    DecodeBinary16(bytes, pos, pixels, sampleCount, maxval);
                }
                else if (maxval == 255)
                {
                    Buffer.BlockCopy(bytes, pos, pixels, 0, dataLen);
                }
                else
                {
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

        return BuildImage(width, height, bands, sixteenBit, pixels,
            interpretation: bands == 1 ? VipsInterpretation.BW : VipsInterpretation.RGB);
    }

    /// <summary>
    /// Parse a PAM (P7) file. Header is line-oriented with key/value
    /// pairs (WIDTH/HEIGHT/DEPTH/MAXVAL/TUPLTYPE) terminated by a line
    /// containing just <c>ENDHDR</c>. Pixel data is binary, depth × WHM
    /// samples, big-endian for 16-bit. TUPLTYPE just hints the
    /// interpretation; we trust DEPTH for band count.
    /// </summary>
    private static VipsImage? ParsePam(byte[] bytes)
    {
        int pos = 2;  // skip 'P7'
        // Skip any whitespace right after the magic to land on the first line.
        while (pos < bytes.Length && IsWhitespace(bytes[pos])) pos++;

        int width = -1, height = -1, depth = -1, maxval = -1;
        string? tupletype = null;
        while (pos < bytes.Length)
        {
            // Read a logical line (skipping blank lines + comments).
            while (pos < bytes.Length && IsWhitespace(bytes[pos])) pos++;
            if (pos >= bytes.Length) return null;
            if (bytes[pos] == (byte)'#')
            {
                while (pos < bytes.Length && bytes[pos] != (byte)'\n') pos++;
                continue;
            }
            int lineStart = pos;
            while (pos < bytes.Length && bytes[pos] != (byte)'\n') pos++;
            string line = System.Text.Encoding.ASCII.GetString(bytes, lineStart, pos - lineStart).TrimEnd('\r');
            if (pos < bytes.Length) pos++;  // consume newline

            if (string.IsNullOrWhiteSpace(line)) continue;
            string trimmed = line.Trim();
            if (trimmed == "ENDHDR") break;

            // Each non-ENDHDR header line is "KEY VALUE".
            int sp = trimmed.IndexOf(' ');
            if (sp < 0) continue;
            string key = trimmed.Substring(0, sp);
            string val = trimmed.Substring(sp + 1).Trim();
            switch (key)
            {
                case "WIDTH":   int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out width); break;
                case "HEIGHT":  int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out height); break;
                case "DEPTH":   int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out depth); break;
                case "MAXVAL":  int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out maxval); break;
                case "TUPLTYPE": tupletype = val; break;
            }
        }

        if (width <= 0 || height <= 0 || depth <= 0 || maxval <= 0) return null;
        if (depth > 4) return null;        // RGBA at most for our pipeline
        if (maxval > 65535) return null;

        bool sixteenBit = maxval > 255;
        int bytesPerSample = sixteenBit ? 2 : 1;
        int sampleCount = width * height * depth;
        int dataLen = sampleCount * bytesPerSample;
        if (pos + dataLen > bytes.Length) return null;

        var pixels = new byte[dataLen];
        if (sixteenBit)
        {
            DecodeBinary16(bytes, pos, pixels, sampleCount, maxval);
        }
        else if (maxval == 255)
        {
            Buffer.BlockCopy(bytes, pos, pixels, 0, dataLen);
        }
        else
        {
            var lut = new byte[maxval + 1];
            for (int i = 0; i <= maxval; i++)
                lut[i] = (byte)Math.Clamp(i * 255 / maxval, 0, 255);
            for (int i = 0; i < dataLen; i++)
            {
                int b = bytes[pos + i];
                pixels[i] = b <= maxval ? lut[b] : (byte)255;
            }
        }

        // Pick an interpretation hint. TUPLTYPE wins when it's a
        // recognised string; otherwise infer from depth.
        VipsInterpretation interp = tupletype switch
        {
            "BLACKANDWHITE" => VipsInterpretation.BW,
            "GRAYSCALE" => VipsInterpretation.BW,
            "GRAYSCALE_ALPHA" => VipsInterpretation.BW,
            "RGB" => VipsInterpretation.RGB,
            "RGB_ALPHA" => VipsInterpretation.RGB,
            _ => depth == 1 || depth == 2 ? VipsInterpretation.BW : VipsInterpretation.RGB,
        };
        return BuildImage(width, height, depth, sixteenBit, pixels, interp);
    }

    /// <summary>
    /// Decode a binary 16-bit-per-sample PNM stream. Source is
    /// big-endian (per spec); we byte-swap to host LE while rescaling
    /// to the canonical 0..65535 UShort range.
    /// </summary>
    private static void DecodeBinary16(byte[] src, int srcOff, byte[] dst, int sampleCount, int maxval)
    {
        if (maxval == 65535)
        {
            for (int i = 0; i < sampleCount; i++)
            {
                byte hi = src[srcOff + i * 2];
                byte lo = src[srcOff + i * 2 + 1];
                dst[i * 2]     = lo;
                dst[i * 2 + 1] = hi;
            }
        }
        else
        {
            for (int i = 0; i < sampleCount; i++)
            {
                byte hi = src[srcOff + i * 2];
                byte lo = src[srcOff + i * 2 + 1];
                int v = (hi << 8) | lo;
                int rescaled = (int)Math.Clamp((long)v * 65535 / maxval, 0, 65535);
                dst[i * 2]     = (byte)rescaled;
                dst[i * 2 + 1] = (byte)(rescaled >> 8);
            }
        }
    }

    private static VipsImage BuildImage(int width, int height, int bands,
        bool sixteenBit, byte[] pixels, VipsInterpretation interpretation)
        => new VipsImage
        {
            Width = width,
            Height = height,
            Bands = bands,
            BandFormat = sixteenBit ? VipsBandFormat.UShort : VipsBandFormat.UChar,
            Interpretation = interpretation,
            Coding = VipsCoding.None,
            XRes = 1.0,
            YRes = 1.0,
            PixelsLazy = new Lazy<byte[]>(() => pixels),
        };

    /// <summary>
    /// Read one whitespace-separated header token, skipping <c>#</c>
    /// line comments. Caller is responsible for stopping after the
    /// last token's terminating whitespace when transitioning to
    /// binary pixel data.
    /// </summary>
    private static bool ReadHeaderToken(byte[] bytes, ref int pos, out int value)
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
