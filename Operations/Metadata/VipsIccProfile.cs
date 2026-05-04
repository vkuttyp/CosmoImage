using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CosmoImage.Operations.Color;

namespace CosmoImage.Operations.Metadata;

/// <summary>
/// ICC profile class signature. Tells the CMM whether the profile
/// describes an input device (scanner / camera), a display, an
/// output device (printer), a device-link, an abstract working
/// space, etc.
/// </summary>
public enum VipsIccProfileClass
{
    /// <summary>Scanner / camera profile (signature "scnr").</summary>
    InputDevice,
    /// <summary>Display / monitor profile (signature "mntr").</summary>
    DisplayDevice,
    /// <summary>Printer profile (signature "prtr").</summary>
    OutputDevice,
    /// <summary>Device-to-device link (signature "link").</summary>
    DeviceLink,
    /// <summary>Color-space conversion profile (signature "spac").</summary>
    ColorSpace,
    /// <summary>Abstract effect profile (signature "abst").</summary>
    Abstract,
    /// <summary>Named-color list (signature "nmcl").</summary>
    NamedColor,
    /// <summary>Unknown / unrecognised signature.</summary>
    Unknown,
}

/// <summary>
/// ICC color space signature (the values "data color space" and
/// "profile connection space" can take). Includes the standard CIE
/// spaces, RGB / Gray / CMYK / CMY, the YCbCr / HSV / HLS variants,
/// plus the n-channel 2CLR…FCLR family.
/// </summary>
public enum VipsIccColorSpace
{
    Xyz, Lab, Luv, YCbCr, Yxy, Rgb, Gray, Hsv, Hls, Cmyk, Cmy,
    /// <summary>Generic 2-channel.</summary>
    TwoColor,
    /// <summary>Generic 3-channel.</summary>
    ThreeColor,
    /// <summary>Generic 4-channel.</summary>
    FourColor,
    /// <summary>Generic 5-channel.</summary>
    FiveColor,
    /// <summary>Generic 6-channel.</summary>
    SixColor,
    /// <summary>Generic 7-channel.</summary>
    SevenColor,
    /// <summary>Generic 8-channel.</summary>
    EightColor,
    Unknown,
}

/// <summary>ICC rendering intent.</summary>
public enum VipsIccRenderingIntent
{
    Perceptual = 0,
    RelativeColorimetric = 1,
    Saturation = 2,
    AbsoluteColorimetric = 3,
}

/// <summary>
/// Parsed view of an ICC profile blob. The 128-byte fixed-layout
/// header is exposed as typed properties; the tag table is indexed
/// by 4-char tag signature with raw tag data accessible per signature.
/// Mirrors ImageSharp's <c>IccProfile</c>.
///
/// <para>Round-trip is safe: <c>TryParse(profile.ToBytes())</c>
/// recovers an equivalent profile. Header fields are mutable; the
/// underlying tag-data bytes are preserved verbatim on serialisation
/// (parsing individual tag types — <c>desc</c> / <c>wtpt</c> /
/// <c>rXYZ</c> / curves / LUT-AtoB / etc. — is deferred to later
/// rounds).</para>
/// </summary>
public sealed class VipsIccProfile
{
    // ---- Header fields ----

    public uint ProfileSize { get; set; }
    public string PreferredCmm { get; set; } = "    ";
    public uint Version { get; set; } = 0x04300000u;  // ICC v4.3 default
    public VipsIccProfileClass ProfileClass { get; set; }
    public VipsIccColorSpace DataColorSpace { get; set; }
    public VipsIccColorSpace ConnectionColorSpace { get; set; }
    public DateTime CreationDateTime { get; set; }
    public string PrimaryPlatform { get; set; } = "    ";
    public uint Flags { get; set; }
    public string DeviceManufacturer { get; set; } = "    ";
    public string DeviceModel { get; set; } = "    ";
    public ulong DeviceAttributes { get; set; }
    public VipsIccRenderingIntent RenderingIntent { get; set; }
    public VipsColorXyz PcsIlluminant { get; set; }
    public string ProfileCreator { get; set; } = "    ";
    public byte[] ProfileId { get; set; } = new byte[16];

    // ---- Tag table ----

    private readonly Dictionary<string, byte[]> _tags = new();

    /// <summary>All tag signatures present in the profile (4-char strings).</summary>
    public IReadOnlyCollection<string> TagSignatures => _tags.Keys;

    /// <summary>Raw bytes for a specific tag, or <c>null</c> if absent.</summary>
    public byte[]? GetTagData(string signature)
        => _tags.TryGetValue(signature, out var data) ? data : null;

    /// <summary>Set / replace raw tag bytes by signature.</summary>
    public void SetTagData(string signature, byte[] data)
    {
        if (signature == null || signature.Length != 4)
            throw new ArgumentException("ICC tag signatures must be 4 characters", nameof(signature));
        _tags[signature] = data ?? throw new ArgumentNullException(nameof(data));
    }

    public bool RemoveTag(string signature) => _tags.Remove(signature);
    public bool ContainsTag(string signature) => _tags.ContainsKey(signature);

    /// <summary>Convenience: ICC version as "X.Y.Z" string from the BCD-encoded field.</summary>
    public string VersionString
        => $"{(Version >> 24) & 0xFF}.{(Version >> 20) & 0xF}.{(Version >> 16) & 0xF}";

    // ---------- Parsing ----------

    /// <summary>
    /// Parse a raw ICC profile blob. Returns <c>null</c> for null /
    /// empty / malformed input. The header magic ("acsp" at offset 36)
    /// must be present.
    /// </summary>
    public static VipsIccProfile? TryParse(byte[]? bytes)
    {
        if (bytes == null || bytes.Length < 128) return null;
        try { return Parse(bytes); }
        catch { return null; }
    }

    private static VipsIccProfile Parse(byte[] bytes)
    {
        // ICC integers are big-endian.
        uint profileSize = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(0, 4));
        if (profileSize > bytes.Length) profileSize = (uint)bytes.Length;

        string magic = Encoding.ASCII.GetString(bytes, 36, 4);
        if (magic != "acsp") throw new InvalidOperationException("Bad ICC magic");

        var profile = new VipsIccProfile
        {
            ProfileSize = profileSize,
            PreferredCmm = ReadSig(bytes, 4),
            Version = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4)),
            ProfileClass = ParseProfileClass(ReadSig(bytes, 12)),
            DataColorSpace = ParseColorSpace(ReadSig(bytes, 16)),
            ConnectionColorSpace = ParseColorSpace(ReadSig(bytes, 20)),
            CreationDateTime = ReadDateTime(bytes, 24),
            PrimaryPlatform = ReadSig(bytes, 40),
            Flags = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(44, 4)),
            DeviceManufacturer = ReadSig(bytes, 48),
            DeviceModel = ReadSig(bytes, 52),
            DeviceAttributes = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(56, 8)),
            RenderingIntent = (VipsIccRenderingIntent)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(64, 4)),
            PcsIlluminant = new VipsColorXyz(
                ReadS15Fixed16(bytes, 68),
                ReadS15Fixed16(bytes, 72),
                ReadS15Fixed16(bytes, 76)),
            ProfileCreator = ReadSig(bytes, 80),
            ProfileId = bytes.AsSpan(84, 16).ToArray(),
        };

        // Tag table.
        if (bytes.Length < 132) return profile;
        uint tagCount = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(128, 4));
        for (uint i = 0; i < tagCount; i++)
        {
            int entryOffset = 132 + (int)i * 12;
            if (entryOffset + 12 > bytes.Length) break;
            string sig = Encoding.ASCII.GetString(bytes, entryOffset, 4);
            uint dataOffset = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(entryOffset + 4, 4));
            uint dataSize = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(entryOffset + 8, 4));
            if (dataOffset + dataSize > bytes.Length) continue;  // bad pointer — skip
            var data = new byte[dataSize];
            Buffer.BlockCopy(bytes, (int)dataOffset, data, 0, (int)dataSize);
            profile._tags[sig] = data;
        }
        return profile;
    }

    // ---------- Serialization ----------

    /// <summary>
    /// Serialise this profile back to bytes. Header fields write to
    /// their fixed offsets; tag data is appended after the tag table
    /// and indexed by recomputed offsets. Round-trip preserves header
    /// fields and per-tag bytes.
    /// </summary>
    public byte[] ToBytes()
    {
        // 128 byte header + 4 byte tag count + N × 12 byte entries +
        // 4-byte-aligned tag data.
        int tagCount = _tags.Count;
        int tagTableSize = 4 + tagCount * 12;
        int dataStart = AlignTo4(128 + tagTableSize);

        // Compute tag offsets.
        var entries = new List<(string Sig, int Offset, int Size, byte[] Data)>();
        int cursor = dataStart;
        foreach (var (sig, data) in _tags.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            entries.Add((sig, cursor, data.Length, data));
            cursor = AlignTo4(cursor + data.Length);
        }
        int totalSize = cursor;

        var output = new byte[totalSize];

        // Header
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(0, 4), (uint)totalSize);
        WriteSig(output, 4, PreferredCmm);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(8, 4), Version);
        WriteSig(output, 12, EncodeProfileClass(ProfileClass));
        WriteSig(output, 16, EncodeColorSpace(DataColorSpace));
        WriteSig(output, 20, EncodeColorSpace(ConnectionColorSpace));
        WriteDateTime(output, 24, CreationDateTime);
        WriteSig(output, 36, "acsp");
        WriteSig(output, 40, PrimaryPlatform);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(44, 4), Flags);
        WriteSig(output, 48, DeviceManufacturer);
        WriteSig(output, 52, DeviceModel);
        BinaryPrimitives.WriteUInt64BigEndian(output.AsSpan(56, 8), DeviceAttributes);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(64, 4), (uint)RenderingIntent);
        WriteS15Fixed16(output, 68, PcsIlluminant.X);
        WriteS15Fixed16(output, 72, PcsIlluminant.Y);
        WriteS15Fixed16(output, 76, PcsIlluminant.Z);
        WriteSig(output, 80, ProfileCreator);
        Buffer.BlockCopy(ProfileId, 0, output, 84, Math.Min(16, ProfileId.Length));

        // Tag table
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(128, 4), (uint)tagCount);
        for (int i = 0; i < entries.Count; i++)
        {
            var (sig, off, sz, _) = entries[i];
            int entryOff = 132 + i * 12;
            WriteSig(output, entryOff, sig);
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(entryOff + 4, 4), (uint)off);
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(entryOff + 8, 4), (uint)sz);
        }

        // Tag data
        foreach (var (_, off, _, data) in entries)
            Buffer.BlockCopy(data, 0, output, off, data.Length);

        // Update ProfileSize so it agrees with reality.
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(0, 4), (uint)totalSize);

        return output;
    }

    // ---------- Helpers ----------

    private static int AlignTo4(int value) => (value + 3) & ~3;

    private static string ReadSig(byte[] bytes, int offset)
        => Encoding.ASCII.GetString(bytes, offset, 4);

    private static void WriteSig(byte[] dst, int offset, string sig)
    {
        if (sig == null) sig = "    ";
        var bytes = Encoding.ASCII.GetBytes(sig.PadRight(4).Substring(0, 4));
        Buffer.BlockCopy(bytes, 0, dst, offset, 4);
    }

    private static double ReadS15Fixed16(byte[] bytes, int offset)
    {
        int v = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, 4));
        return v / 65536.0;
    }

    private static void WriteS15Fixed16(byte[] dst, int offset, double value)
    {
        int v = (int)Math.Round(value * 65536.0);
        BinaryPrimitives.WriteInt32BigEndian(dst.AsSpan(offset, 4), v);
    }

    private static DateTime ReadDateTime(byte[] bytes, int offset)
    {
        ushort year = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));
        ushort month = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 2, 2));
        ushort day = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 4, 2));
        ushort hour = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 6, 2));
        ushort minute = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 8, 2));
        ushort second = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 10, 2));
        if (year == 0) return DateTime.MinValue;
        try { return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc); }
        catch { return DateTime.MinValue; }
    }

    private static void WriteDateTime(byte[] dst, int offset, DateTime dt)
    {
        if (dt == DateTime.MinValue)
        {
            for (int i = 0; i < 12; i++) dst[offset + i] = 0;
            return;
        }
        BinaryPrimitives.WriteUInt16BigEndian(dst.AsSpan(offset, 2), (ushort)dt.Year);
        BinaryPrimitives.WriteUInt16BigEndian(dst.AsSpan(offset + 2, 2), (ushort)dt.Month);
        BinaryPrimitives.WriteUInt16BigEndian(dst.AsSpan(offset + 4, 2), (ushort)dt.Day);
        BinaryPrimitives.WriteUInt16BigEndian(dst.AsSpan(offset + 6, 2), (ushort)dt.Hour);
        BinaryPrimitives.WriteUInt16BigEndian(dst.AsSpan(offset + 8, 2), (ushort)dt.Minute);
        BinaryPrimitives.WriteUInt16BigEndian(dst.AsSpan(offset + 10, 2), (ushort)dt.Second);
    }

    private static VipsIccProfileClass ParseProfileClass(string sig) => sig switch
    {
        "scnr" => VipsIccProfileClass.InputDevice,
        "mntr" => VipsIccProfileClass.DisplayDevice,
        "prtr" => VipsIccProfileClass.OutputDevice,
        "link" => VipsIccProfileClass.DeviceLink,
        "spac" => VipsIccProfileClass.ColorSpace,
        "abst" => VipsIccProfileClass.Abstract,
        "nmcl" => VipsIccProfileClass.NamedColor,
        _ => VipsIccProfileClass.Unknown,
    };

    private static string EncodeProfileClass(VipsIccProfileClass c) => c switch
    {
        VipsIccProfileClass.InputDevice => "scnr",
        VipsIccProfileClass.DisplayDevice => "mntr",
        VipsIccProfileClass.OutputDevice => "prtr",
        VipsIccProfileClass.DeviceLink => "link",
        VipsIccProfileClass.ColorSpace => "spac",
        VipsIccProfileClass.Abstract => "abst",
        VipsIccProfileClass.NamedColor => "nmcl",
        _ => "    ",
    };

    private static VipsIccColorSpace ParseColorSpace(string sig) => sig switch
    {
        "XYZ " => VipsIccColorSpace.Xyz,
        "Lab " => VipsIccColorSpace.Lab,
        "Luv " => VipsIccColorSpace.Luv,
        "YCbr" => VipsIccColorSpace.YCbCr,
        "Yxy " => VipsIccColorSpace.Yxy,
        "RGB " => VipsIccColorSpace.Rgb,
        "GRAY" => VipsIccColorSpace.Gray,
        "HSV " => VipsIccColorSpace.Hsv,
        "HLS " => VipsIccColorSpace.Hls,
        "CMYK" => VipsIccColorSpace.Cmyk,
        "CMY " => VipsIccColorSpace.Cmy,
        "2CLR" => VipsIccColorSpace.TwoColor,
        "3CLR" => VipsIccColorSpace.ThreeColor,
        "4CLR" => VipsIccColorSpace.FourColor,
        "5CLR" => VipsIccColorSpace.FiveColor,
        "6CLR" => VipsIccColorSpace.SixColor,
        "7CLR" => VipsIccColorSpace.SevenColor,
        "8CLR" => VipsIccColorSpace.EightColor,
        _ => VipsIccColorSpace.Unknown,
    };

    private static string EncodeColorSpace(VipsIccColorSpace cs) => cs switch
    {
        VipsIccColorSpace.Xyz => "XYZ ",
        VipsIccColorSpace.Lab => "Lab ",
        VipsIccColorSpace.Luv => "Luv ",
        VipsIccColorSpace.YCbCr => "YCbr",
        VipsIccColorSpace.Yxy => "Yxy ",
        VipsIccColorSpace.Rgb => "RGB ",
        VipsIccColorSpace.Gray => "GRAY",
        VipsIccColorSpace.Hsv => "HSV ",
        VipsIccColorSpace.Hls => "HLS ",
        VipsIccColorSpace.Cmyk => "CMYK",
        VipsIccColorSpace.Cmy => "CMY ",
        VipsIccColorSpace.TwoColor => "2CLR",
        VipsIccColorSpace.ThreeColor => "3CLR",
        VipsIccColorSpace.FourColor => "4CLR",
        VipsIccColorSpace.FiveColor => "5CLR",
        VipsIccColorSpace.SixColor => "6CLR",
        VipsIccColorSpace.SevenColor => "7CLR",
        VipsIccColorSpace.EightColor => "8CLR",
        _ => "    ",
    };
}
