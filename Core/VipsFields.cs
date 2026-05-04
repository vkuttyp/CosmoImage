using System;
using System.Globalization;

namespace CosmoImage.Core;

/// <summary>
/// Typed convenience accessors over <see cref="VipsImage.Metadata"/> (string
/// dictionary) and <see cref="VipsImage.MetadataBlobs"/> (byte[] dictionary).
/// Keeps the underlying storage as-is — these are pure readers/writers — but
/// hides the per-call <c>TryGetValue</c> + parse for the handful of fields
/// real callers actually want. Mirrors the spirit of libvips'
/// <c>vips_image_get_*</c> family.
///
/// All Get methods return <see langword="null"/> when the field is absent or
/// unparseable; Set methods overwrite if present and create otherwise.
/// </summary>
public static class VipsFields
{
    // ---- Strings ----
    public static string? GetString(this VipsImage image, string key)
        => image.Metadata.TryGetValue(key, out var v) ? v : null;

    public static void SetString(this VipsImage image, string key, string value)
        => image.Metadata[key] = value;

    // ---- Ints ----
    public static int? GetInt(this VipsImage image, string key)
    {
        if (!image.Metadata.TryGetValue(key, out var s)) return null;
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    public static void SetInt(this VipsImage image, string key, int value)
        => image.Metadata[key] = value.ToString(CultureInfo.InvariantCulture);

    // ---- Doubles ----
    public static double? GetDouble(this VipsImage image, string key)
    {
        if (!image.Metadata.TryGetValue(key, out var s)) return null;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    public static void SetDouble(this VipsImage image, string key, double value)
        => image.Metadata[key] = value.ToString("R", CultureInfo.InvariantCulture);

    // ---- Double arrays (e.g. GPS coords) ----
    public static double[]? GetDoubleArray(this VipsImage image, string key)
    {
        if (!image.Metadata.TryGetValue(key, out var s)) return null;
        var parts = s.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var result = new double[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out result[i]))
                return null;
        }
        return result;
    }

    public static void SetDoubleArray(this VipsImage image, string key, double[] values)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(values[i].ToString("R", CultureInfo.InvariantCulture));
        }
        image.Metadata[key] = sb.ToString();
    }

    // ---- Raw blobs ----
    public static byte[]? GetBlob(this VipsImage image, string key)
        => image.MetadataBlobs.TryGetValue(key, out var v) ? v : null;

    public static void SetBlob(this VipsImage image, string key, byte[] value)
        => image.MetadataBlobs[key] = value;

    // ---- Convenience: well-known keys ----

    /// <summary>EXIF orientation tag value (1..8) if known.</summary>
    public static int? GetOrientation(this VipsImage image) => image.GetInt("orientation");
    public static void SetOrientation(this VipsImage image, int value) => image.SetInt("orientation", value);

    /// <summary>Free-form comment (round-trips for GIF/APNG; PNG tEXt; TIFF ImageDescription).</summary>
    public static string? GetComment(this VipsImage image) => image.GetString("comment");
    public static void SetComment(this VipsImage image, string value) => image.SetString("comment", value);

    /// <summary>Per-band animation delays in 1/100 sec units (GIF/APNG/WebP).</summary>
    public static int[]? GetAnimationDelays(this VipsImage image)
    {
        var s = image.GetString("animation-delays");
        if (s == null) return null;
        var parts = s.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var result = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result[i]))
                return null;
        }
        return result;
    }

    public static void SetAnimationDelays(this VipsImage image, int[] delays)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < delays.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(delays[i].ToString(CultureInfo.InvariantCulture));
        }
        image.Metadata["animation-delays"] = sb.ToString();
    }

    public static byte[]? GetExif(this VipsImage image) => image.GetBlob("exif");
    public static byte[]? GetXmp(this VipsImage image) => image.GetBlob("xmp");
    public static byte[]? GetIccProfile(this VipsImage image) => image.GetBlob("icc");
    public static byte[]? GetIptc(this VipsImage image) => image.GetBlob("iptc");
    public static void SetExif(this VipsImage image, byte[] blob) => image.SetBlob("exif", blob);
    public static void SetXmp(this VipsImage image, byte[] blob) => image.SetBlob("xmp", blob);
    public static void SetIccProfile(this VipsImage image, byte[] blob) => image.SetBlob("icc", blob);
    public static void SetIptc(this VipsImage image, byte[] blob) => image.SetBlob("iptc", blob);

    /// <summary>
    /// Parse the image's EXIF blob into a typed
    /// <see cref="CosmoImage.Operations.Metadata.VipsExifProfile"/>.
    /// Returns <c>null</c> when no EXIF metadata is present or the blob
    /// is malformed.
    /// </summary>
    public static CosmoImage.Operations.Metadata.VipsExifProfile? GetExifProfile(this VipsImage image)
        => CosmoImage.Operations.Metadata.VipsExifProfile.TryParse(image.GetBlob("exif"));

    /// <summary>
    /// Serialize a typed
    /// <see cref="CosmoImage.Operations.Metadata.VipsExifProfile"/>
    /// back into the image's EXIF blob. Overwrites any existing data.
    /// </summary>
    public static void SetExifProfile(this VipsImage image,
        CosmoImage.Operations.Metadata.VipsExifProfile profile)
        => image.SetBlob("exif", profile.ToBytes());

    /// <summary>
    /// Parse the image's IPTC IIM blob into a typed
    /// <see cref="CosmoImage.Operations.Metadata.VipsIptcProfile"/>.
    /// Returns <c>null</c> when no IPTC metadata is present or the blob
    /// is malformed.
    /// </summary>
    public static CosmoImage.Operations.Metadata.VipsIptcProfile? GetIptcProfile(this VipsImage image)
        => CosmoImage.Operations.Metadata.VipsIptcProfile.TryParse(image.GetBlob("iptc"));

    /// <summary>
    /// Serialize a typed
    /// <see cref="CosmoImage.Operations.Metadata.VipsIptcProfile"/>
    /// back into the image's IPTC blob. Overwrites any existing data.
    /// </summary>
    public static void SetIptcProfile(this VipsImage image,
        CosmoImage.Operations.Metadata.VipsIptcProfile profile)
        => image.SetBlob("iptc", profile.ToBytes());

    /// <summary>
    /// Parse the image's ICC profile blob into a typed
    /// <see cref="CosmoImage.Operations.Metadata.VipsIccProfile"/>.
    /// Returns <c>null</c> when no ICC profile is present or the blob
    /// is malformed (no <c>"acsp"</c> magic).
    /// </summary>
    public static CosmoImage.Operations.Metadata.VipsIccProfile? GetIccProfileTyped(this VipsImage image)
        => CosmoImage.Operations.Metadata.VipsIccProfile.TryParse(image.GetBlob("icc"));

    /// <summary>
    /// Serialize a typed
    /// <see cref="CosmoImage.Operations.Metadata.VipsIccProfile"/>
    /// back into the image's ICC blob. Overwrites any existing data.
    /// </summary>
    public static void SetIccProfileTyped(this VipsImage image,
        CosmoImage.Operations.Metadata.VipsIccProfile profile)
        => image.SetBlob("icc", profile.ToBytes());

    /// <summary>
    /// Parse the image's XMP blob into a typed
    /// <see cref="CosmoImage.Operations.Metadata.VipsXmpProfile"/>
    /// (XDocument-backed). Returns <c>null</c> when no XMP is present
    /// or the blob isn't valid XML.
    /// </summary>
    public static CosmoImage.Operations.Metadata.VipsXmpProfile? GetXmpProfile(this VipsImage image)
        => CosmoImage.Operations.Metadata.VipsXmpProfile.TryParse(image.GetBlob("xmp"));

    /// <summary>
    /// Serialize a typed
    /// <see cref="CosmoImage.Operations.Metadata.VipsXmpProfile"/>
    /// back into the image's XMP blob. Overwrites any existing data.
    /// </summary>
    public static void SetXmpProfile(this VipsImage image,
        CosmoImage.Operations.Metadata.VipsXmpProfile profile)
        => image.SetBlob("xmp", profile.ToBytes());
}
