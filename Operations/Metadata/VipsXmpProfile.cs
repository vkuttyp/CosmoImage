using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace CosmoImage.Operations.Metadata;

/// <summary>
/// Typed accessor for an image's XMP metadata. Mirrors ImageSharp's
/// <c>XmpProfile</c> + ImageMagick's <c>XmpProfile</c> DOM model.
/// XMP is RDF/XML wrapped in <c>&lt;x:xmpmeta&gt;</c> + optional
/// xpacket markers; we lean on <see cref="XDocument"/> for the
/// underlying DOM and surface convenience accessors over the three
/// standard XMP value shapes:
/// <list type="bullet">
/// <item>Simple value — text inside an element</item>
/// <item>Unordered list — <c>rdf:Bag</c> with <c>rdf:li</c> children</item>
/// <item>Ordered list — <c>rdf:Seq</c> with <c>rdf:li</c> children</item>
/// <item>Language alternatives — <c>rdf:Alt</c> with <c>xml:lang</c>-tagged
///       <c>rdf:li</c> children (used for <c>dc:title</c>, <c>dc:description</c>, etc.)</item>
/// </list>
/// <para>Direct DOM access via <see cref="Document"/> for any case
/// the typed accessors don't cover (structured values, attribute
/// forms, RDF resource references).</para>
/// </summary>
public sealed class VipsXmpProfile
{
    /// <summary>Standard XMP namespace URIs.</summary>
    public static class Namespaces
    {
        public const string Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
        public const string XmpMeta = "adobe:ns:meta/";
        public const string Dc = "http://purl.org/dc/elements/1.1/";
        public const string Xmp = "http://ns.adobe.com/xap/1.0/";
        public const string Exif = "http://ns.adobe.com/exif/1.0/";
        public const string Tiff = "http://ns.adobe.com/tiff/1.0/";
        public const string Photoshop = "http://ns.adobe.com/photoshop/1.0/";
        public const string Iptc4XmpCore = "http://iptc.org/std/Iptc4xmpCore/1.0/xmlns/";
    }

    private static readonly XNamespace RdfNs = Namespaces.Rdf;
    private static readonly XNamespace XmpMetaNs = Namespaces.XmpMeta;
    private static readonly XName XmlLang = XNamespace.Xml + "lang";

    /// <summary>Underlying XML DOM. Mutate directly for advanced use.</summary>
    public XDocument Document { get; }

    public VipsXmpProfile() : this(BuildEmpty()) { }
    private VipsXmpProfile(XDocument doc) { Document = doc; }

    private static XDocument BuildEmpty()
    {
        return new XDocument(
            new XElement(XmpMetaNs + "xmpmeta",
                new XAttribute(XNamespace.Xmlns + "x", Namespaces.XmpMeta),
                new XElement(RdfNs + "RDF",
                    new XAttribute(XNamespace.Xmlns + "rdf", Namespaces.Rdf),
                    new XElement(RdfNs + "Description",
                        new XAttribute(RdfNs + "about", "")))));
    }

    // ---------- Simple property ----------

    /// <summary>
    /// Get a simple property's text. Returns <c>null</c> if not set.
    /// Checks both the element form (<c>&lt;ns:Name&gt;value&lt;/ns:Name&gt;</c>)
    /// and the attribute form (<c>rdf:Description ns:Name="value"</c>).
    /// </summary>
    public string? GetProperty(string ns, string localName)
    {
        var xname = XName.Get(localName, ns);
        foreach (var desc in EnumerateDescriptions())
        {
            var attr = desc.Attribute(xname);
            if (attr != null) return attr.Value;
            var elem = desc.Element(xname);
            if (elem != null && !elem.HasElements) return elem.Value;
        }
        return null;
    }

    /// <summary>Set a simple property. Replaces any existing value (element or attribute form).</summary>
    public void SetProperty(string ns, string localName, string value)
    {
        if (value == null) throw new ArgumentNullException(nameof(value));
        var desc = GetOrCreateDescription();
        var xname = XName.Get(localName, ns);
        // Drop any existing attribute form first so element form wins.
        desc.Attribute(xname)?.Remove();
        var existing = desc.Element(xname);
        if (existing != null) existing.Remove();
        desc.Add(new XElement(xname, value));
    }

    /// <summary>Remove a property (element or attribute form). Returns whether anything was removed.</summary>
    public bool RemoveProperty(string ns, string localName)
    {
        var xname = XName.Get(localName, ns);
        bool removed = false;
        foreach (var desc in EnumerateDescriptions().ToList())
        {
            var attr = desc.Attribute(xname);
            if (attr != null) { attr.Remove(); removed = true; }
            var elem = desc.Element(xname);
            if (elem != null) { elem.Remove(); removed = true; }
        }
        return removed;
    }

    /// <summary>True if the property is set in any form.</summary>
    public bool ContainsProperty(string ns, string localName)
        => GetProperty(ns, localName) != null
        || GetList(ns, localName).Count > 0
        || GetAltLang(ns, localName) != null;

    // ---------- Bag / Seq lists ----------

    /// <summary>
    /// Read an ordered or unordered list (handles both <c>rdf:Bag</c>
    /// and <c>rdf:Seq</c>). Returns an empty list if the property
    /// isn't a list.
    /// </summary>
    public IReadOnlyList<string> GetList(string ns, string localName)
    {
        var xname = XName.Get(localName, ns);
        foreach (var desc in EnumerateDescriptions())
        {
            var prop = desc.Element(xname);
            if (prop == null) continue;
            var container = prop.Element(RdfNs + "Bag") ?? prop.Element(RdfNs + "Seq");
            if (container == null) continue;
            return container.Elements(RdfNs + "li").Select(li => li.Value).ToList();
        }
        return Array.Empty<string>();
    }

    /// <summary>Set an unordered list (<c>rdf:Bag</c>). Replaces any existing value.</summary>
    public void SetBag(string ns, string localName, IEnumerable<string> values)
        => SetContainer(ns, localName, "Bag", values);

    /// <summary>Set an ordered list (<c>rdf:Seq</c>). Replaces any existing value.</summary>
    public void SetSeq(string ns, string localName, IEnumerable<string> values)
        => SetContainer(ns, localName, "Seq", values);

    private void SetContainer(string ns, string localName, string containerKind, IEnumerable<string> values)
    {
        if (values == null) throw new ArgumentNullException(nameof(values));
        var desc = GetOrCreateDescription();
        var xname = XName.Get(localName, ns);
        var existing = desc.Element(xname);
        if (existing != null) existing.Remove();
        var prop = new XElement(xname,
            new XElement(RdfNs + containerKind,
                values.Select(v => new XElement(RdfNs + "li", v ?? ""))));
        desc.Add(prop);
    }

    // ---------- Alt-lang ----------

    /// <summary>
    /// Read a language-alternative property's value for the given
    /// <paramref name="lang"/> code (default <c>"x-default"</c>).
    /// Used for <c>dc:title</c>, <c>dc:description</c>,
    /// <c>dc:rights</c>, etc.
    /// </summary>
    public string? GetAltLang(string ns, string localName, string lang = "x-default")
    {
        var xname = XName.Get(localName, ns);
        foreach (var desc in EnumerateDescriptions())
        {
            var prop = desc.Element(xname);
            if (prop == null) continue;
            var alt = prop.Element(RdfNs + "Alt");
            if (alt == null) continue;
            foreach (var li in alt.Elements(RdfNs + "li"))
            {
                var attr = li.Attribute(XmlLang);
                var liLang = attr?.Value ?? "x-default";
                if (liLang == lang) return li.Value;
            }
        }
        return null;
    }

    /// <summary>
    /// Set / overwrite a language alternative. Existing entries for
    /// the same language are replaced; entries for other languages
    /// are preserved.
    /// </summary>
    public void SetAltLang(string ns, string localName, string value, string lang = "x-default")
    {
        if (value == null) throw new ArgumentNullException(nameof(value));
        var desc = GetOrCreateDescription();
        var xname = XName.Get(localName, ns);
        var prop = desc.Element(xname);
        XElement alt;
        if (prop == null)
        {
            alt = new XElement(RdfNs + "Alt");
            desc.Add(new XElement(xname, alt));
        }
        else
        {
            alt = prop.Element(RdfNs + "Alt") ?? prop;
            if (alt == prop)
            {
                prop.RemoveNodes();
                alt = new XElement(RdfNs + "Alt");
                prop.Add(alt);
            }
        }
        // Drop any existing li with this lang.
        foreach (var li in alt.Elements(RdfNs + "li").ToList())
        {
            var liLang = li.Attribute(XmlLang)?.Value ?? "x-default";
            if (liLang == lang) li.Remove();
        }
        alt.Add(new XElement(RdfNs + "li", new XAttribute(XmlLang, lang), value));
    }

    // ---------- Helpers ----------

    private IEnumerable<XElement> EnumerateDescriptions()
    {
        var rdfRoot = Document.Descendants(RdfNs + "RDF").FirstOrDefault();
        if (rdfRoot == null) yield break;
        foreach (var desc in rdfRoot.Elements(RdfNs + "Description"))
            yield return desc;
    }

    private XElement GetOrCreateDescription()
    {
        var rdfRoot = Document.Descendants(RdfNs + "RDF").FirstOrDefault();
        if (rdfRoot == null)
        {
            // Replace document with the canonical empty skeleton.
            Document.RemoveNodes();
            Document.Add(BuildEmpty().Root!);
            rdfRoot = Document.Descendants(RdfNs + "RDF").First();
        }
        var desc = rdfRoot.Elements(RdfNs + "Description").FirstOrDefault();
        if (desc == null)
        {
            desc = new XElement(RdfNs + "Description", new XAttribute(RdfNs + "about", ""));
            rdfRoot.Add(desc);
        }
        return desc;
    }

    // ---------- Parse / serialize ----------

    /// <summary>
    /// Parse an XMP byte blob (UTF-8 XML, optionally wrapped in
    /// xpacket markers). Returns <c>null</c> for null / empty /
    /// malformed input.
    /// </summary>
    public static VipsXmpProfile? TryParse(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;
        try
        {
            string xml = Encoding.UTF8.GetString(bytes);
            // Strip BOM and leading whitespace before xpacket / xmpmeta.
            xml = xml.TrimStart('﻿', ' ', '\t', '\r', '\n');
            // Remove the optional <?xpacket?> wrappers — XDocument doesn't
            // accept arbitrary processing instructions outside the root.
            int packetStart = xml.IndexOf("<?xpacket", StringComparison.Ordinal);
            if (packetStart >= 0)
            {
                int piEnd = xml.IndexOf("?>", packetStart, StringComparison.Ordinal);
                if (piEnd > 0) xml = xml.Substring(piEnd + 2);
            }
            int packetEnd = xml.LastIndexOf("<?xpacket", StringComparison.Ordinal);
            if (packetEnd > 0) xml = xml.Substring(0, packetEnd);
            var doc = XDocument.Parse(xml.Trim(), LoadOptions.PreserveWhitespace);
            return new VipsXmpProfile(doc);
        }
        catch { return null; }
    }

    /// <summary>
    /// Serialize back to UTF-8 bytes wrapped in xpacket markers.
    /// Round-tripping <c>TryParse(profile.ToBytes())</c> recovers an
    /// equivalent profile (whitespace / formatting may differ).
    /// </summary>
    public byte[] ToBytes()
    {
        var sb = new StringBuilder();
        sb.Append("<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>");
        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            Indent = true,
            Encoding = new UTF8Encoding(false),
            ConformanceLevel = ConformanceLevel.Document,
        };
        using (var writer = XmlWriter.Create(sb, settings))
            Document.Save(writer);
        sb.Append("<?xpacket end=\"w\"?>");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}
