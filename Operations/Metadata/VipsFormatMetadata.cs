using System.Collections.Generic;
using System.Linq;
using CosmoImage.Core;

namespace CosmoImage.Operations.Metadata;

/// <summary>
/// Typed accessors for format-specific metadata that doesn't fit
/// the EXIF / IPTC / ICC / XMP profile model — PNG text chunks,
/// JPEG comments, GIF comments. Mirrors ImageSharp's
/// <c>image.Metadata.GetPngMetadata().TextData</c> family.
///
/// <para>Storage: namespaced keys on <see cref="VipsImage.Metadata"/>.
/// PNG text chunks live under <c>"png:text:&lt;keyword&gt;"</c>;
/// the JPEG comment is <c>"jpeg:comment"</c>; the GIF comment is
/// <c>"gif:comment"</c>. Loaders that extract these populate the
/// keys; savers that emit these check for them. Users who construct
/// images programmatically attach the metadata via these helpers
/// before saving.</para>
/// </summary>
public static class VipsFormatMetadataExtensions
{
    /// <summary>Standard PNG <c>tEXt</c> / <c>iTXt</c> keyword conventions (from the PNG spec).</summary>
    public static class PngTextKeywords
    {
        public const string Title = "Title";
        public const string Author = "Author";
        public const string Description = "Description";
        public const string Copyright = "Copyright";
        public const string CreationTime = "Creation Time";
        public const string Software = "Software";
        public const string Disclaimer = "Disclaimer";
        public const string Warning = "Warning";
        public const string Source = "Source";
        public const string Comment = "Comment";
    }

    private const string PngTextPrefix = "png:text:";
    private const string JpegCommentKey = "jpeg:comment";
    private const string GifCommentKey = "gif:comment";

    // ---- PNG text chunks (tEXt / iTXt / zTXt) ----

    /// <summary>
    /// Get a PNG text chunk's value by keyword. Returns <c>null</c>
    /// if the keyword isn't set. PNG keywords are case-sensitive
    /// per the spec.
    /// </summary>
    public static string? GetPngText(this VipsImage image, string keyword)
    {
        if (string.IsNullOrEmpty(keyword)) return null;
        return image.Metadata.TryGetValue(PngTextPrefix + keyword, out var v) ? v : null;
    }

    /// <summary>
    /// Set a PNG text chunk. Both <c>tEXt</c> (ASCII) and <c>iTXt</c>
    /// (UTF-8) are written by savers as appropriate based on the
    /// content of <paramref name="value"/>.
    /// </summary>
    public static void SetPngText(this VipsImage image, string keyword, string value)
    {
        if (string.IsNullOrEmpty(keyword))
            throw new System.ArgumentException("PNG text keyword must be non-empty", nameof(keyword));
        if (value == null) throw new System.ArgumentNullException(nameof(value));
        image.Metadata[PngTextPrefix + keyword] = value;
    }

    /// <summary>Remove a PNG text chunk by keyword. Returns whether it was set.</summary>
    public static bool RemovePngText(this VipsImage image, string keyword)
        => keyword != null && image.Metadata.Remove(PngTextPrefix + keyword);

    /// <summary>All PNG text chunks currently set, as a keyword → value map.</summary>
    public static IReadOnlyDictionary<string, string> GetAllPngText(this VipsImage image)
    {
        var result = new Dictionary<string, string>();
        foreach (var kv in image.Metadata)
        {
            if (kv.Key.StartsWith(PngTextPrefix, System.StringComparison.Ordinal))
                result[kv.Key.Substring(PngTextPrefix.Length)] = kv.Value;
        }
        return result;
    }

    /// <summary>Replace ALL PNG text chunks with the given dictionary.</summary>
    public static void SetAllPngText(this VipsImage image, IEnumerable<KeyValuePair<string, string>> entries)
    {
        if (entries == null) throw new System.ArgumentNullException(nameof(entries));
        // Drop any existing png:text:* entries.
        var existing = image.Metadata.Keys
            .Where(k => k.StartsWith(PngTextPrefix, System.StringComparison.Ordinal)).ToList();
        foreach (var k in existing) image.Metadata.Remove(k);
        // Add new ones.
        foreach (var kv in entries)
            image.Metadata[PngTextPrefix + kv.Key] = kv.Value;
    }

    // ---- JPEG comment ----

    /// <summary>
    /// Get the JPEG <c>COM</c> marker comment, or <c>null</c> if absent.
    /// </summary>
    public static string? GetJpegComment(this VipsImage image)
        => image.Metadata.TryGetValue(JpegCommentKey, out var v) ? v : null;

    /// <summary>Set the JPEG <c>COM</c> marker comment.</summary>
    public static void SetJpegComment(this VipsImage image, string value)
    {
        if (value == null) throw new System.ArgumentNullException(nameof(value));
        image.Metadata[JpegCommentKey] = value;
    }

    /// <summary>Remove the JPEG comment. Returns whether it was set.</summary>
    public static bool RemoveJpegComment(this VipsImage image)
        => image.Metadata.Remove(JpegCommentKey);

    // ---- GIF comment ----

    /// <summary>Get the GIF Comment Extension content, or <c>null</c> if absent.</summary>
    public static string? GetGifComment(this VipsImage image)
        => image.Metadata.TryGetValue(GifCommentKey, out var v) ? v : null;

    /// <summary>Set the GIF Comment Extension content.</summary>
    public static void SetGifComment(this VipsImage image, string value)
    {
        if (value == null) throw new System.ArgumentNullException(nameof(value));
        image.Metadata[GifCommentKey] = value;
    }

    /// <summary>Remove the GIF comment. Returns whether it was set.</summary>
    public static bool RemoveGifComment(this VipsImage image)
        => image.Metadata.Remove(GifCommentKey);
}
