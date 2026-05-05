using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.IO.Hashing;
using CosmoImage.Loaders;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 168 — sub-8-bit PNG decoding inside <see cref="PurePngDecoder"/>.
///
/// Replaces the StbImageSharp fallback for 1/2/4-bit greyscale and 1/2/4-bit
/// palette PNGs. Each test crafts a minimal valid PNG byte stream by hand,
/// pushes it through <c>PurePngDecoder.TryDecode</c>, and asserts pixel-exact
/// output.
///
/// Greyscale samples are stretched to 0..255 (×255 / ×85 / ×17 for 1/2/4-bit
/// inputs). Palette indices stay raw and resolve through PLTE on the way out
/// — verified against a hand-built RGB palette.
/// </summary>
public class Round168Tests
{
    [Fact]
    public void Decodes1BitGreyscale()
    {
        // 8×2 image. Row 0: 10101010 → {0xFF, 0, 0xFF, 0, 0xFF, 0, 0xFF, 0}
        // Row 1: 11110000                → {0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0}
        var rows = new byte[][]
        {
            new byte[] { 0b10101010 },
            new byte[] { 0b11110000 },
        };
        var png = BuildPng(width: 8, height: 2, bitDepth: 1, colorType: 0, rows, plte: null);
        var pixels = PurePngDecoder.TryDecode(png, out int channels);
        Assert.NotNull(pixels);
        Assert.Equal(1, channels);
        Assert.Equal(new byte[]
        {
            0xFF, 0, 0xFF, 0, 0xFF, 0, 0xFF, 0,
            0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0,
        }, pixels);
    }

    [Fact]
    public void Decodes2BitGreyscale()
    {
        // 4×1 image, samples {0, 1, 2, 3} → after ×85 scaling: {0, 85, 170, 255}.
        // Packed msb-first: 0b00 01 10 11 = 0x1B.
        var rows = new byte[][] { new byte[] { 0x1B } };
        var png = BuildPng(width: 4, height: 1, bitDepth: 2, colorType: 0, rows, plte: null);
        var pixels = PurePngDecoder.TryDecode(png, out int channels);
        Assert.NotNull(pixels);
        Assert.Equal(1, channels);
        Assert.Equal(new byte[] { 0, 85, 170, 255 }, pixels);
    }

    [Fact]
    public void Decodes4BitGreyscale()
    {
        // 2×2 image, samples {0x0, 0xF, 0x8, 0x4} → ×17 → {0, 255, 136, 68}.
        // Packed: row0 = 0x0F, row1 = 0x84.
        var rows = new byte[][]
        {
            new byte[] { 0x0F },
            new byte[] { 0x84 },
        };
        var png = BuildPng(width: 2, height: 2, bitDepth: 4, colorType: 0, rows, plte: null);
        var pixels = PurePngDecoder.TryDecode(png, out int channels);
        Assert.NotNull(pixels);
        Assert.Equal(1, channels);
        Assert.Equal(new byte[] { 0, 255, 136, 68 }, pixels);
    }

    [Fact]
    public void Decodes4BitPalette()
    {
        // 2×2 palette image, indices {1, 2, 3, 0} packed msb-first as
        // {0x12, 0x30}. PLTE is 4 entries: black / red / green / blue.
        // Expected RGB output: {red, green, blue, black}.
        var rows = new byte[][]
        {
            new byte[] { 0x12 },
            new byte[] { 0x30 },
        };
        var plte = new byte[]
        {
            0, 0, 0,        // index 0: black
            255, 0, 0,      // index 1: red
            0, 255, 0,      // index 2: green
            0, 0, 255,      // index 3: blue
        };
        var png = BuildPng(width: 2, height: 2, bitDepth: 4, colorType: 3, rows, plte: plte);
        var pixels = PurePngDecoder.TryDecode(png, out int channels);
        Assert.NotNull(pixels);
        Assert.Equal(3, channels);
        Assert.Equal(new byte[]
        {
            255, 0, 0,    0, 255, 0,
            0, 0, 255,    0, 0, 0,
        }, pixels);
    }

    [Fact]
    public void Decodes1BitPalette()
    {
        // 4×1, indices {0, 1, 0, 1} packed = 0b01010000 = 0x50.
        // PLTE: index 0 = white, index 1 = orange.
        var rows = new byte[][] { new byte[] { 0b01010000 } };
        var plte = new byte[]
        {
            255, 255, 255,
            255, 128, 0,
        };
        var png = BuildPng(width: 4, height: 1, bitDepth: 1, colorType: 3, rows, plte: plte);
        var pixels = PurePngDecoder.TryDecode(png, out int channels);
        Assert.NotNull(pixels);
        Assert.Equal(3, channels);
        Assert.Equal(new byte[]
        {
            255, 255, 255,
            255, 128, 0,
            255, 255, 255,
            255, 128, 0,
        }, pixels);
    }

    /// <summary>
    /// Build a minimal valid PNG with filter-None rows. Caller supplies one
    /// packed-bits payload per scanline; the IDAT is filter byte (0) +
    /// payload, repeated per row, zlib-compressed.
    /// </summary>
    private static byte[] BuildPng(int width, int height, byte bitDepth, byte colorType,
        byte[][] rows, byte[]? plte)
    {
        Assert.Equal(height, rows.Length);

        using var ms = new MemoryStream();
        // Signature.
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        // IHDR.
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = bitDepth;
        ihdr[9] = colorType;
        ihdr[10] = 0; // compression: deflate
        ihdr[11] = 0; // filter: standard
        ihdr[12] = 0; // interlace: none
        WriteChunk(ms, "IHDR", ihdr);

        if (plte != null) WriteChunk(ms, "PLTE", plte);

        // IDAT: per row, write filter byte (0=None) + packed-bits payload, zlib-compress.
        using var raw = new MemoryStream();
        foreach (var row in rows)
        {
            raw.WriteByte(0);
            raw.Write(row, 0, row.Length);
        }
        var rawBytes = raw.ToArray();
        using var deflated = new MemoryStream();
        using (var z = new ZLibStream(deflated, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(rawBytes, 0, rawBytes.Length);
        WriteChunk(ms, "IDAT", deflated.ToArray());

        // IEND.
        WriteChunk(ms, "IEND", Array.Empty<byte>());

        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);

        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);

        // CRC over type + data, big-endian.
        var crc = new Crc32();
        crc.Append(typeBytes);
        crc.Append(data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc.GetCurrentHashAsUInt32());
        s.Write(crcBytes);
    }
}
