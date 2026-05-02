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
