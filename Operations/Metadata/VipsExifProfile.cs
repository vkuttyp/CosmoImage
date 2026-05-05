using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CosmoImage.Operations.Metadata;

/// <summary>
/// EXIF tag identifiers — the common subset useful to most workflows
/// (orientation, datetime, camera info, exposure parameters, dimensions).
/// Tag IDs match the EXIF specification.
/// </summary>
public enum VipsExifTag : ushort
{
    // ---- IFD0 (TIFF baseline) ----
    ImageDescription = 0x010E,
    Make = 0x010F,
    Model = 0x0110,
    Orientation = 0x0112,
    XResolution = 0x011A,
    YResolution = 0x011B,
    ResolutionUnit = 0x0128,
    Software = 0x0131,
    DateTime = 0x0132,
    Artist = 0x013B,
    Copyright = 0x8298,

    // ---- Exif sub-IFD (private) ----
    ExposureTime = 0x829A,
    FNumber = 0x829D,
    ISOSpeedRatings = 0x8827,
    DateTimeOriginal = 0x9003,
    DateTimeDigitized = 0x9004,
    UserComment = 0x9286,
    FocalLength = 0x920A,
    ColorSpace = 0xA001,
    PixelXDimension = 0xA002,
    PixelYDimension = 0xA003,
    LensMake = 0xA433,
    LensModel = 0xA434,
}

/// <summary>
/// GPS sub-IFD tag identifiers (EXIF specification, GPS Info IFD).
/// Tag IDs are a separate namespace from <see cref="VipsExifTag"/> —
/// the same numeric IDs collide between GPS and TIFF, so GPS tags
/// have their own enum + their own accessor methods on
/// <see cref="VipsExifProfile"/>.
/// </summary>
public enum VipsGpsTag : ushort
{
    /// <summary>Byte[4] — typically {2, 2, 0, 0} for v2.2.0.0.</summary>
    VersionID = 0x0000,
    /// <summary>2-char ASCII — "N" or "S" plus terminator.</summary>
    LatitudeRef = 0x0001,
    /// <summary>3 rationals — degrees, minutes, seconds.</summary>
    Latitude = 0x0002,
    /// <summary>2-char ASCII — "E" or "W" plus terminator.</summary>
    LongitudeRef = 0x0003,
    /// <summary>3 rationals — degrees, minutes, seconds.</summary>
    Longitude = 0x0004,
    /// <summary>Single byte — 0=above sea level, 1=below.</summary>
    AltitudeRef = 0x0005,
    /// <summary>Rational — altitude in metres.</summary>
    Altitude = 0x0006,
    /// <summary>3 rationals — UTC hour / minute / second.</summary>
    TimeStamp = 0x0007,
    Satellites = 0x0008,
    /// <summary>"A" (active) or "V" (void).</summary>
    Status = 0x0009,
    /// <summary>"2" (2D) or "3" (3D).</summary>
    MeasureMode = 0x000A,
    Dop = 0x000B,
    SpeedRef = 0x000C,
    Speed = 0x000D,
    TrackRef = 0x000E,
    Track = 0x000F,
    ImgDirectionRef = 0x0010,
    ImgDirection = 0x0011,
    MapDatum = 0x0012,
    DestLatitudeRef = 0x0013,
    DestLatitude = 0x0014,
    DestLongitudeRef = 0x0015,
    DestLongitude = 0x0016,
    DestBearingRef = 0x0017,
    DestBearing = 0x0018,
    DestDistanceRef = 0x0019,
    DestDistance = 0x001A,
    ProcessingMethod = 0x001B,
    AreaInformation = 0x001C,
    /// <summary>11-char ASCII — "YYYY:MM:DD" UTC.</summary>
    DateStamp = 0x001D,
}

/// <summary>EXIF / TIFF data type codes (per the spec).</summary>
public enum VipsExifType : ushort
{
    Byte = 1,
    Ascii = 2,
    Short = 3,
    Long = 4,
    Rational = 5,
    SByte = 6,
    Undefined = 7,
    SShort = 8,
    SLong = 9,
    SRational = 10,
    Float = 11,
    Double = 12,
}

/// <summary>EXIF rational — pair of unsigned 32-bit integers (numerator / denominator).</summary>
public readonly record struct VipsExifRational(uint Numerator, uint Denominator)
{
    public double ToDouble() => Denominator == 0 ? 0 : (double)Numerator / Denominator;
    public override string ToString() => $"{Numerator}/{Denominator}";
}

/// <summary>EXIF signed rational — pair of signed 32-bit integers.</summary>
public readonly record struct VipsExifSRational(int Numerator, int Denominator)
{
    public double ToDouble() => Denominator == 0 ? 0 : (double)Numerator / Denominator;
    public override string ToString() => $"{Numerator}/{Denominator}";
}

/// <summary>
/// Typed accessor for an image's EXIF metadata. Mirrors ImageSharp's
/// <c>ExifProfile</c>. Parse a raw EXIF blob (TIFF-format bytes — what
/// CosmoImage already round-trips via <c>image.GetExif()</c>) into a
/// dictionary of typed values; serialize back with <see cref="ToBytes"/>.
///
/// <para>Supports the most common tags across IFD0, the Exif
/// sub-IFD, and the GPS sub-IFD (Latitude / Longitude / Altitude /
/// Timestamp / Direction / etc., all under <see cref="VipsGpsTag"/>).</para>
///
/// <para>Both byte orders (II = little-endian; MM = big-endian) are
/// supported. The byte order of a parsed profile is preserved on
/// serialization unless overridden via <see cref="BigEndian"/>.</para>
/// </summary>
public sealed class VipsExifProfile
{
    private readonly Dictionary<VipsExifTag, object> _values = new();
    private readonly Dictionary<VipsGpsTag, object> _gpsValues = new();

    /// <summary>Byte order for serialization. Default <c>false</c> = little-endian (II).</summary>
    public bool BigEndian { get; set; } = false;

    /// <summary>True if the tag has a value set.</summary>
    public bool Contains(VipsExifTag tag) => _values.ContainsKey(tag);

    /// <summary>Remove a tag. Returns whether it was present.</summary>
    public bool Remove(VipsExifTag tag) => _values.Remove(tag);

    /// <summary>All tags currently set on this profile.</summary>
    public IEnumerable<VipsExifTag> Tags => _values.Keys;

    /// <summary>
    /// Get a tag's value, attempting to convert to <typeparamref name="T"/>.
    /// Returns <c>default(T)</c> if the tag isn't present or the
    /// stored value can't be coerced.
    /// </summary>
    public T? GetValue<T>(VipsExifTag tag)
    {
        if (!_values.TryGetValue(tag, out var raw)) return default;
        if (raw is T t) return t;
        try { return (T)System.Convert.ChangeType(raw, typeof(T))!; }
        catch { return default; }
    }

    /// <summary>Get the raw stored value (boxed). Useful for unknown types.</summary>
    public object? GetRaw(VipsExifTag tag)
        => _values.TryGetValue(tag, out var v) ? v : null;

    /// <summary>Set a tag's value. The runtime type drives how it serializes.</summary>
    public void SetValue<T>(VipsExifTag tag, T value) where T : notnull
        => _values[tag] = value;

    // ---- GPS accessors (parallel API for the GPS sub-IFD) ----

    /// <summary>True if the GPS tag has a value set.</summary>
    public bool ContainsGps(VipsGpsTag tag) => _gpsValues.ContainsKey(tag);
    /// <summary>Remove a GPS tag. Returns whether it was present.</summary>
    public bool RemoveGps(VipsGpsTag tag) => _gpsValues.Remove(tag);
    /// <summary>All GPS tags currently set on this profile.</summary>
    public IEnumerable<VipsGpsTag> GpsTags => _gpsValues.Keys;

    /// <summary>Get a GPS tag's value, attempting to convert to <typeparamref name="T"/>.</summary>
    public T? GetGpsValue<T>(VipsGpsTag tag)
    {
        if (!_gpsValues.TryGetValue(tag, out var raw)) return default;
        if (raw is T t) return t;
        try { return (T)System.Convert.ChangeType(raw, typeof(T))!; }
        catch { return default; }
    }

    /// <summary>Get the raw stored GPS value (boxed).</summary>
    public object? GetGpsRaw(VipsGpsTag tag)
        => _gpsValues.TryGetValue(tag, out var v) ? v : null;

    /// <summary>Set a GPS tag's value.</summary>
    public void SetGpsValue<T>(VipsGpsTag tag, T value) where T : notnull
        => _gpsValues[tag] = value;

    /// <summary>
    /// Decode latitude / longitude as decimal degrees from
    /// <see cref="VipsGpsTag.Latitude"/> + <see cref="VipsGpsTag.LatitudeRef"/>
    /// + <see cref="VipsGpsTag.Longitude"/> + <see cref="VipsGpsTag.LongitudeRef"/>.
    /// Returns <c>null</c> if any of the four are missing.
    /// </summary>
    public (double latitude, double longitude)? GetLocation()
    {
        var lat = _gpsValues.TryGetValue(VipsGpsTag.Latitude, out var lv) ? lv as VipsExifRational[] : null;
        var lon = _gpsValues.TryGetValue(VipsGpsTag.Longitude, out var nv) ? nv as VipsExifRational[] : null;
        if (lat == null || lat.Length < 3 || lon == null || lon.Length < 3) return null;
        var latRef = (_gpsValues.TryGetValue(VipsGpsTag.LatitudeRef, out var lr) ? lr as string : null)
                     ?? "N";
        var lonRef = (_gpsValues.TryGetValue(VipsGpsTag.LongitudeRef, out var nr) ? nr as string : null)
                     ?? "E";
        double latDeg = DmsToDegrees(lat) * (latRef.StartsWith("S") ? -1 : 1);
        double lonDeg = DmsToDegrees(lon) * (lonRef.StartsWith("W") ? -1 : 1);
        return (latDeg, lonDeg);
    }

    /// <summary>
    /// Encode <paramref name="latitude"/> / <paramref name="longitude"/>
    /// as decimal degrees into the four GPS tags
    /// (<see cref="VipsGpsTag.Latitude"/> / <see cref="VipsGpsTag.LatitudeRef"/>
    /// / <see cref="VipsGpsTag.Longitude"/> / <see cref="VipsGpsTag.LongitudeRef"/>).
    /// </summary>
    public void SetLocation(double latitude, double longitude)
    {
        if (latitude < -90 || latitude > 90) throw new ArgumentOutOfRangeException(nameof(latitude));
        if (longitude < -180 || longitude > 180) throw new ArgumentOutOfRangeException(nameof(longitude));
        _gpsValues[VipsGpsTag.LatitudeRef] = latitude < 0 ? "S" : "N";
        _gpsValues[VipsGpsTag.Latitude] = DegreesToDms(Math.Abs(latitude));
        _gpsValues[VipsGpsTag.LongitudeRef] = longitude < 0 ? "W" : "E";
        _gpsValues[VipsGpsTag.Longitude] = DegreesToDms(Math.Abs(longitude));
    }

    private static double DmsToDegrees(VipsExifRational[] dms)
        => dms[0].ToDouble() + dms[1].ToDouble() / 60 + dms[2].ToDouble() / 3600;

    private static VipsExifRational[] DegreesToDms(double degrees)
    {
        int deg = (int)Math.Floor(degrees);
        double minDecimal = (degrees - deg) * 60;
        int min = (int)Math.Floor(minDecimal);
        double secDecimal = (minDecimal - min) * 60;
        // Store seconds as a rational with 1e6 denominator for sub-second precision.
        const uint secScale = 1_000_000;
        uint secNum = (uint)Math.Round(secDecimal * secScale);
        return new[]
        {
            new VipsExifRational((uint)deg, 1),
            new VipsExifRational((uint)min, 1),
            new VipsExifRational(secNum, secScale),
        };
    }

    // ---- Tag → IFD / type tables ----

    private static readonly Dictionary<VipsExifTag, int> TagIfd = new()
    {
        [VipsExifTag.ImageDescription] = 0,
        [VipsExifTag.Make] = 0,
        [VipsExifTag.Model] = 0,
        [VipsExifTag.Orientation] = 0,
        [VipsExifTag.XResolution] = 0,
        [VipsExifTag.YResolution] = 0,
        [VipsExifTag.ResolutionUnit] = 0,
        [VipsExifTag.Software] = 0,
        [VipsExifTag.DateTime] = 0,
        [VipsExifTag.Artist] = 0,
        [VipsExifTag.Copyright] = 0,
        [VipsExifTag.ExposureTime] = 1,
        [VipsExifTag.FNumber] = 1,
        [VipsExifTag.ISOSpeedRatings] = 1,
        [VipsExifTag.DateTimeOriginal] = 1,
        [VipsExifTag.DateTimeDigitized] = 1,
        [VipsExifTag.UserComment] = 1,
        [VipsExifTag.FocalLength] = 1,
        [VipsExifTag.ColorSpace] = 1,
        [VipsExifTag.PixelXDimension] = 1,
        [VipsExifTag.PixelYDimension] = 1,
        [VipsExifTag.LensMake] = 1,
        [VipsExifTag.LensModel] = 1,
    };

    private static readonly Dictionary<VipsExifTag, VipsExifType> TagType = new()
    {
        [VipsExifTag.ImageDescription] = VipsExifType.Ascii,
        [VipsExifTag.Make] = VipsExifType.Ascii,
        [VipsExifTag.Model] = VipsExifType.Ascii,
        [VipsExifTag.Orientation] = VipsExifType.Short,
        [VipsExifTag.XResolution] = VipsExifType.Rational,
        [VipsExifTag.YResolution] = VipsExifType.Rational,
        [VipsExifTag.ResolutionUnit] = VipsExifType.Short,
        [VipsExifTag.Software] = VipsExifType.Ascii,
        [VipsExifTag.DateTime] = VipsExifType.Ascii,
        [VipsExifTag.Artist] = VipsExifType.Ascii,
        [VipsExifTag.Copyright] = VipsExifType.Ascii,
        [VipsExifTag.ExposureTime] = VipsExifType.Rational,
        [VipsExifTag.FNumber] = VipsExifType.Rational,
        [VipsExifTag.ISOSpeedRatings] = VipsExifType.Short,
        [VipsExifTag.DateTimeOriginal] = VipsExifType.Ascii,
        [VipsExifTag.DateTimeDigitized] = VipsExifType.Ascii,
        [VipsExifTag.UserComment] = VipsExifType.Undefined,
        [VipsExifTag.FocalLength] = VipsExifType.Rational,
        [VipsExifTag.ColorSpace] = VipsExifType.Short,
        [VipsExifTag.PixelXDimension] = VipsExifType.Long,
        [VipsExifTag.PixelYDimension] = VipsExifType.Long,
        [VipsExifTag.LensMake] = VipsExifType.Ascii,
        [VipsExifTag.LensModel] = VipsExifType.Ascii,
    };

    private const ushort ExifIfdPointerTag = 0x8769;
    private const ushort GpsIfdPointerTag = 0x8825;

    private static readonly Dictionary<VipsGpsTag, VipsExifType> GpsTagType = new()
    {
        [VipsGpsTag.VersionID] = VipsExifType.Byte,
        [VipsGpsTag.LatitudeRef] = VipsExifType.Ascii,
        [VipsGpsTag.Latitude] = VipsExifType.Rational,
        [VipsGpsTag.LongitudeRef] = VipsExifType.Ascii,
        [VipsGpsTag.Longitude] = VipsExifType.Rational,
        [VipsGpsTag.AltitudeRef] = VipsExifType.Byte,
        [VipsGpsTag.Altitude] = VipsExifType.Rational,
        [VipsGpsTag.TimeStamp] = VipsExifType.Rational,
        [VipsGpsTag.Satellites] = VipsExifType.Ascii,
        [VipsGpsTag.Status] = VipsExifType.Ascii,
        [VipsGpsTag.MeasureMode] = VipsExifType.Ascii,
        [VipsGpsTag.Dop] = VipsExifType.Rational,
        [VipsGpsTag.SpeedRef] = VipsExifType.Ascii,
        [VipsGpsTag.Speed] = VipsExifType.Rational,
        [VipsGpsTag.TrackRef] = VipsExifType.Ascii,
        [VipsGpsTag.Track] = VipsExifType.Rational,
        [VipsGpsTag.ImgDirectionRef] = VipsExifType.Ascii,
        [VipsGpsTag.ImgDirection] = VipsExifType.Rational,
        [VipsGpsTag.MapDatum] = VipsExifType.Ascii,
        [VipsGpsTag.DestLatitudeRef] = VipsExifType.Ascii,
        [VipsGpsTag.DestLatitude] = VipsExifType.Rational,
        [VipsGpsTag.DestLongitudeRef] = VipsExifType.Ascii,
        [VipsGpsTag.DestLongitude] = VipsExifType.Rational,
        [VipsGpsTag.DestBearingRef] = VipsExifType.Ascii,
        [VipsGpsTag.DestBearing] = VipsExifType.Rational,
        [VipsGpsTag.DestDistanceRef] = VipsExifType.Ascii,
        [VipsGpsTag.DestDistance] = VipsExifType.Rational,
        [VipsGpsTag.ProcessingMethod] = VipsExifType.Undefined,
        [VipsGpsTag.AreaInformation] = VipsExifType.Undefined,
        [VipsGpsTag.DateStamp] = VipsExifType.Ascii,
    };

    // ---------- Parsing ----------

    /// <summary>
    /// Parse a raw EXIF byte blob (TIFF format — what
    /// <c>image.GetExif()</c> returns). Returns <c>null</c> on
    /// malformed input.
    /// </summary>
    public static VipsExifProfile? TryParse(byte[]? bytes)
    {
        if (bytes == null || bytes.Length < 8) return null;
        try { return Parse(bytes); }
        catch { return null; }
    }

    private static VipsExifProfile Parse(byte[] bytes)
    {
        bool be;
        if (bytes[0] == 0x49 && bytes[1] == 0x49) be = false;
        else if (bytes[0] == 0x4D && bytes[1] == 0x4D) be = true;
        else throw new InvalidOperationException("Bad TIFF byte-order marker");

        ushort magic = ReadU16(bytes, 2, be);
        if (magic != 0x002A) throw new InvalidOperationException("Bad TIFF magic");
        uint ifd0Offset = ReadU32(bytes, 4, be);

        var profile = new VipsExifProfile { BigEndian = be };
        ReadIfd(bytes, (int)ifd0Offset, be, profile, isExifSub: false);
        return profile;
    }

    private static void ReadIfd(byte[] bytes, int offset, bool be, VipsExifProfile profile, bool isExifSub)
    {
        if (offset < 0 || offset + 2 > bytes.Length) return;
        ushort numEntries = ReadU16(bytes, offset, be);
        int p = offset + 2;
        if (p + numEntries * 12 > bytes.Length) return;

        for (int i = 0; i < numEntries; i++, p += 12)
        {
            ushort tagId = ReadU16(bytes, p, be);
            ushort typeCode = ReadU16(bytes, p + 2, be);
            uint count = ReadU32(bytes, p + 4, be);

            // Sub-IFD pointer recursion (only from IFD0)
            if (!isExifSub && tagId == ExifIfdPointerTag)
            {
                uint subOffset = ReadU32(bytes, p + 8, be);
                ReadIfd(bytes, (int)subOffset, be, profile, isExifSub: true);
                continue;
            }
            if (!isExifSub && tagId == GpsIfdPointerTag)
            {
                uint subOffset = ReadU32(bytes, p + 8, be);
                ReadGpsIfd(bytes, (int)subOffset, be, profile);
                continue;
            }

            if (!Enum.IsDefined(typeof(VipsExifTag), tagId)) continue;
            var tag = (VipsExifTag)tagId;
            object? value = ReadValue(bytes, p + 8, (VipsExifType)typeCode, (int)count, be);
            if (value != null) profile._values[tag] = value;
        }
    }

    private static void ReadGpsIfd(byte[] bytes, int offset, bool be, VipsExifProfile profile)
    {
        if (offset < 0 || offset + 2 > bytes.Length) return;
        ushort numEntries = ReadU16(bytes, offset, be);
        int p = offset + 2;
        if (p + numEntries * 12 > bytes.Length) return;

        for (int i = 0; i < numEntries; i++, p += 12)
        {
            ushort tagId = ReadU16(bytes, p, be);
            ushort typeCode = ReadU16(bytes, p + 2, be);
            uint count = ReadU32(bytes, p + 4, be);
            if (!Enum.IsDefined(typeof(VipsGpsTag), tagId)) continue;
            var tag = (VipsGpsTag)tagId;
            object? value = ReadValue(bytes, p + 8, (VipsExifType)typeCode, (int)count, be);
            if (value != null) profile._gpsValues[tag] = value;
        }
    }

    private static object? ReadValue(byte[] bytes, int valueOffset, VipsExifType type, int count, bool be)
    {
        int unit = SizeOf(type);
        if (unit <= 0 || count < 0) return null;
        long total = (long)unit * count;
        // Inline storage if total bytes ≤ 4; otherwise valueOffset stores an offset.
        int dataOffset = total > 4 ? (int)ReadU32(bytes, valueOffset, be) : valueOffset;
        if (dataOffset < 0 || dataOffset + total > bytes.Length) return null;

        switch (type)
        {
            case VipsExifType.Byte:
            case VipsExifType.Undefined:
                if (count == 1) return bytes[dataOffset];
                var bs = new byte[count];
                Buffer.BlockCopy(bytes, dataOffset, bs, 0, count);
                return bs;
            case VipsExifType.Ascii:
                int len = count;
                while (len > 0 && bytes[dataOffset + len - 1] == 0) len--;
                return Encoding.ASCII.GetString(bytes, dataOffset, len);
            case VipsExifType.Short:
                if (count == 1) return ReadU16(bytes, dataOffset, be);
                var us = new ushort[count];
                for (int i = 0; i < count; i++) us[i] = ReadU16(bytes, dataOffset + i * 2, be);
                return us;
            case VipsExifType.Long:
                if (count == 1) return ReadU32(bytes, dataOffset, be);
                var u32s = new uint[count];
                for (int i = 0; i < count; i++) u32s[i] = ReadU32(bytes, dataOffset + i * 4, be);
                return u32s;
            case VipsExifType.Rational:
                if (count == 1)
                    return new VipsExifRational(
                        ReadU32(bytes, dataOffset, be),
                        ReadU32(bytes, dataOffset + 4, be));
                var rs = new VipsExifRational[count];
                for (int i = 0; i < count; i++)
                    rs[i] = new VipsExifRational(
                        ReadU32(bytes, dataOffset + i * 8, be),
                        ReadU32(bytes, dataOffset + i * 8 + 4, be));
                return rs;
            case VipsExifType.SShort:
                if (count == 1) return (short)ReadU16(bytes, dataOffset, be);
                var ss = new short[count];
                for (int i = 0; i < count; i++) ss[i] = (short)ReadU16(bytes, dataOffset + i * 2, be);
                return ss;
            case VipsExifType.SLong:
                if (count == 1) return (int)ReadU32(bytes, dataOffset, be);
                var i32s = new int[count];
                for (int i = 0; i < count; i++) i32s[i] = (int)ReadU32(bytes, dataOffset + i * 4, be);
                return i32s;
            case VipsExifType.SRational:
                if (count == 1)
                    return new VipsExifSRational(
                        (int)ReadU32(bytes, dataOffset, be),
                        (int)ReadU32(bytes, dataOffset + 4, be));
                var srs = new VipsExifSRational[count];
                for (int i = 0; i < count; i++)
                    srs[i] = new VipsExifSRational(
                        (int)ReadU32(bytes, dataOffset + i * 8, be),
                        (int)ReadU32(bytes, dataOffset + i * 8 + 4, be));
                return srs;
            default:
                return null;  // Float / Double not common in EXIF
        }
    }

    // ---------- Serialization ----------

    /// <summary>
    /// Serialize this profile back to a TIFF-format EXIF byte blob.
    /// Round-tripping <c>TryParse(profile.ToBytes())</c> recovers an
    /// equivalent profile.
    /// </summary>
    public byte[] ToBytes()
    {
        bool be = BigEndian;
        var ifd0Tags = _values.Where(kv => TagIfd[kv.Key] == 0)
            .OrderBy(kv => (ushort)kv.Key).ToList();
        var subTags = _values.Where(kv => TagIfd[kv.Key] == 1)
            .OrderBy(kv => (ushort)kv.Key).ToList();
        var gpsTags = _gpsValues.OrderBy(kv => (ushort)kv.Key).ToList();
        bool hasSubIfd = subTags.Count > 0;
        bool hasGpsIfd = gpsTags.Count > 0;
        int ifd0EntryCount = ifd0Tags.Count
            + (hasSubIfd ? 1 : 0) + (hasGpsIfd ? 1 : 0);

        // Pre-compute offsets so we can write entry "value-or-offset" fields correctly.
        const int headerSize = 8;                   // II/MM + magic + IFD0 ptr
        int ifd0Start = headerSize;
        int ifd0Size = 2 + ifd0EntryCount * 12 + 4;
        int subStart = ifd0Start + ifd0Size;
        int subSize = hasSubIfd ? 2 + subTags.Count * 12 + 4 : 0;
        int gpsStart = subStart + subSize;
        int gpsSize = hasGpsIfd ? 2 + gpsTags.Count * 12 + 4 : 0;
        int dataStart = gpsStart + gpsSize;

        var stream = new MemoryStream();
        // Header
        stream.WriteByte(be ? (byte)0x4D : (byte)0x49);
        stream.WriteByte(be ? (byte)0x4D : (byte)0x49);
        WriteU16(stream, 0x002A, be);
        WriteU32(stream, (uint)ifd0Start, be);

        // Build the data area in a separate buffer; entries reference into it.
        var data = new MemoryStream();
        int dataCursor = dataStart;

        // IFD0 entries — emit user tags + sub-IFD pointers in tag-ID order.
        WriteU16(stream, (ushort)ifd0EntryCount, be);
        // Sub-IFD pointer entries are 0x8769 / 0x8825; merge them into the
        // sorted tag list so the on-wire order is ascending.
        var ifd0Entries = new List<(ushort id, Action emit)>();
        foreach (var kv in ifd0Tags)
        {
            var tag = kv.Key; var val = kv.Value;
            ifd0Entries.Add(((ushort)tag,
                () => WriteEntry(stream, data, ref dataCursor, tag, val, be)));
        }
        if (hasSubIfd)
            ifd0Entries.Add((ExifIfdPointerTag, () =>
            {
                WriteU16(stream, ExifIfdPointerTag, be);
                WriteU16(stream, (ushort)VipsExifType.Long, be);
                WriteU32(stream, 1, be);
                WriteU32(stream, (uint)subStart, be);
            }));
        if (hasGpsIfd)
            ifd0Entries.Add((GpsIfdPointerTag, () =>
            {
                WriteU16(stream, GpsIfdPointerTag, be);
                WriteU16(stream, (ushort)VipsExifType.Long, be);
                WriteU32(stream, 1, be);
                WriteU32(stream, (uint)gpsStart, be);
            }));
        foreach (var (_, emit) in ifd0Entries.OrderBy(e => e.id))
            emit();
        WriteU32(stream, 0, be);  // No next IFD

        // Exif sub-IFD entries
        if (hasSubIfd)
        {
            WriteU16(stream, (ushort)subTags.Count, be);
            foreach (var (tag, val) in subTags)
                WriteEntry(stream, data, ref dataCursor, tag, val, be);
            WriteU32(stream, 0, be);
        }

        // GPS sub-IFD entries
        if (hasGpsIfd)
        {
            WriteU16(stream, (ushort)gpsTags.Count, be);
            foreach (var (tag, val) in gpsTags)
                WriteGpsEntry(stream, data, ref dataCursor, tag, val, be);
            WriteU32(stream, 0, be);
        }

        // Data area
        data.Position = 0;
        data.CopyTo(stream);
        return stream.ToArray();
    }

    private static void WriteGpsEntry(MemoryStream entries, MemoryStream data, ref int dataCursor,
        VipsGpsTag tag, object value, bool be)
    {
        var type = GpsTagType[tag];
        WriteU16(entries, (ushort)tag, be);
        WriteU16(entries, (ushort)type, be);
        var (encoded, count) = EncodeValue(type, value, be);
        WriteU32(entries, (uint)count, be);
        if (encoded.Length <= 4)
        {
            entries.Write(encoded, 0, encoded.Length);
            for (int i = encoded.Length; i < 4; i++) entries.WriteByte(0);
        }
        else
        {
            WriteU32(entries, (uint)dataCursor, be);
            data.Write(encoded, 0, encoded.Length);
            dataCursor += encoded.Length;
            if ((encoded.Length & 1) != 0) { data.WriteByte(0); dataCursor++; }
        }
    }

    private static void WriteEntry(MemoryStream entries, MemoryStream data, ref int dataCursor,
        VipsExifTag tag, object value, bool be)
    {
        var type = TagType[tag];
        WriteU16(entries, (ushort)tag, be);
        WriteU16(entries, (ushort)type, be);

        // Encode the value to bytes; record count and inline-vs-offset.
        var (encoded, count) = EncodeValue(type, value, be);
        WriteU32(entries, (uint)count, be);

        if (encoded.Length <= 4)
        {
            // Inline; pad to 4 bytes.
            entries.Write(encoded, 0, encoded.Length);
            for (int i = encoded.Length; i < 4; i++) entries.WriteByte(0);
        }
        else
        {
            // Offset; append data and reference it.
            WriteU32(entries, (uint)dataCursor, be);
            data.Write(encoded, 0, encoded.Length);
            dataCursor += encoded.Length;
            // EXIF requires entries on word boundaries — pad if odd.
            if ((encoded.Length & 1) != 0) { data.WriteByte(0); dataCursor++; }
        }
    }

    private static (byte[] bytes, int count) EncodeValue(VipsExifType type, object value, bool be)
    {
        switch (type)
        {
            case VipsExifType.Ascii:
            {
                string s = value as string ?? value.ToString()!;
                var bytes = Encoding.ASCII.GetBytes(s + "\0");
                return (bytes, bytes.Length);
            }
            case VipsExifType.Short:
            {
                ushort u = value switch
                {
                    ushort us => us,
                    short s => (ushort)s,
                    int i => (ushort)i,
                    _ => System.Convert.ToUInt16(value),
                };
                var b = new byte[2];
                if (be) BinaryPrimitives.WriteUInt16BigEndian(b, u);
                else BinaryPrimitives.WriteUInt16LittleEndian(b, u);
                return (b, 1);
            }
            case VipsExifType.Long:
            {
                uint u = value switch
                {
                    uint uu => uu,
                    int i => (uint)i,
                    _ => System.Convert.ToUInt32(value),
                };
                var b = new byte[4];
                if (be) BinaryPrimitives.WriteUInt32BigEndian(b, u);
                else BinaryPrimitives.WriteUInt32LittleEndian(b, u);
                return (b, 1);
            }
            case VipsExifType.Rational:
            {
                if (value is VipsExifRational[] rArr)
                {
                    var b = new byte[8 * rArr.Length];
                    for (int i = 0; i < rArr.Length; i++)
                    {
                        if (be)
                        {
                            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(i * 8, 4), rArr[i].Numerator);
                            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(i * 8 + 4, 4), rArr[i].Denominator);
                        }
                        else
                        {
                            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 8, 4), rArr[i].Numerator);
                            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 8 + 4, 4), rArr[i].Denominator);
                        }
                    }
                    return (b, rArr.Length);
                }
                var r = (VipsExifRational)value;
                var bs = new byte[8];
                if (be)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(bs.AsSpan(0, 4), r.Numerator);
                    BinaryPrimitives.WriteUInt32BigEndian(bs.AsSpan(4, 4), r.Denominator);
                }
                else
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(bs.AsSpan(0, 4), r.Numerator);
                    BinaryPrimitives.WriteUInt32LittleEndian(bs.AsSpan(4, 4), r.Denominator);
                }
                return (bs, 1);
            }
            case VipsExifType.Byte:
            case VipsExifType.Undefined:
            {
                byte[] arr = value switch
                {
                    byte[] ba => ba,
                    byte single => new byte[] { single },
                    string s => Encoding.UTF8.GetBytes(s),  // user comment etc.
                    _ => throw new InvalidOperationException($"Unsupported byte/undefined value: {value.GetType().Name}"),
                };
                return (arr, arr.Length);
            }
            default:
                throw new InvalidOperationException($"Encoding for {type} not implemented");
        }
    }

    // ---------- Helpers ----------

    private static int SizeOf(VipsExifType t) => t switch
    {
        VipsExifType.Byte or VipsExifType.SByte or VipsExifType.Ascii or VipsExifType.Undefined => 1,
        VipsExifType.Short or VipsExifType.SShort => 2,
        VipsExifType.Long or VipsExifType.SLong or VipsExifType.Float => 4,
        VipsExifType.Rational or VipsExifType.SRational or VipsExifType.Double => 8,
        _ => 0,
    };

    private static ushort ReadU16(byte[] b, int off, bool be) =>
        be ? BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(off, 2))
           : BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(off, 2));

    private static uint ReadU32(byte[] b, int off, bool be) =>
        be ? BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(off, 4))
           : BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(off, 4));

    private static void WriteU16(Stream s, ushort v, bool be)
    {
        Span<byte> b = stackalloc byte[2];
        if (be) BinaryPrimitives.WriteUInt16BigEndian(b, v);
        else BinaryPrimitives.WriteUInt16LittleEndian(b, v);
        s.Write(b);
    }

    private static void WriteU32(Stream s, uint v, bool be)
    {
        Span<byte> b = stackalloc byte[4];
        if (be) BinaryPrimitives.WriteUInt32BigEndian(b, v);
        else BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        s.Write(b);
    }
}
