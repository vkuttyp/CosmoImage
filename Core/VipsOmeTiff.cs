using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Linq;

namespace CosmoImage.Core;

/// <summary>
/// Per-channel info parsed from an OME-XML <c>&lt;Channel&gt;</c> element.
/// Fields are nullable so absent attributes round-trip as <see langword="null"/>
/// rather than zero.
/// </summary>
public sealed class OmeChannel
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    /// <summary>RGBA color packed as 0xRRGGBBAA, the OME schema convention.</summary>
    public uint? Color { get; init; }
    public int? SamplesPerPixel { get; init; }
    public string? Fluor { get; init; }
}

/// <summary>
/// Physical pixel size from an OME-XML <c>&lt;Pixels&gt;</c> element.
/// </summary>
public sealed class OmePhysicalSize
{
    public double? X { get; init; }
    public double? Y { get; init; }
    public double? Z { get; init; }
    /// <summary>Unit string from the X axis ("µm", "mm", "nm", ...). The Y and Z units are equal in nearly every real file; we surface the X value for simplicity.</summary>
    public string? Unit { get; init; }
}

/// <summary>
/// Typed accessors for OME-XML metadata embedded in TIFF ImageDescription.
/// The loader populates <c>Metadata["ome:xml"]</c> when it detects an
/// OME-shaped XML payload; this class provides parsed views over that
/// payload for callers that want structured fields rather than raw XML.
///
/// <para>Multi-dimensional layout (Z, C, T) is intentionally not modelled —
/// the underlying <see cref="VipsImage"/> is 2D / multi-page only. Use the
/// raw XML via <see cref="GetOmeXml"/> when you need full schema access.</para>
/// </summary>
public static class VipsOmeTiff
{
    /// <summary>Best-effort detection: starts with XML prologue / element and contains an OME root element.</summary>
    public static bool LooksLikeOmeXml(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith("<")) return false;
        // OME root is "<OME ..." with the xmlns attached. Schemas vary
        // (2010-04, 2013-06, 2016-06, …) so don't pin a specific version.
        return trimmed.Contains("<OME") || trimmed.Contains(":OME ") || trimmed.Contains(":OME>");
    }

    /// <summary>True if the loader populated <c>Metadata["ome:xml"]</c> on this image.</summary>
    public static bool IsOmeTiff(VipsImage image)
        => image != null && image.Metadata.TryGetValue("ome:xml", out var v) && !string.IsNullOrEmpty(v);

    /// <summary>Returns the raw OME-XML payload, or <see langword="null"/> if absent.</summary>
    public static string? GetOmeXml(VipsImage image)
        => image != null && image.Metadata.TryGetValue("ome:xml", out var v) ? v : null;

    /// <summary>
    /// Parse the first <c>&lt;Pixels&gt;</c> element's <c>PhysicalSizeX/Y/Z</c>
    /// attributes. Returns <see langword="null"/> if the XML is absent or
    /// malformed; individual fields may be null even when the result isn't.
    /// </summary>
    public static OmePhysicalSize? GetOmePhysicalSize(VipsImage image)
    {
        var xml = GetOmeXml(image);
        if (xml == null) return null;
        try
        {
            var doc = XDocument.Parse(xml);
            var pixels = FindFirst(doc.Root, "Pixels");
            if (pixels == null) return null;
            return new OmePhysicalSize
            {
                X = ParseDouble(pixels.Attribute("PhysicalSizeX")?.Value),
                Y = ParseDouble(pixels.Attribute("PhysicalSizeY")?.Value),
                Z = ParseDouble(pixels.Attribute("PhysicalSizeZ")?.Value),
                Unit = pixels.Attribute("PhysicalSizeXUnit")?.Value,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parse all <c>&lt;Channel&gt;</c> elements into typed records, in
    /// document order. Returns an empty array when the XML lacks channels;
    /// returns <see langword="null"/> when the XML is malformed or absent.
    /// </summary>
    public static OmeChannel[]? GetOmeChannels(VipsImage image)
    {
        var xml = GetOmeXml(image);
        if (xml == null) return null;
        try
        {
            var doc = XDocument.Parse(xml);
            var channels = new List<OmeChannel>();
            foreach (var ch in FindAll(doc.Root, "Channel"))
            {
                channels.Add(new OmeChannel
                {
                    Id = ch.Attribute("ID")?.Value,
                    Name = ch.Attribute("Name")?.Value,
                    Color = ParseUInt(ch.Attribute("Color")?.Value),
                    SamplesPerPixel = ParseInt(ch.Attribute("SamplesPerPixel")?.Value),
                    Fluor = ch.Attribute("Fluor")?.Value,
                });
            }
            return channels.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Set <see cref="VipsImage.XRes"/> and <see cref="VipsImage.YRes"/> from
    /// OME PhysicalSizeX/Y. libvips uses pixels-per-millimeter, so we convert
    /// based on the unit attribute. Called by the TIFF loader when it
    /// detects OME-XML; safe to call directly for round-trip pipelines.
    /// </summary>
    public static void PopulatePhysicalSize(VipsImage image)
    {
        var sz = GetOmePhysicalSize(image);
        if (sz == null) return;

        // PhysicalSizeX/Y is "size of one pixel". libvips XRes/YRes is
        // "pixels per mm". invert + unit convert. Default unit for OME is µm.
        double mmPerUnit = (sz.Unit ?? "µm") switch
        {
            "mm" => 1.0,
            "cm" => 10.0,
            "m"  => 1000.0,
            "nm" => 1e-6,
            "Å" or "A" or "angstrom" => 1e-7,
            "µm" or "micrometer" or "um" => 1e-3,
            _ => 1e-3, // assume µm if unrecognised — by far the OME default
        };

        if (sz.X is double sx && sx > 0)
            image.XRes = 1.0 / (sx * mmPerUnit);
        if (sz.Y is double sy && sy > 0)
            image.YRes = 1.0 / (sy * mmPerUnit);
    }

    // ---- XML helpers (OME-XML uses a default namespace; ignore it for
    // local-name lookups so we work across schema versions) ----

    private static XElement? FindFirst(XElement? root, string localName)
    {
        if (root == null) return null;
        foreach (var el in root.DescendantsAndSelf())
            if (el.Name.LocalName == localName) return el;
        return null;
    }

    private static IEnumerable<XElement> FindAll(XElement? root, string localName)
    {
        if (root == null) yield break;
        foreach (var el in root.DescendantsAndSelf())
            if (el.Name.LocalName == localName) yield return el;
    }

    private static double? ParseDouble(string? s)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    private static int? ParseInt(string? s)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    private static uint? ParseUInt(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        // OME Color is sometimes signed Int32 in the schema (<= 2013) and
        // sometimes UInt32 (≥ 2016). Accept both via long parse.
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
            return (uint)l;
        return null;
    }
}
