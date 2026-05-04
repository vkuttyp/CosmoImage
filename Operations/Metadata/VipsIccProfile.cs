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
public sealed class IccMft2
{
    /// <summary>Number of input channels (1..4 in practice).</summary>
    public int InputChannels { get; init; }
    /// <summary>Number of output channels (1..4 in practice).</summary>
    public int OutputChannels { get; init; }
    /// <summary>Grid points per CLUT axis (same in every dimension).</summary>
    public int GridSize { get; init; }
    /// <summary>3×3 matrix applied to XYZ-input profiles after the input curves; identity for non-XYZ inputs.</summary>
    public double[,] Matrix { get; init; } = new double[3, 3];
    /// <summary>Per-input-channel input curves: each table has the same length.</summary>
    public ushort[][] InputTables { get; init; } = Array.Empty<ushort[]>();
    /// <summary>Flattened CLUT: <c>GridSize^InputChannels * OutputChannels</c> entries.</summary>
    public ushort[] Clut { get; init; } = Array.Empty<ushort>();
    /// <summary>Per-output-channel output curves.</summary>
    public ushort[][] OutputTables { get; init; } = Array.Empty<ushort[]>();
}

/// <summary>
/// Parsed multi-dimensional CLUT (used by mAB/mBA tags). Grid sizes
/// can vary per dimension (unlike mft2 which uses a single grid size
/// for all axes).
/// </summary>
public sealed class IccClut
{
    public int InputChannels { get; init; }
    public int OutputChannels { get; init; }
    /// <summary>Grid points per dimension (length = InputChannels).</summary>
    public int[] GridSizes { get; init; } = Array.Empty<int>();
    /// <summary>Flattened CLUT entries normalized to ushort (range 0..65535).</summary>
    public ushort[] Data { get; init; } = Array.Empty<ushort>();
}

/// <summary>
/// Parsed lutAtoBType ('mAB ') or lutBtoAType ('mBA ') tag. The two
/// formats share a layout — the only difference is pipeline order:
///   mAB (forward): A curves → CLUT → M curves → matrix → B curves
///   mBA (reverse): B curves → matrix → M curves → CLUT → A curves
/// Each component is optional; missing pieces are skipped at apply
/// time.
/// </summary>
public sealed class IccLutAB
{
    public bool IsAtoB { get; init; }
    public int InputChannels { get; init; }
    public int OutputChannels { get; init; }
    public Func<double, double>[]? ACurves { get; init; }
    public Func<double, double>[]? BCurves { get; init; }
    public Func<double, double>[]? MCurves { get; init; }
    /// <summary>3×4 matrix: 3×3 multiplier in cols 0..2, offset vector in col 3. Applied as <c>output = M[3×3] * input + M[*, 3]</c>.</summary>
    public double[,]? Matrix { get; init; }
    public IccClut? Clut { get; init; }
}

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

    private bool IsV4 => (Version >> 24) >= 4;

    // ---- Typed tag-data accessors ----

    /// <summary>
    /// Decode a text tag's value (handles <c>text</c>, <c>desc</c>
    /// (textDescriptionType / ICC v2), and <c>mluc</c>
    /// (multiLocalizedUnicodeType / ICC v4)). Returns <c>null</c>
    /// when the tag is absent or its type isn't a text variant.
    /// </summary>
    public string? GetTagText(string signature)
    {
        var data = GetTagData(signature);
        if (data == null || data.Length < 8) return null;
        string type = Encoding.ASCII.GetString(data, 0, 4);
        return type switch
        {
            "text" => DecodeText(data),
            "desc" => DecodeDesc(data),
            "mluc" => DecodeMluc(data),
            _ => null,
        };
    }

    /// <summary>
    /// Set a text tag's value. Encoder is chosen by profile version:
    /// ICC v4+ writes <c>mluc</c>; ICC v2 writes <c>desc</c> for the
    /// description-flavoured tags (<c>desc</c> / <c>dmnd</c> / <c>dmdd</c>)
    /// and <c>text</c> for everything else (<c>cprt</c> etc.).
    /// </summary>
    public void SetTagText(string signature, string value)
    {
        if (value == null) throw new ArgumentNullException(nameof(value));
        byte[] data;
        if (IsV4) data = EncodeMluc(value);
        else if (signature is "desc" or "dmnd" or "dmdd") data = EncodeDesc(value);
        else data = EncodeText(value);
        SetTagData(signature, data);
    }

    /// <summary>Decode an <c>XYZ</c>-type tag (e.g., wtpt, bkpt, rXYZ).</summary>
    public VipsColorXyz? GetTagXyz(string signature)
    {
        var data = GetTagData(signature);
        if (data == null || data.Length < 20) return null;
        if (Encoding.ASCII.GetString(data, 0, 4) != "XYZ ") return null;
        return new VipsColorXyz(
            ReadS15Fixed16(data, 8),
            ReadS15Fixed16(data, 12),
            ReadS15Fixed16(data, 16));
    }

    /// <summary>Set an <c>XYZ</c>-type tag.</summary>
    public void SetTagXyz(string signature, VipsColorXyz xyz)
    {
        var data = new byte[20];
        Encoding.ASCII.GetBytes("XYZ ").CopyTo(data, 0);
        WriteS15Fixed16(data, 8, xyz.X);
        WriteS15Fixed16(data, 12, xyz.Y);
        WriteS15Fixed16(data, 16, xyz.Z);
        SetTagData(signature, data);
    }

    /// <summary>
    /// Decode a <c>curv</c>-type tag's gamma value. Returns
    /// <c>1.0</c> for an identity curve (count=0), the gamma exponent
    /// for the single-value form (count=1, u8Fixed8 encoding), or
    /// <c>null</c> if the curve is a lookup table — use
    /// <see cref="GetTagCurveTable"/> for the LUT form.
    /// </summary>
    public double? GetTagCurveGamma(string signature)
    {
        var data = GetTagData(signature);
        if (data == null || data.Length < 12) return null;
        if (Encoding.ASCII.GetString(data, 0, 4) != "curv") return null;
        uint count = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8, 4));
        if (count == 0) return 1.0;
        if (count == 1 && data.Length >= 14)
            return BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(12, 2)) / 256.0;
        return null;
    }

    /// <summary>
    /// Decode a <c>curv</c>-type tag's lookup table. Returns the raw
    /// uint16 entries (range 0..65535), or <c>null</c> if the curve is
    /// gamma-form or absent.
    /// </summary>
    public ushort[]? GetTagCurveTable(string signature)
    {
        var data = GetTagData(signature);
        if (data == null || data.Length < 12) return null;
        if (Encoding.ASCII.GetString(data, 0, 4) != "curv") return null;
        uint count = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8, 4));
        if (count <= 1) return null;
        if (12 + count * 2 > data.Length) return null;
        var table = new ushort[count];
        for (int i = 0; i < count; i++)
            table[i] = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(12 + i * 2, 2));
        return table;
    }

    /// <summary>Set a <c>curv</c>-type tag in single-gamma form.</summary>
    public void SetTagCurveGamma(string signature, double gamma)
    {
        var data = new byte[14];
        Encoding.ASCII.GetBytes("curv").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8, 4), 1);
        ushort raw = (ushort)Math.Round(Math.Clamp(gamma * 256, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(12, 2), raw);
        SetTagData(signature, data);
    }

    /// <summary>Set a <c>curv</c>-type tag with an arbitrary lookup table.</summary>
    public void SetTagCurveTable(string signature, ushort[] table)
    {
        if (table == null) throw new ArgumentNullException(nameof(table));
        var data = new byte[12 + table.Length * 2];
        Encoding.ASCII.GetBytes("curv").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8, 4), (uint)table.Length);
        for (int i = 0; i < table.Length; i++)
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(12 + i * 2, 2), table[i]);
        SetTagData(signature, data);
    }

    /// <summary>
    /// Decode a <c>para</c>-type tag (parametricCurveType, ICC v4
    /// section 10.16). Returns <c>(functionType, params)</c> where
    /// <c>functionType</c> is 0..4 and <c>params</c> contains the
    /// transmitted s15Fixed16 values (1, 3, 4, 5, or 7 entries
    /// respectively for each functionType). Returns <c>null</c> for
    /// missing tags or unrecognised forms.
    ///
    /// <para>Function definitions:</para>
    /// <list type="bullet">
    ///   <item>0: Y = X^g (1 param: g)</item>
    ///   <item>1: Y = (a*X + b)^g for X &#x2265; -b/a, else 0 (3: g, a, b)</item>
    ///   <item>2: Y = (a*X + b)^g + c for X &#x2265; -b/a, else c (4: g, a, b, c)</item>
    ///   <item>3: Y = (a*X + b)^g for X &#x2265; d, else c*X (5: g, a, b, c, d)</item>
    ///   <item>4: Y = (a*X + b)^g + e for X &#x2265; d, else c*X + f (7: g, a, b, c, d, e, f)</item>
    /// </list>
    /// </summary>
    public (int FunctionType, double[] Params)? GetTagParametricCurve(string signature)
    {
        var data = GetTagData(signature);
        if (data == null || data.Length < 12) return null;
        if (Encoding.ASCII.GetString(data, 0, 4) != "para") return null;
        int functionType = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(8, 2));
        // bytes 10-11 are reserved.
        int paramCount = functionType switch
        {
            0 => 1, 1 => 3, 2 => 4, 3 => 5, 4 => 7,
            _ => -1,
        };
        if (paramCount < 0) return null;
        if (data.Length < 12 + paramCount * 4) return null;
        var pars = new double[paramCount];
        for (int i = 0; i < paramCount; i++)
            pars[i] = ReadS15Fixed16(data, 12 + i * 4);
        return (functionType, pars);
    }

    /// <summary>
    /// Build a forward evaluator for the curve tag at <paramref name="signature"/>:
    /// maps input <c>x</c> in [0, 1] to output in [0, 1] regardless of
    /// whether the underlying tag is <c>curv</c> (gamma or LUT) or
    /// <c>para</c> (parametric). Returns <c>null</c> when no curve is
    /// stored under that signature.
    /// </summary>
    public Func<double, double>? GetTagCurveEvaluator(string signature)
    {
        var data = GetTagData(signature);
        if (data == null || data.Length < 8) return null;
        string type = Encoding.ASCII.GetString(data, 0, 4);
        if (type == "curv")
        {
            var gamma = GetTagCurveGamma(signature);
            if (gamma.HasValue)
            {
                double g = gamma.Value;
                return x => x <= 0 ? 0 : Math.Pow(x, g);
            }
            var table = GetTagCurveTable(signature);
            if (table == null) return null;
            return BuildLutEvaluator(table);
        }
        if (type == "para")
        {
            var p = GetTagParametricCurve(signature);
            if (p == null) return null;
            var (fn, pars) = p.Value;
            return BuildParametricEvaluator(fn, pars);
        }
        return null;
    }

    private static Func<double, double> BuildLutEvaluator(ushort[] table)
    {
        int n = table.Length;
        return x =>
        {
            if (x <= 0) return table[0] / 65535.0;
            if (x >= 1) return table[n - 1] / 65535.0;
            double pos = x * (n - 1);
            int i = (int)pos;
            double frac = pos - i;
            double a = table[i] / 65535.0;
            double b = table[Math.Min(i + 1, n - 1)] / 65535.0;
            return a + (b - a) * frac;
        };
    }

    /// <summary>
    /// Decode a <c>mft2</c>-type tag (lut16Type, ICC v2 / v4 section 10.10).
    /// Carries an n-input m-output pipeline:
    ///   input table per input channel → optional 3×3 matrix (XYZ profiles
    ///   only) → 3D CLUT with trilinear interp → output table per output
    ///   channel. Tables and CLUT entries are uint16 (range 0..65535).
    ///   The matrix uses s15Fixed16 encoding.
    ///
    /// <para>Returns <c>null</c> for missing tags or shapes the parser
    /// can't recognise.</para>
    /// </summary>
    public IccMft2? GetTagMft2(string signature)
    {
        var data = GetTagData(signature);
        if (data == null || data.Length < 52) return null;
        if (Encoding.ASCII.GetString(data, 0, 4) != "mft2") return null;

        int inCh = data[8];
        int outCh = data[9];
        int grid = data[10];
        if (inCh < 1 || inCh > 4 || outCh < 1 || outCh > 4 || grid < 2 || grid > 255)
            return null;

        var matrix = new double[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                matrix[i, j] = ReadS15Fixed16(data, 12 + (i * 3 + j) * 4);

        int n = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(48, 2));  // input table size
        int m = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(50, 2));  // output table size
        if (n < 2 || m < 2) return null;

        long need = 52L + 2L * inCh * n + 2L * Pow(grid, inCh) * outCh + 2L * outCh * m;
        if (data.Length < need) return null;

        var inputTables = new ushort[inCh][];
        int p = 52;
        for (int c = 0; c < inCh; c++)
        {
            inputTables[c] = new ushort[n];
            for (int k = 0; k < n; k++)
                inputTables[c][k] = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p + k * 2, 2));
            p += n * 2;
        }

        long clutEntries = (long)Pow(grid, inCh) * outCh;
        var clut = new ushort[clutEntries];
        for (long k = 0; k < clutEntries; k++)
        {
            clut[k] = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p + (int)k * 2, 2));
        }
        p += (int)clutEntries * 2;

        var outputTables = new ushort[outCh][];
        for (int c = 0; c < outCh; c++)
        {
            outputTables[c] = new ushort[m];
            for (int k = 0; k < m; k++)
                outputTables[c][k] = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p + k * 2, 2));
            p += m * 2;
        }

        return new IccMft2
        {
            InputChannels = inCh,
            OutputChannels = outCh,
            GridSize = grid,
            Matrix = matrix,
            InputTables = inputTables,
            Clut = clut,
            OutputTables = outputTables,
        };
    }

    private static int Pow(int b, int e)
    {
        int r = 1;
        for (int i = 0; i < e; i++) r *= b;
        return r;
    }

    /// <summary>
    /// Decode a <c>mAB </c> (lutAtoBType) or <c>mBA </c> (lutBtoAType)
    /// tag. Returns <c>null</c> for missing tags or shapes the parser
    /// can't recognise. <paramref name="signature"/> is the tag slot
    /// name (e.g., <c>"A2B0"</c> for the perceptual A-to-B intent).
    /// </summary>
    public IccLutAB? GetTagLutAB(string signature)
    {
        var data = GetTagData(signature);
        if (data == null || data.Length < 32) return null;
        string sig = Encoding.ASCII.GetString(data, 0, 4);
        bool isAtoB;
        if (sig == "mAB ") isAtoB = true;
        else if (sig == "mBA ") isAtoB = false;
        else return null;

        int inCh = data[8];
        int outCh = data[9];
        if (inCh < 1 || inCh > 4 || outCh < 1 || outCh > 4) return null;

        uint offB = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(12, 4));
        uint offMatrix = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16, 4));
        uint offM = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(20, 4));
        uint offClut = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(24, 4));
        uint offA = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(28, 4));

        // For mAB: A curves count = i, B curves count = o.
        // For mBA: A curves count = o, B curves count = i.
        // Easier framing: A curves apply to whichever side is the "A" side
        // (the device side); B curves apply to the PCS side.
        int aCount = isAtoB ? inCh : outCh;
        int bCount = isAtoB ? outCh : inCh;

        Func<double, double>[]? aCurves = offA != 0 ? ParseCurves(data, (int)offA, aCount) : null;
        Func<double, double>[]? bCurves = offB != 0 ? ParseCurves(data, (int)offB, bCount) : null;
        Func<double, double>[]? mCurves = offM != 0 ? ParseCurves(data, (int)offM, 3) : null;
        if ((offA != 0 && aCurves == null) || (offB != 0 && bCurves == null) || (offM != 0 && mCurves == null))
            return null;

        double[,]? matrix = null;
        if (offMatrix != 0)
        {
            if (offMatrix + 12 * 4 > data.Length) return null;
            matrix = new double[3, 4];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    matrix[i, j] = ReadS15Fixed16(data, (int)offMatrix + (i * 3 + j) * 4);
            // Offset vector is the last 3 s15Fixed16 values.
            for (int i = 0; i < 3; i++)
                matrix[i, 3] = ReadS15Fixed16(data, (int)offMatrix + (9 + i) * 4);
        }

        IccClut? clut = null;
        if (offClut != 0)
        {
            clut = ParseClut(data, (int)offClut, inCh, outCh);
            if (clut == null) return null;
        }

        return new IccLutAB
        {
            IsAtoB = isAtoB,
            InputChannels = inCh,
            OutputChannels = outCh,
            ACurves = aCurves,
            BCurves = bCurves,
            MCurves = mCurves,
            Matrix = matrix,
            Clut = clut,
        };
    }

    /// <summary>
    /// Parse <paramref name="count"/> consecutive curves ('curv' or 'para')
    /// starting at <paramref name="offset"/>. Each curve is padded to a
    /// 4-byte boundary in the file. Returns <c>null</c> on malformed
    /// curves.
    /// </summary>
    private Func<double, double>[]? ParseCurves(byte[] data, int offset, int count)
    {
        var result = new Func<double, double>[count];
        int p = offset;
        for (int i = 0; i < count; i++)
        {
            if (p + 8 > data.Length) return null;
            string type = Encoding.ASCII.GetString(data, p, 4);
            int consumed;
            if (type == "curv")
            {
                if (p + 12 > data.Length) return null;
                uint n = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p + 8, 4));
                consumed = 12 + (int)n * 2;
                if (p + consumed > data.Length) return null;
                if (n == 0)
                {
                    result[i] = x => x;  // identity
                }
                else if (n == 1)
                {
                    if (p + 14 > data.Length) return null;
                    double gamma = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p + 12, 2)) / 256.0;
                    result[i] = x => x <= 0 ? 0 : Math.Pow(x, gamma);
                }
                else
                {
                    var table = new ushort[n];
                    for (int k = 0; k < n; k++)
                        table[k] = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p + 12 + k * 2, 2));
                    result[i] = BuildLutEvaluator(table);
                }
            }
            else if (type == "para")
            {
                if (p + 12 > data.Length) return null;
                int fnType = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p + 8, 2));
                int paramCount = fnType switch { 0 => 1, 1 => 3, 2 => 4, 3 => 5, 4 => 7, _ => -1 };
                if (paramCount < 0) return null;
                consumed = 12 + paramCount * 4;
                if (p + consumed > data.Length) return null;
                var pars = new double[paramCount];
                for (int k = 0; k < paramCount; k++)
                    pars[k] = ReadS15Fixed16(data, p + 12 + k * 4);
                result[i] = BuildParametricEvaluator(fnType, pars);
            }
            else
            {
                return null;
            }
            // Curves are 4-byte aligned within the tag.
            p += (consumed + 3) & ~3;
        }
        return result;
    }

    /// <summary>
    /// Parse the multi-dimensional CLUT inside an mAB/mBA tag.
    /// CLUT layout: 16 bytes of grid sizes (one byte per dim, unused
    /// dims = 0), 1 byte precision (1 = uint8, 2 = uint16), 3 reserved
    /// bytes, then the table data.
    /// </summary>
    private static IccClut? ParseClut(byte[] data, int offset, int inCh, int outCh)
    {
        if (offset + 20 > data.Length) return null;
        var grids = new int[inCh];
        long entries = 1;
        for (int i = 0; i < inCh; i++)
        {
            grids[i] = data[offset + i];
            if (grids[i] < 2 || grids[i] > 255) return null;
            entries *= grids[i];
        }
        int precision = data[offset + 16];
        if (precision != 1 && precision != 2) return null;
        long bytes = entries * outCh * precision;
        if (offset + 20 + bytes > data.Length) return null;

        var dataArr = new ushort[entries * outCh];
        int p = offset + 20;
        if (precision == 1)
        {
            for (long i = 0; i < entries * outCh; i++)
                dataArr[i] = (ushort)((data[p + (int)i]) * 257);  // 8-bit → 16-bit scale
        }
        else
        {
            for (long i = 0; i < entries * outCh; i++)
                dataArr[i] = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p + (int)i * 2, 2));
        }

        return new IccClut
        {
            InputChannels = inCh,
            OutputChannels = outCh,
            GridSizes = grids,
            Data = dataArr,
        };
    }

    private static Func<double, double> BuildParametricEvaluator(int fn, double[] p)
    {
        return fn switch
        {
            0 => x => x <= 0 ? 0 : Math.Pow(x, p[0]),
            1 => x =>
            {
                double thresh = -p[2] / p[1];
                if (x < thresh) return 0;
                return Math.Pow(p[1] * x + p[2], p[0]);
            },
            2 => x =>
            {
                double thresh = -p[2] / p[1];
                if (x < thresh) return p[3];
                return Math.Pow(p[1] * x + p[2], p[0]) + p[3];
            },
            3 => x =>
            {
                if (x < p[4]) return p[3] * x;
                return Math.Pow(p[1] * x + p[2], p[0]);
            },
            4 => x =>
            {
                if (x < p[4]) return p[3] * x + p[6];
                return Math.Pow(p[1] * x + p[2], p[0]) + p[5];
            },
            _ => x => x,
        };
    }

    // ---- Convenience properties for common tags ----

    /// <summary>The profile's human-readable description ("desc" tag).</summary>
    public string? Description
    {
        get => GetTagText("desc");
        set { if (value == null) RemoveTag("desc"); else SetTagText("desc", value); }
    }

    /// <summary>The profile's copyright string ("cprt" tag).</summary>
    public string? Copyright
    {
        get => GetTagText("cprt");
        set { if (value == null) RemoveTag("cprt"); else SetTagText("cprt", value); }
    }

    /// <summary>The profile's media white point ("wtpt" tag).</summary>
    public VipsColorXyz? WhitePoint
    {
        get => GetTagXyz("wtpt");
        set { if (value is VipsColorXyz xyz) SetTagXyz("wtpt", xyz); else RemoveTag("wtpt"); }
    }

    /// <summary>The profile's media black point ("bkpt" tag).</summary>
    public VipsColorXyz? BlackPoint
    {
        get => GetTagXyz("bkpt");
        set { if (value is VipsColorXyz xyz) SetTagXyz("bkpt", xyz); else RemoveTag("bkpt"); }
    }

    /// <summary>RGB profile primary — red column of the XYZ matrix ("rXYZ" tag).</summary>
    public VipsColorXyz? RedPrimary
    {
        get => GetTagXyz("rXYZ");
        set { if (value is VipsColorXyz xyz) SetTagXyz("rXYZ", xyz); else RemoveTag("rXYZ"); }
    }

    /// <summary>RGB profile primary — green column ("gXYZ" tag).</summary>
    public VipsColorXyz? GreenPrimary
    {
        get => GetTagXyz("gXYZ");
        set { if (value is VipsColorXyz xyz) SetTagXyz("gXYZ", xyz); else RemoveTag("gXYZ"); }
    }

    /// <summary>RGB profile primary — blue column ("bXYZ" tag).</summary>
    public VipsColorXyz? BluePrimary
    {
        get => GetTagXyz("bXYZ");
        set { if (value is VipsColorXyz xyz) SetTagXyz("bXYZ", xyz); else RemoveTag("bXYZ"); }
    }

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

    // ---- Tag-type decoders ----

    private static string? DecodeText(byte[] tagData)
    {
        if (tagData.Length < 8) return null;
        int len = 0;
        while (8 + len < tagData.Length && tagData[8 + len] != 0) len++;
        return Encoding.ASCII.GetString(tagData, 8, len);
    }

    private static string? DecodeDesc(byte[] tagData)
    {
        if (tagData.Length < 12) return null;
        uint asciiCount = BinaryPrimitives.ReadUInt32BigEndian(tagData.AsSpan(8, 4));
        if (12 + asciiCount > tagData.Length) return null;
        int len = (int)asciiCount;
        if (len > 0 && tagData[12 + len - 1] == 0) len--;
        return Encoding.ASCII.GetString(tagData, 12, len);
    }

    private static string? DecodeMluc(byte[] tagData, string preferredLang = "en")
    {
        if (tagData.Length < 16) return null;
        uint numRecords = BinaryPrimitives.ReadUInt32BigEndian(tagData.AsSpan(8, 4));
        uint recordSize = BinaryPrimitives.ReadUInt32BigEndian(tagData.AsSpan(12, 4));
        if (recordSize != 12 || numRecords == 0) return null;
        if (16 + numRecords * 12 > tagData.Length) return null;

        int bestOffset = -1, bestLength = 0;
        bool bestIsPreferred = false;
        for (uint i = 0; i < numRecords; i++)
        {
            int recOff = 16 + (int)i * 12;
            string lang = Encoding.ASCII.GetString(tagData, recOff, 2);
            uint strLen = BinaryPrimitives.ReadUInt32BigEndian(tagData.AsSpan(recOff + 4, 4));
            uint strOff = BinaryPrimitives.ReadUInt32BigEndian(tagData.AsSpan(recOff + 8, 4));
            if (strOff + strLen > tagData.Length) continue;
            bool isPreferred = string.Equals(lang, preferredLang, StringComparison.OrdinalIgnoreCase);
            if (bestOffset < 0 || (isPreferred && !bestIsPreferred))
            {
                bestOffset = (int)strOff;
                bestLength = (int)strLen;
                bestIsPreferred = isPreferred;
                if (isPreferred) break;
            }
        }
        if (bestOffset < 0) return null;
        return Encoding.BigEndianUnicode.GetString(tagData, bestOffset, bestLength);
    }

    // ---- Tag-type encoders ----

    private static byte[] EncodeText(string text)
    {
        var ascii = Encoding.ASCII.GetBytes(text + "\0");
        var output = new byte[8 + ascii.Length];
        Encoding.ASCII.GetBytes("text").CopyTo(output, 0);
        ascii.CopyTo(output, 8);
        return output;
    }

    private static byte[] EncodeDesc(string text)
    {
        // ICC v2 textDescriptionType — ASCII portion + zero Unicode +
        // zero ScriptCode (a 67-byte fixed-length ScriptCode field).
        var ascii = Encoding.ASCII.GetBytes(text + "\0");
        // 8 (header) + 4 (ascii count) + N (ascii) + 4 (Unicode lang) +
        // 4 (Unicode count) + 1 (ScriptCode) + 1 (ScriptCode count) + 67
        int totalSize = 8 + 4 + ascii.Length + 4 + 4 + 1 + 1 + 67;
        var output = new byte[totalSize];
        Encoding.ASCII.GetBytes("desc").CopyTo(output, 0);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(8, 4), (uint)ascii.Length);
        ascii.CopyTo(output, 12);
        // Trailing Unicode + ScriptCode fields stay zero.
        return output;
    }

    private static byte[] EncodeMluc(string text, string lang = "en", string country = "US")
    {
        // Single-record mluc: header + 1 record (12 bytes) + UTF-16BE string.
        var stringBytes = Encoding.BigEndianUnicode.GetBytes(text);
        int totalSize = 16 + 12 + stringBytes.Length;
        var output = new byte[totalSize];
        Encoding.ASCII.GetBytes("mluc").CopyTo(output, 0);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(8, 4), 1);   // 1 record
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(12, 4), 12); // record size
        Encoding.ASCII.GetBytes((lang + "  ").Substring(0, 2)).CopyTo(output, 16);
        Encoding.ASCII.GetBytes((country + "  ").Substring(0, 2)).CopyTo(output, 18);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(20, 4), (uint)stringBytes.Length);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(24, 4), 28);  // string offset
        stringBytes.CopyTo(output, 28);
        return output;
    }

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
