using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CosmoImage.Loaders;

/// <summary>
/// CSV / numeric-text loader. Each non-empty, non-comment line is one row of
/// numbers separated by whitespace or commas. All rows must have the same
/// width. Output is a single-band UChar image with values clamped to [0,255]
/// — sufficient for masks/maps/LUTs in a UChar pipeline. (When float-format
/// ops land, this can be widened to Float without rewriting the parser.)
/// Lines starting with <c>#</c> are skipped. Mirrors libvips' <c>vips_csvload</c>.
/// </summary>
public static class VipsCsvLoader
{
    public static async ValueTask<VipsImage?> LoadAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        var ms = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            ms.Write(buffer, 0, read);
        }
        if (ms.Length == 0) return null;
        return Parse(ms.ToArray(), skipHeaderTokens: 0);
    }

    internal static VipsImage? Parse(byte[] bytes, int skipHeaderTokens)
    {
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        var rawLines = text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

        // Strip comments and empty/whitespace-only lines.
        var rows = new System.Collections.Generic.List<double[]>();
        bool first = true;
        int width = 0;
        foreach (var raw in rawLines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var tokens = line.Split(new[] { ',', ' ', '\t', ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) continue;

            // Matrix-format files start with header tokens (width height [scale offset]).
            // Caller passes skipHeaderTokens to drop those before tokenization here.
            if (first && skipHeaderTokens > 0)
            {
                if (tokens.Length < skipHeaderTokens) return null;
                first = false;
                if (tokens.Length == skipHeaderTokens) continue;
                tokens = tokens[skipHeaderTokens..];
            }
            first = false;

            var row = new double[tokens.Length];
            for (int i = 0; i < tokens.Length; i++)
            {
                if (!double.TryParse(tokens[i], NumberStyles.Any, CultureInfo.InvariantCulture, out row[i]))
                    return null;
            }
            if (rows.Count == 0) width = row.Length;
            else if (row.Length != width) return null;
            rows.Add(row);
        }
        if (rows.Count == 0 || width == 0) return null;

        int height = rows.Count;
        var pixels = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            var src = rows[y];
            for (int x = 0; x < width; x++)
                pixels[y * width + x] = (byte)Math.Clamp(src[x], 0, 255);
        }

        return new VipsImage
        {
            Width = width,
            Height = height,
            Bands = 1,
            BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            Coding = VipsCoding.None,
            XRes = 1.0,
            YRes = 1.0,
            PixelsLazy = new Lazy<byte[]>(() => pixels)
        };
    }
}

/// <summary>
/// libvips Matrix file loader. Same numeric-text grid as CSV, with a leading
/// header line: <c>width height [scale] [offset]</c>. Scale/offset are
/// optional and currently ignored (we render the values directly into UChar
/// pixels — the pipeline doesn't carry an affine the way libvips does).
/// </summary>
public static class VipsMatrixLoader
{
    public static async ValueTask<VipsImage?> LoadAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        var ms = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            ms.Write(buffer, 0, read);
        }
        if (ms.Length == 0) return null;

        // Look at first non-comment line to figure out header arity (2..4 ints).
        var bytes = ms.ToArray();
        var firstLine = FirstDataLine(bytes);
        if (firstLine == null) return null;
        var tokens = firstLine.Split(new[] { ',', ' ', '\t', ';' }, StringSplitOptions.RemoveEmptyEntries);
        int headerArity = 0;
        if (tokens.Length >= 2 &&
            int.TryParse(tokens[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _) &&
            int.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            headerArity = 2;
            // Optional scale + offset at positions 2..3.
            if (tokens.Length >= 4 &&
                double.TryParse(tokens[2], NumberStyles.Any, CultureInfo.InvariantCulture, out _) &&
                double.TryParse(tokens[3], NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                headerArity = 4;
            else if (tokens.Length >= 3 &&
                double.TryParse(tokens[2], NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                headerArity = 3;
        }
        return VipsCsvLoader.Parse(bytes, headerArity);
    }

    private static string? FirstDataLine(byte[] bytes)
    {
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        foreach (var raw in text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length > 0 && !line.StartsWith('#')) return line;
        }
        return null;
    }
}
