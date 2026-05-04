using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CosmoImage.Operations.Metadata;

/// <summary>
/// IPTC IIM Application Record (record 2) tag identifiers — the
/// common subset for image metadata (title, keywords, byline,
/// location, copyright, caption). Repeatable tags are documented
/// inline.
/// </summary>
public enum VipsIptcTag : byte
{
    /// <summary>Title / object name (max 64 chars).</summary>
    ObjectName = 5,
    /// <summary>Editorial urgency 1..8.</summary>
    Urgency = 10,
    /// <summary>Category code (3 chars).</summary>
    Category = 15,
    /// <summary>Supplemental category (repeatable, max 32 chars each).</summary>
    SupplementalCategory = 20,
    /// <summary>Keywords (repeatable, max 64 chars each).</summary>
    Keywords = 25,
    /// <summary>Special instructions (max 256 chars).</summary>
    SpecialInstructions = 40,
    /// <summary>Date created (CCYYMMDD).</summary>
    DateCreated = 55,
    /// <summary>Time created (HHMMSS±HHMM).</summary>
    TimeCreated = 60,
    /// <summary>Photographer / byline (repeatable, max 32 chars each).</summary>
    Byline = 80,
    /// <summary>Byline title (repeatable, max 32 chars each).</summary>
    BylineTitle = 85,
    /// <summary>City (max 32 chars).</summary>
    City = 90,
    /// <summary>Sub-location (max 32 chars).</summary>
    SubLocation = 92,
    /// <summary>Province / state (max 32 chars).</summary>
    ProvinceState = 95,
    /// <summary>Country code (ISO 3166, 3 chars).</summary>
    CountryPrimaryLocationCode = 100,
    /// <summary>Country name (max 64 chars).</summary>
    CountryPrimaryLocationName = 101,
    /// <summary>Original transmission reference (max 32 chars).</summary>
    OriginalTransmissionReference = 103,
    /// <summary>Headline (max 256 chars).</summary>
    Headline = 105,
    /// <summary>Credit (max 32 chars).</summary>
    Credit = 110,
    /// <summary>Source (max 32 chars).</summary>
    Source = 115,
    /// <summary>Copyright notice (max 128 chars).</summary>
    CopyrightNotice = 116,
    /// <summary>Contact (repeatable, max 128 chars each).</summary>
    Contact = 118,
    /// <summary>Caption / abstract (max 2000 chars).</summary>
    Caption = 120,
    /// <summary>Writer / editor (repeatable, max 32 chars each).</summary>
    WriterEditor = 122,
}

/// <summary>
/// Typed accessor for an image's IPTC IIM Application Record metadata.
/// Mirrors ImageSharp's <c>IptcProfile</c>. Parses the IIM tag-stream
/// format (each entry: <c>0x1C</c> marker, record, dataset, big-endian
/// length, value bytes), supports repeatable tags as lists, round-trips
/// to UTF-8 strings, and serialises back to a valid IIM byte stream.
///
/// <para>The blob this works on is the raw Application-Record bytes —
/// what's typically stored inside Photoshop 8BIM resource block
/// 0x0404 in JPEGs. Format-specific extraction (JPEG APP13 / 8BIM
/// unwrapping) is deferred to a later round; this round provides the
/// parser/serializer and the <see cref="VipsImage.GetIptcProfile"/>
/// bridge so users can attach typed metadata directly.</para>
/// </summary>
public sealed class VipsIptcProfile
{
    private readonly Dictionary<VipsIptcTag, List<string>> _values = new();

    private const byte TagMarker = 0x1C;
    private const byte ApplicationRecord = 2;

    /// <summary>True if this tag has at least one value.</summary>
    public bool Contains(VipsIptcTag tag) => _values.ContainsKey(tag);

    /// <summary>All tags with at least one value.</summary>
    public IEnumerable<VipsIptcTag> Tags => _values.Keys;

    /// <summary>First value for a tag, or <c>null</c> if not set.</summary>
    public string? GetValue(VipsIptcTag tag)
        => _values.TryGetValue(tag, out var list) && list.Count > 0 ? list[0] : null;

    /// <summary>
    /// All values for a tag (empty list if not set). Repeatable tags
    /// like <see cref="VipsIptcTag.Keywords"/> can have many.
    /// </summary>
    public IReadOnlyList<string> GetValues(VipsIptcTag tag)
        => _values.TryGetValue(tag, out var list) ? list : Array.Empty<string>();

    /// <summary>Set a single value, replacing any existing entries for the tag.</summary>
    public void SetValue(VipsIptcTag tag, string value)
    {
        if (value == null) throw new ArgumentNullException(nameof(value));
        _values[tag] = new List<string> { value };
    }

    /// <summary>Replace all values for a (typically repeatable) tag.</summary>
    public void SetValues(VipsIptcTag tag, IEnumerable<string> values)
    {
        if (values == null) throw new ArgumentNullException(nameof(values));
        _values[tag] = values.Where(v => v != null).ToList();
    }

    /// <summary>Append a value (intended for repeatable tags like Keywords).</summary>
    public void Add(VipsIptcTag tag, string value)
    {
        if (value == null) throw new ArgumentNullException(nameof(value));
        if (!_values.TryGetValue(tag, out var list))
        {
            list = new List<string>();
            _values[tag] = list;
        }
        list.Add(value);
    }

    /// <summary>Remove all values for a tag. Returns whether the tag was set.</summary>
    public bool Remove(VipsIptcTag tag) => _values.Remove(tag);

    // ---------- Parsing ----------

    /// <summary>
    /// Parse a raw IIM byte stream. Returns <c>null</c> for null /
    /// empty / malformed input. Skips entries from records other than
    /// the Application Record (record 2).
    /// </summary>
    public static VipsIptcProfile? TryParse(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;
        try
        {
            var profile = new VipsIptcProfile();
            int p = 0;
            while (p + 5 <= bytes.Length)
            {
                if (bytes[p] != TagMarker) return null;
                byte record = bytes[p + 1];
                byte dataset = bytes[p + 2];
                int len = (bytes[p + 3] << 8) | bytes[p + 4];
                int valueStart;
                int valueLen;
                // Extended-length form: high bit of length-MSB set, then
                // the next 4 bytes are the actual length (32-bit BE).
                if ((bytes[p + 3] & 0x80) != 0)
                {
                    int extLenBytes = ((bytes[p + 3] & 0x7F) << 8) | bytes[p + 4];
                    if (extLenBytes != 4) return null;  // only 4-byte extended supported
                    if (p + 9 > bytes.Length) return null;
                    valueLen = (bytes[p + 5] << 24) | (bytes[p + 6] << 16)
                             | (bytes[p + 7] << 8) | bytes[p + 8];
                    valueStart = p + 9;
                }
                else
                {
                    valueLen = len;
                    valueStart = p + 5;
                }
                if (valueStart + valueLen > bytes.Length) return null;

                if (record == ApplicationRecord && Enum.IsDefined(typeof(VipsIptcTag), dataset))
                {
                    var tag = (VipsIptcTag)dataset;
                    var s = Encoding.UTF8.GetString(bytes, valueStart, valueLen);
                    if (!profile._values.TryGetValue(tag, out var list))
                    {
                        list = new List<string>();
                        profile._values[tag] = list;
                    }
                    list.Add(s);
                }
                p = valueStart + valueLen;
            }
            // Empty profile is still valid; fail only if we were given
            // outright junk.
            return p == bytes.Length ? profile : null;
        }
        catch { return null; }
    }

    // ---------- Serialization ----------

    /// <summary>
    /// Serialise this profile to the raw IIM byte stream. Round-tripping
    /// <c>TryParse(profile.ToBytes())</c> recovers an equivalent profile.
    /// Tags are emitted in ascending dataset order; repeatable values
    /// preserve their insertion order.
    /// </summary>
    public byte[] ToBytes()
    {
        var stream = new MemoryStream();
        foreach (var tag in _values.Keys.OrderBy(t => (byte)t))
        {
            foreach (var value in _values[tag])
            {
                var encoded = Encoding.UTF8.GetBytes(value);
                stream.WriteByte(TagMarker);
                stream.WriteByte(ApplicationRecord);
                stream.WriteByte((byte)tag);
                if (encoded.Length <= 0x7FFF)
                {
                    stream.WriteByte((byte)((encoded.Length >> 8) & 0xFF));
                    stream.WriteByte((byte)(encoded.Length & 0xFF));
                }
                else
                {
                    // Extended: high bit + 0x4 in first 2 bytes, then 4-byte BE length.
                    stream.WriteByte(0x80);
                    stream.WriteByte(0x04);
                    stream.WriteByte((byte)((encoded.Length >> 24) & 0xFF));
                    stream.WriteByte((byte)((encoded.Length >> 16) & 0xFF));
                    stream.WriteByte((byte)((encoded.Length >> 8) & 0xFF));
                    stream.WriteByte((byte)(encoded.Length & 0xFF));
                }
                stream.Write(encoded, 0, encoded.Length);
            }
        }
        return stream.ToArray();
    }
}
