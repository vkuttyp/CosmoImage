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
