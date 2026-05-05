using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CosmoImage.Loaders;

/// <summary>
/// Radiance HDR (<c>.hdr</c> / <c>.pic</c>) loader. The format is an ASCII
/// header followed by RGBE-encoded scanlines (4 bytes per pixel: R, G, B,
/// shared exponent). Scanlines are RLE-compressed when width is in
/// <c>[8, 0x7FFF]</c>; smaller or larger widths use plain uncompressed RGBE.
///
/// <para>Output is a 3-band Float <see cref="VipsImage"/> in linear-light
/// RGB. Float-throughout (rounds 5-8) is what makes this loadable
/// end-to-end without quantising the HDR range.</para>
///
/// Decoding follows Greg Ward's reference: per pixel
/// <c>f = 2^(E - 136); R_float = (R + 0.5) * f</c>, with <c>E == 0</c>
/// shorthand for an all-zero pixel.
/// </summary>
public static class VipsHdrLoader
{
    private static readonly byte[] RadianceMagic = System.Text.Encoding.ASCII.GetBytes("#?RADIANCE");
    private static readonly byte[] RgbeMagic = System.Text.Encoding.ASCII.GetBytes("#?RGBE");

    public static async ValueTask<bool> IsHdrAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        var sniff = await source.SniffAsync(11, cancellationToken);
        if (sniff.Length < 6) return false;
        var s = sniff.Span;
        if (sniff.Length >= 10 && s.Slice(0, 10).SequenceEqual(RadianceMagic)) return true;
        if (sniff.Length >= 6 && s.Slice(0, 6).SequenceEqual(RgbeMagic)) return true;
        return false;
    }

    public static async ValueTask<VipsImage?> LoadAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        if (!await IsHdrAsync(source, cancellationToken)) return null;

        // Drain into a buffer — same pattern as the other byte-buffered
        // loaders. Streaming variant could be added later but the format's
        // ASCII header makes it awkward to do without rewinding.
        var ms = new MemoryStream();
        var rawBuffer = new byte[81920];
        while (true)
        {
            int read = await source.ReadAsync(rawBuffer, cancellationToken);
            if (read == 0) break;
            ms.Write(rawBuffer, 0, read);
        }
        var bytes = ms.ToArray();

        // ---- Header ----
        // ASCII lines until an empty one, then a resolution line, then binary.
        int pos = 0;
        var metadata = new System.Collections.Generic.Dictionary<string, string>();

        // Read first line (magic) and discard.
        if (!ReadLine(bytes, ref pos, out _)) return null;

        while (true)
        {
            if (!ReadLine(bytes, ref pos, out var line)) return null;
            if (line.Length == 0) break;
            if (line.StartsWith("#")) continue;
            int eq = line.IndexOf('=');
            if (eq > 0)
            {
                var key = line.Substring(0, eq).Trim().ToLowerInvariant();
                var val = line.Substring(eq + 1).Trim();
                metadata[key] = val;
            }
        }

        // Resolution line. Standard form: "-Y <h> +X <w>". Other axis
        // orderings exist but are rare in real-world files; we punt on them.
        if (!ReadLine(bytes, ref pos, out var resLine)) return null;
        var parts = resLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4) return null;
        if (parts[0] != "-Y" || parts[2] != "+X") return null;
        if (!int.TryParse(parts[1], out int height)) return null;
        if (!int.TryParse(parts[3], out int width)) return null;
        if (width <= 0 || height <= 0) return null;

        // ---- Pixel decode (eager — HDR pixels are Float, no UChar fallback) ----
        var pixels = new byte[width * height * 3 * 4];
        var scanline = new byte[width * 4];

        for (int y = 0; y < height; y++)
        {
            if (!ReadScanline(bytes, ref pos, width, scanline)) return null;
            // Scanline now holds RGBE per pixel. Convert to 3 floats.
            int dstBase = y * width * 3 * 4;
            for (int x = 0; x < width; x++)
            {
                byte r = scanline[x * 4 + 0];
                byte g = scanline[x * 4 + 1];
                byte b = scanline[x * 4 + 2];
                byte e = scanline[x * 4 + 3];
                float fr, fg, fb;
                if (e == 0)
                {
                    fr = fg = fb = 0f;
                }
                else
                {
                    // f = 2^(e - 136). e=128 → f = 2^-8 = 1/256, so a pixel
                    // with R=G=B=128, E=128 decodes to ~(0.502, 0.502, 0.502).
                    // The +0.5 mid-cell offset matches the reference encoder.
                    double f = Math.Pow(2.0, e - 136);
                    fr = (float)((r + 0.5) * f);
                    fg = (float)((g + 0.5) * f);
                    fb = (float)((b + 0.5) * f);
                }
                BinaryPrimitives.WriteSingleLittleEndian(pixels.AsSpan(dstBase + x * 12 + 0, 4), fr);
                BinaryPrimitives.WriteSingleLittleEndian(pixels.AsSpan(dstBase + x * 12 + 4, 4), fg);
                BinaryPrimitives.WriteSingleLittleEndian(pixels.AsSpan(dstBase + x * 12 + 8, 4), fb);
            }
        }

        var image = new VipsImage
        {
            Width = width,
            Height = height,
            Bands = 3,
            BandFormat = VipsBandFormat.Float,
            Interpretation = VipsInterpretation.RGB,
            Coding = VipsCoding.None,
            XRes = 1.0,
            YRes = 1.0,
            PixelsLazy = new Lazy<byte[]>(() => pixels),
        };
        foreach (var kv in metadata) image.Metadata["hdr:" + kv.Key] = kv.Value;
        return image;
    }

    /// <summary>
    /// Read one scanline of RGBE. Handles both the "new-style" RLE format
    /// (marker bytes 0x02 0x02 followed by big-endian width, then 4 RLE
    /// streams for R/G/B/E) and uncompressed/old-style scanlines.
    /// </summary>
    private static bool ReadScanline(byte[] bytes, ref int pos, int width, byte[] scanline)
    {
        if (pos + 4 > bytes.Length) return false;

        // Detect new-style RLE: marker is 0x02, 0x02, hi(width), lo(width).
        bool newRle = width >= 8 && width <= 0x7FFF
            && bytes[pos] == 0x02 && bytes[pos + 1] == 0x02
            && ((bytes[pos + 2] << 8) | bytes[pos + 3]) == width;

        if (!newRle)
        {
            // Old-style: width pixels of RGBE that may include run markers.
            // Per Greg Ward's reference decoder (oldreadcolrs in
            // radiance/src/common/color.c), a pixel with R=G=B=1 is a run
            // marker that replicates the previous pixel (E << rshift)
            // times. rshift accumulates by 8 across consecutive run
            // markers (so a multi-marker chain can encode large runs);
            // any non-marker pixel resets rshift to 0. Plain literal
            // scanlines decode through the same loop with no run markers
            // ever firing.
            return ReadOldRleScanline(bytes, ref pos, width, scanline);
        }

        pos += 4;

        // 4 component streams: R, G, B, E. Each is RLE-encoded with the
        // same scheme: a leading byte > 128 starts a run of length (byte-128)
        // of a single value; ≤ 128 starts a literal run of that length.
        for (int comp = 0; comp < 4; comp++)
        {
            int x = 0;
            while (x < width)
            {
                if (pos >= bytes.Length) return false;
                byte ctrl = bytes[pos++];
                if (ctrl > 128)
                {
                    int runLen = ctrl - 128;
                    if (pos >= bytes.Length || x + runLen > width) return false;
                    byte val = bytes[pos++];
                    for (int i = 0; i < runLen; i++) scanline[(x + i) * 4 + comp] = val;
                    x += runLen;
                }
                else
                {
                    int litLen = ctrl;
                    if (litLen == 0) return false; // malformed
                    if (pos + litLen > bytes.Length || x + litLen > width) return false;
                    for (int i = 0; i < litLen; i++) scanline[(x + i) * 4 + comp] = bytes[pos + i];
                    pos += litLen;
                    x += litLen;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// Decode an old-style scanline. Greg Ward's algorithm: walk pixels
    /// 4 bytes at a time, treating R=G=B=1 quads as run markers that
    /// replicate the previous pixel <c>(E &lt;&lt; rshift)</c> times.
    /// rshift starts at 0 and grows by 8 with each consecutive marker;
    /// any non-marker pixel resets it.
    /// </summary>
    private static bool ReadOldRleScanline(byte[] bytes, ref int pos, int width, byte[] scanline)
    {
        int rshift = 0;
        int x = 0;
        while (x < width)
        {
            if (pos + 4 > bytes.Length) return false;
            byte r = bytes[pos++];
            byte g = bytes[pos++];
            byte b = bytes[pos++];
            byte e = bytes[pos++];
            if (r == 1 && g == 1 && b == 1)
            {
                // Run marker — replicate previous pixel. A marker as the
                // very first pixel of a scanline would have nothing to
                // copy from; reject as malformed.
                if (x == 0) return false;
                int repCount = e << rshift;
                int prevOff = (x - 1) * 4;
                int copies = Math.Min(repCount, width - x);
                for (int i = 0; i < copies; i++)
                {
                    int dst = x * 4;
                    scanline[dst + 0] = scanline[prevOff + 0];
                    scanline[dst + 1] = scanline[prevOff + 1];
                    scanline[dst + 2] = scanline[prevOff + 2];
                    scanline[dst + 3] = scanline[prevOff + 3];
                    x++;
                }
                rshift += 8;
            }
            else
            {
                int dst = x * 4;
                scanline[dst + 0] = r;
                scanline[dst + 1] = g;
                scanline[dst + 2] = b;
                scanline[dst + 3] = e;
                x++;
                rshift = 0;
            }
        }
        return true;
    }

    private static bool ReadLine(byte[] bytes, ref int pos, out string line)
    {
        int start = pos;
        while (pos < bytes.Length && bytes[pos] != (byte)'\n') pos++;
        if (pos > bytes.Length) { line = ""; return false; }
        int end = pos;
        // Strip optional CR.
        if (end > start && bytes[end - 1] == (byte)'\r') end--;
        line = System.Text.Encoding.ASCII.GetString(bytes, start, end - start);
        if (pos < bytes.Length) pos++; // consume the newline
        return true;
    }
}
