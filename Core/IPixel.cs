using System;
using System.Runtime.InteropServices;

namespace CosmoImage.Core;

/// <summary>
/// Compile-time pixel descriptor used by <see cref="TypedImage{TPixel}"/> for
/// strongly-typed pixel access. Implementations are packed structs whose
/// in-memory layout matches the on-disk byte layout of one pixel — that lets
/// <see cref="System.Runtime.InteropServices.MemoryMarshal.Cast{TFrom, TTo}"/>
/// reinterpret a contiguous <c>byte[]</c> row as a <c>Span&lt;TPixel&gt;</c>
/// with no copy.
///
/// Only UChar band-format pixels are defined initially. Float, Short and
/// Complex variants will land when the architectural Float-throughout work
/// in <c>TODO_PARITY.md</c> is undertaken.
/// </summary>
public interface IPixel<TSelf> where TSelf : struct, IPixel<TSelf>
{
    /// <summary>Bands per pixel — must match <see cref="VipsImage.Bands"/>.</summary>
    static abstract int BandCount { get; }

    /// <summary>Band format — must match <see cref="VipsImage.BandFormat"/>.</summary>
    static abstract VipsBandFormat BandFormat { get; }
}

/// <summary>Single-band 8-bit grayscale.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct L8 : IPixel<L8>
{
    public byte L;
    public L8(byte l) { L = l; }
    public static int BandCount => 1;
    public static VipsBandFormat BandFormat => VipsBandFormat.UChar;
}

/// <summary>2-band 8-bit grayscale + alpha.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct La16 : IPixel<La16>
{
    public byte L;
    public byte A;
    public La16(byte l, byte a) { L = l; A = a; }
    public static int BandCount => 2;
    public static VipsBandFormat BandFormat => VipsBandFormat.UChar;
}

/// <summary>3-band 8-bit RGB (no alpha).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Rgb24 : IPixel<Rgb24>
{
    public byte R;
    public byte G;
    public byte B;
    public Rgb24(byte r, byte g, byte b) { R = r; G = g; B = b; }
    public static int BandCount => 3;
    public static VipsBandFormat BandFormat => VipsBandFormat.UChar;
}

/// <summary>4-band 8-bit RGBA — straight (non-premultiplied) alpha.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Rgba32 : IPixel<Rgba32>
{
    public byte R;
    public byte G;
    public byte B;
    public byte A;
    public Rgba32(byte r, byte g, byte b, byte a) { R = r; G = g; B = b; A = a; }
    public static int BandCount => 4;
    public static VipsBandFormat BandFormat => VipsBandFormat.UChar;
}

/// <summary>
/// 3-band 8-bit BGR — channel order swapped vs <see cref="Rgb24"/>.
/// The byte layout BMP / TGA / DIB-style readers see directly. Same
/// <c>BandCount</c> and <c>BandFormat</c> as <see cref="Rgb24"/>, so
/// <see cref="TypedImage{TPixel}"/> can reinterpret either struct
/// over the same image — choose whichever matches the underlying
/// byte order.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Bgr24 : IPixel<Bgr24>
{
    public byte B;
    public byte G;
    public byte R;
    public Bgr24(byte b, byte g, byte r) { B = b; G = g; R = r; }
    public static int BandCount => 3;
    public static VipsBandFormat BandFormat => VipsBandFormat.UChar;
}

/// <summary>
/// 4-band 8-bit BGRA — channel order swapped vs <see cref="Rgba32"/>.
/// What Windows GDI / WIC / Direct2D consume natively.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Bgra32 : IPixel<Bgra32>
{
    public byte B;
    public byte G;
    public byte R;
    public byte A;
    public Bgra32(byte b, byte g, byte r, byte a) { B = b; G = g; R = r; A = a; }
    public static int BandCount => 4;
    public static VipsBandFormat BandFormat => VipsBandFormat.UChar;
}

/// <summary>
/// 4-band 8-bit ARGB — alpha-first variant. Common in legacy
/// Win32-bitmap and some Java BufferedImage layouts.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Argb32 : IPixel<Argb32>
{
    public byte A;
    public byte R;
    public byte G;
    public byte B;
    public Argb32(byte a, byte r, byte g, byte b) { A = a; R = r; G = g; B = b; }
    public static int BandCount => 4;
    public static VipsBandFormat BandFormat => VipsBandFormat.UChar;
}

/// <summary>1-band 16-bit grayscale. Source format for medical imaging,
/// HDR luminance, and astronomy.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct L16 : IPixel<L16>
{
    public ushort L;
    public L16(ushort l) { L = l; }
    public static int BandCount => 1;
    public static VipsBandFormat BandFormat => VipsBandFormat.UShort;
}

/// <summary>4-band 16-bit RGBA. The wide-gamut workhorse — what
/// Photoshop / Capture One / RAW pipelines hand off in.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Rgba64 : IPixel<Rgba64>
{
    public ushort R;
    public ushort G;
    public ushort B;
    public ushort A;
    public Rgba64(ushort r, ushort g, ushort b, ushort a) { R = r; G = g; B = b; A = a; }
    public static int BandCount => 4;
    public static VipsBandFormat BandFormat => VipsBandFormat.UShort;
}

/// <summary>3-band 16-bit RGB. The 48-bit photo-printing format.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Rgb48 : IPixel<Rgb48>
{
    public ushort R;
    public ushort G;
    public ushort B;
    public Rgb48(ushort r, ushort g, ushort b) { R = r; G = g; B = b; }
    public static int BandCount => 3;
    public static VipsBandFormat BandFormat => VipsBandFormat.UShort;
}

/// <summary>2-band 16-bit grayscale + alpha (8-bit each).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct La32 : IPixel<La32>
{
    public ushort L;
    public ushort A;
    public La32(ushort l, ushort a) { L = l; A = a; }
    public static int BandCount => 2;
    public static VipsBandFormat BandFormat => VipsBandFormat.UShort;
}

/// <summary>
/// Single-band 8-bit alpha. Same byte layout as <see cref="L8"/>; the
/// distinct struct lets typed pipelines model "this is alpha, not
/// luminance" semantically.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct A8 : IPixel<A8>
{
    public byte A;
    public A8(byte a) { A = a; }
    public static int BandCount => 1;
    public static VipsBandFormat BandFormat => VipsBandFormat.UChar;
}

/// <summary>2-band 16-bit (R, G). The standard normal-map / data-texture
/// channel layout (e.g. derivative maps, height + roughness).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Rg32 : IPixel<Rg32>
{
    public ushort R;
    public ushort G;
    public Rg32(ushort r, ushort g) { R = r; G = g; }
    public static int BandCount => 2;
    public static VipsBandFormat BandFormat => VipsBandFormat.UShort;
}

/// <summary>
/// 16-bit packed BGR with 5/6/5 bit allocation: <c>BBBBB GGGGGG RRRRR</c>
/// from MSB to LSB. The classic Direct3D / embedded-display format —
/// 16-bit colour without alpha. Stored as a single <c>ushort</c>;
/// the per-channel <c>R</c>/<c>G</c>/<c>B</c> properties expand each
/// field to an 8-bit value via standard bit-replication.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Bgr565 : IPixel<Bgr565>
{
    public ushort Packed;

    public Bgr565(ushort packed) { Packed = packed; }

    /// <summary>Build from 8-bit RGB by truncating to 5/6/5.</summary>
    public Bgr565(byte r, byte g, byte b)
    {
        Packed = (ushort)(((b >> 3) << 11) | ((g >> 2) << 5) | (r >> 3));
    }

    public byte R => Expand5((Packed >> 0) & 0x1F);
    public byte G => Expand6((Packed >> 5) & 0x3F);
    public byte B => Expand5((Packed >> 11) & 0x1F);

    /// <summary>5-bit field → 8-bit via bit-replication (val · 255 / 31).</summary>
    private static byte Expand5(int v) => (byte)((v << 3) | (v >> 2));
    private static byte Expand6(int v) => (byte)((v << 2) | (v >> 4));

    public static int BandCount => 1;
    public static VipsBandFormat BandFormat => VipsBandFormat.UShort;
}

/// <summary>
/// 16-bit packed ARGB with 4 bits per channel:
/// <c>AAAA RRRR GGGG BBBB</c> from MSB to LSB. Common in older
/// Direct3D and Android UI buffers where 4-bit alpha is enough.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Bgra4444 : IPixel<Bgra4444>
{
    public ushort Packed;

    public Bgra4444(ushort packed) { Packed = packed; }

    public Bgra4444(byte r, byte g, byte b, byte a)
    {
        Packed = (ushort)(
            ((a >> 4) << 12) | ((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4));
    }

    public byte B => Expand4((Packed >> 0) & 0xF);
    public byte G => Expand4((Packed >> 4) & 0xF);
    public byte R => Expand4((Packed >> 8) & 0xF);
    public byte A => Expand4((Packed >> 12) & 0xF);

    /// <summary>4-bit field → 8-bit via duplication (val · 0x11).</summary>
    private static byte Expand4(int v) => (byte)((v << 4) | v);

    public static int BandCount => 1;
    public static VipsBandFormat BandFormat => VipsBandFormat.UShort;
}

/// <summary>
/// 16-bit packed ARGB with 5/5/5 bits per colour channel + 1 bit
/// alpha: <c>A RRRRR GGGGG BBBBB</c> from MSB to LSB. The "alpha test"
/// format from older fixed-function GPU pipelines — alpha is binary
/// (transparent / opaque), no blending.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Bgra5551 : IPixel<Bgra5551>
{
    public ushort Packed;

    public Bgra5551(ushort packed) { Packed = packed; }

    /// <summary>Build from 8-bit RGBA. Alpha &gt;= 128 is treated as opaque.</summary>
    public Bgra5551(byte r, byte g, byte b, byte a)
    {
        Packed = (ushort)(
            ((a >= 128 ? 1 : 0) << 15) |
            ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3));
    }

    public byte B => Expand5((Packed >> 0) & 0x1F);
    public byte G => Expand5((Packed >> 5) & 0x1F);
    public byte R => Expand5((Packed >> 10) & 0x1F);
    public byte A => (Packed & 0x8000) != 0 ? (byte)255 : (byte)0;

    private static byte Expand5(int v) => (byte)((v << 3) | (v >> 2));

    public static int BandCount => 1;
    public static VipsBandFormat BandFormat => VipsBandFormat.UShort;
}

/// <summary>
/// 4 unsigned bytes — same layout as <see cref="Rgba32"/> but
/// semantically a generic <c>byte4</c> tuple (no R/G/B/A meaning).
/// What ImageSharp calls <c>Byte4</c>; appears in raw GPU vertex /
/// instance buffers.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Byte4 : IPixel<Byte4>
{
    public byte X;
    public byte Y;
    public byte Z;
    public byte W;
    public Byte4(byte x, byte y, byte z, byte w) { X = x; Y = y; Z = z; W = w; }
    public static int BandCount => 4;
    public static VipsBandFormat BandFormat => VipsBandFormat.UChar;
}

/// <summary>2 signed 16-bit ints. GPU mesh-attribute / data-buffer format.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Short2 : IPixel<Short2>
{
    public short X;
    public short Y;
    public Short2(short x, short y) { X = x; Y = y; }
    public static int BandCount => 2;
    public static VipsBandFormat BandFormat => VipsBandFormat.Short;
}

/// <summary>4 signed 16-bit ints.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Short4 : IPixel<Short4>
{
    public short X;
    public short Y;
    public short Z;
    public short W;
    public Short4(short x, short y, short z, short w) { X = x; Y = y; Z = z; W = w; }
    public static int BandCount => 4;
    public static VipsBandFormat BandFormat => VipsBandFormat.Short;
}

/// <summary>
/// 2 signed bytes with normalised <see cref="X"/> / <see cref="Y"/>
/// accessors in <c>[-1, 1]</c>. The raw bytes are reinterpreted as
/// <c>sbyte</c>; <c>−128</c> maps to <c>−1</c>, <c>0</c> to <c>0</c>,
/// <c>127</c> to <c>+1</c>. ImageSharp <c>NormalizedByte2</c> parity.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct NormalizedByte2 : IPixel<NormalizedByte2>
{
    public byte RawX;
    public byte RawY;

    public NormalizedByte2(float x, float y)
    {
        RawX = (byte)(sbyte)Math.Clamp((int)Math.Round(x * 127), -128, 127);
        RawY = (byte)(sbyte)Math.Clamp((int)Math.Round(y * 127), -128, 127);
    }

    public float X => (sbyte)RawX / 127.0f;
    public float Y => (sbyte)RawY / 127.0f;

    public static int BandCount => 2;
    public static VipsBandFormat BandFormat => VipsBandFormat.UChar;
}

/// <summary>4 signed bytes normalised to <c>[-1, 1]</c>.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct NormalizedByte4 : IPixel<NormalizedByte4>
{
    public byte RawX;
    public byte RawY;
    public byte RawZ;
    public byte RawW;

    public NormalizedByte4(float x, float y, float z, float w)
    {
        RawX = (byte)(sbyte)Math.Clamp((int)Math.Round(x * 127), -128, 127);
        RawY = (byte)(sbyte)Math.Clamp((int)Math.Round(y * 127), -128, 127);
        RawZ = (byte)(sbyte)Math.Clamp((int)Math.Round(z * 127), -128, 127);
        RawW = (byte)(sbyte)Math.Clamp((int)Math.Round(w * 127), -128, 127);
    }

    public float X => (sbyte)RawX / 127.0f;
    public float Y => (sbyte)RawY / 127.0f;
    public float Z => (sbyte)RawZ / 127.0f;
    public float W => (sbyte)RawW / 127.0f;

    public static int BandCount => 4;
    public static VipsBandFormat BandFormat => VipsBandFormat.UChar;
}

/// <summary>2 signed shorts normalised to <c>[-1, 1]</c>.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct NormalizedShort2 : IPixel<NormalizedShort2>
{
    public short RawX;
    public short RawY;

    public NormalizedShort2(float x, float y)
    {
        RawX = (short)Math.Clamp((int)Math.Round(x * 32767), -32768, 32767);
        RawY = (short)Math.Clamp((int)Math.Round(y * 32767), -32768, 32767);
    }

    public float X => RawX / 32767.0f;
    public float Y => RawY / 32767.0f;

    public static int BandCount => 2;
    public static VipsBandFormat BandFormat => VipsBandFormat.Short;
}

/// <summary>4 signed shorts normalised to <c>[-1, 1]</c>.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct NormalizedShort4 : IPixel<NormalizedShort4>
{
    public short RawX;
    public short RawY;
    public short RawZ;
    public short RawW;

    public NormalizedShort4(float x, float y, float z, float w)
    {
        RawX = (short)Math.Clamp((int)Math.Round(x * 32767), -32768, 32767);
        RawY = (short)Math.Clamp((int)Math.Round(y * 32767), -32768, 32767);
        RawZ = (short)Math.Clamp((int)Math.Round(z * 32767), -32768, 32767);
        RawW = (short)Math.Clamp((int)Math.Round(w * 32767), -32768, 32767);
    }

    public float X => RawX / 32767.0f;
    public float Y => RawY / 32767.0f;
    public float Z => RawZ / 32767.0f;
    public float W => RawW / 32767.0f;

    public static int BandCount => 4;
    public static VipsBandFormat BandFormat => VipsBandFormat.Short;
}

/// <summary>
/// 32-bit packed RGBA with 10/10/10/2 bit allocation: 10 bits each
/// for R, G, B and 2 bits for A. The "wide-gamut HDR-lite" format —
/// keeps higher colour precision than 8-bit channels at the same
/// bandwidth.
///
/// <para>Layout (matches ImageSharp): bits 0..9 = R, 10..19 = G,
/// 20..29 = B, 30..31 = A. Channel range R/G/B = 0..1023, A = 0..3.
/// 8-bit accessors expand each field via bit-replication.</para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Rgba1010102 : IPixel<Rgba1010102>
{
    public uint Packed;

    public Rgba1010102(uint packed) { Packed = packed; }

    /// <summary>Build from 8-bit RGBA — RGB scaled to 10 bits, A truncated to 2.</summary>
    public Rgba1010102(byte r, byte g, byte b, byte a)
    {
        uint r10 = (uint)((r << 2) | (r >> 6));   // 8-bit → 10-bit by bit-replication
        uint g10 = (uint)((g << 2) | (g >> 6));
        uint b10 = (uint)((b << 2) | (b >> 6));
        uint a2 = (uint)(a >> 6);                 // 8-bit → 2-bit truncation
        Packed = r10 | (g10 << 10) | (b10 << 20) | (a2 << 30);
    }

    /// <summary>Get the 10-bit red field expanded to 8 bits.</summary>
    public byte R => Expand10((int)((Packed >> 0) & 0x3FF));
    public byte G => Expand10((int)((Packed >> 10) & 0x3FF));
    public byte B => Expand10((int)((Packed >> 20) & 0x3FF));
    /// <summary>Get the 2-bit alpha field expanded to 8 bits.</summary>
    public byte A => Expand2((int)((Packed >> 30) & 0x3));

    /// <summary>10-bit field → 8-bit (drop low 2 bits).</summary>
    private static byte Expand10(int v) => (byte)(v >> 2);
    /// <summary>2-bit field → 8-bit via duplication: 0/85/170/255.</summary>
    private static byte Expand2(int v) => (byte)((v << 6) | (v << 4) | (v << 2) | v);

    public static int BandCount => 1;
    public static VipsBandFormat BandFormat => VipsBandFormat.UInt;
}

/// <summary>
/// Single-band 16-bit IEEE 754 float. The .NET <see cref="Half"/>
/// type is the underlying storage. Used in HDR / wide-gamut pipelines
/// where Float (32-bit) is overkill but UShort can't represent
/// negative values or extended range.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct HalfSingle : IPixel<HalfSingle>
{
    public Half Value;
    public HalfSingle(Half value) { Value = value; }
    public HalfSingle(float value) { Value = (Half)value; }
    public static int BandCount => 1;
    public static VipsBandFormat BandFormat => VipsBandFormat.Half;
}

/// <summary>2-band 16-bit float (X, Y). Common for normalised
/// derivative / gradient maps with extended range.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct HalfVector2 : IPixel<HalfVector2>
{
    public Half X;
    public Half Y;
    public HalfVector2(Half x, Half y) { X = x; Y = y; }
    public HalfVector2(float x, float y) { X = (Half)x; Y = (Half)y; }
    public static int BandCount => 2;
    public static VipsBandFormat BandFormat => VipsBandFormat.Half;
}

/// <summary>4-band 16-bit float RGBA. The HDR-imaging workhorse —
/// half the storage of <c>RgbaVector</c> with enough range for
/// linear-light pipelines.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct HalfVector4 : IPixel<HalfVector4>
{
    public Half X;
    public Half Y;
    public Half Z;
    public Half W;
    public HalfVector4(Half x, Half y, Half z, Half w) { X = x; Y = y; Z = z; W = w; }
    public HalfVector4(float x, float y, float z, float w)
    {
        X = (Half)x; Y = (Half)y; Z = (Half)z; W = (Half)w;
    }
    public static int BandCount => 4;
    public static VipsBandFormat BandFormat => VipsBandFormat.Half;
}

/// <summary>
/// Single-band 32-bit float. <c>L</c> in [0, 1] is the conventional
/// range for linear-light luminance; values outside that range are
/// allowed and round-trip cleanly.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct LFloat : IPixel<LFloat>
{
    public float L;
    public LFloat(float l) { L = l; }
    public static int BandCount => 1;
    public static VipsBandFormat BandFormat => VipsBandFormat.Float;
}

/// <summary>
/// 3-band 32-bit float RGB (linear or sRGB; struct itself is
/// space-agnostic). Mirrors ImageSharp's <c>Rgb</c> typed pixel.
/// Use <see cref="Operations.Color.VipsColorRgb"/> + the
/// <c>Linearize</c> / <c>Delinearize</c> ops when you need explicit
/// gamma handling.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct RgbVector : IPixel<RgbVector>
{
    public float R;
    public float G;
    public float B;
    public RgbVector(float r, float g, float b) { R = r; G = g; B = b; }
    public static int BandCount => 3;
    public static VipsBandFormat BandFormat => VipsBandFormat.Float;
}

/// <summary>
/// 4-band 32-bit float RGBA. Maximum precision for HDR / linear-light
/// pipelines. Mirrors ImageSharp's <c>RgbaVector</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct RgbaVector : IPixel<RgbaVector>
{
    public float R;
    public float G;
    public float B;
    public float A;
    public RgbaVector(float r, float g, float b, float a) { R = r; G = g; B = b; A = a; }
    public static int BandCount => 4;
    public static VipsBandFormat BandFormat => VipsBandFormat.Float;
}

/// <summary>
/// 2-band 32-bit float — luminance + alpha. Float counterpart of
/// <see cref="La16"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct LaVector : IPixel<LaVector>
{
    public float L;
    public float A;
    public LaVector(float l, float a) { L = l; A = a; }
    public static int BandCount => 2;
    public static VipsBandFormat BandFormat => VipsBandFormat.Float;
}
