using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Core;
using CosmoImage.Loaders;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 144 — TGA paletted (types 1/9) and 15/16-bit RGB555. These
/// previously fell back to Magick; this round adds them to the pure
/// fast path so most real-world TGA variants decode without a native
/// dependency.
/// </summary>
public class Round144Tests
{
    private static IVipsSource Source(byte[] bytes)
        => new PipeVipsSource(PipeReader.Create(new MemoryStream(bytes)));

    /// <summary>
    /// Build a minimal TGA header. Descriptor's bit 5 = 1 makes pixels
    /// top-to-bottom (matches our test data layout); avoids the post-
    /// decode vertical flip.
    /// </summary>
    private static byte[] Header(byte imageType, ushort cmLen, byte cmEntryBits,
        ushort w, ushort h, byte depth)
    {
        var hdr = new byte[18];
        hdr[1] = (byte)(cmLen > 0 ? 1 : 0);  // colorMapType
        hdr[2] = imageType;
        BinaryPrimitives.WriteUInt16LittleEndian(hdr.AsSpan(5, 2), cmLen);
        hdr[7] = cmEntryBits;
        BinaryPrimitives.WriteUInt16LittleEndian(hdr.AsSpan(12, 2), w);
        BinaryPrimitives.WriteUInt16LittleEndian(hdr.AsSpan(14, 2), h);
        hdr[16] = depth;
        hdr[17] = 0x20;  // top-down
        return hdr;
    }

    [Fact]
    public async Task Type1_Uncompressed_Paletted_Decodes()
    {
        // 4×2 image, 8-color palette of 24-bit BGR, indexed 8-bit.
        ushort w = 4, h = 2;
        var palette = new byte[][]
        {
            new byte[] { 0x00, 0x00, 0x00 }, // 0: black
            new byte[] { 0x00, 0x00, 0xFF }, // 1: red (BGR → R=0xFF)
            new byte[] { 0x00, 0xFF, 0x00 }, // 2: green
            new byte[] { 0xFF, 0x00, 0x00 }, // 3: blue
            new byte[] { 0x00, 0xFF, 0xFF }, // 4: yellow
            new byte[] { 0xFF, 0x00, 0xFF }, // 5: magenta
            new byte[] { 0xFF, 0xFF, 0x00 }, // 6: cyan
            new byte[] { 0xFF, 0xFF, 0xFF }, // 7: white
        };
        var indices = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 };

        using var ms = new MemoryStream();
        ms.Write(Header(imageType: 1, cmLen: 8, cmEntryBits: 24, w, h, depth: 8));
        foreach (var entry in palette) ms.Write(entry);
        ms.Write(indices);
        var bytes = ms.ToArray();

        var img = await VipsTgaLoader.LoadAsync(Source(bytes));
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
        Assert.Equal(3, img.Bands);

        var got = img.PixelsLazy!.Value;
        // Pixel (0,0) = palette[0] = black.
        Assert.Equal(0x00, got[0]); Assert.Equal(0x00, got[1]); Assert.Equal(0x00, got[2]);
        // Pixel (1,0) = palette[1] = red.
        Assert.Equal(0xFF, got[3]); Assert.Equal(0x00, got[4]); Assert.Equal(0x00, got[5]);
        // Pixel (3,0) = palette[3] = blue.
        Assert.Equal(0x00, got[9]); Assert.Equal(0x00, got[10]); Assert.Equal(0xFF, got[11]);
        // Pixel (3,1) = palette[7] = white.
        Assert.Equal(0xFF, got[21]); Assert.Equal(0xFF, got[22]); Assert.Equal(0xFF, got[23]);
    }

    [Fact]
    public async Task Type9_Rle_Paletted_Decodes()
    {
        // 6×1 image: RLE-encoded indices that use one run + one literal.
        ushort w = 6, h = 1;
        var palette = new byte[][]
        {
            new byte[] { 0x00, 0x00, 0x00 }, // 0
            new byte[] { 0x00, 0x00, 0xAA }, // 1: dim red
            new byte[] { 0x00, 0xAA, 0x00 }, // 2: dim green
        };
        // RLE stream: run of 4 × index 1, then literal of 2 (indices 2, 1).
        var rle = new byte[] {
            0x80 | 3,  // run packet, count = 4
            0x01,      // index 1
            0x00 | 1,  // literal packet, count = 2
            0x02,      // index 2
            0x01,      // index 1
        };

        using var ms = new MemoryStream();
        ms.Write(Header(imageType: 9, cmLen: 3, cmEntryBits: 24, w, h, depth: 8));
        foreach (var entry in palette) ms.Write(entry);
        ms.Write(rle);
        var bytes = ms.ToArray();

        var img = await VipsTgaLoader.LoadAsync(Source(bytes));
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);

        var got = img.PixelsLazy!.Value;
        // Pixels 0..3 = index 1 = dim red.
        for (int i = 0; i < 4; i++)
        { Assert.Equal(0xAA, got[i * 3]); Assert.Equal(0x00, got[i * 3 + 1]); Assert.Equal(0x00, got[i * 3 + 2]); }
        // Pixel 4 = index 2 = dim green.
        Assert.Equal(0x00, got[12]); Assert.Equal(0xAA, got[13]); Assert.Equal(0x00, got[14]);
        // Pixel 5 = index 1 = dim red.
        Assert.Equal(0xAA, got[15]); Assert.Equal(0x00, got[16]); Assert.Equal(0x00, got[17]);
    }

    [Fact]
    public async Task Type2_Depth16_Rgb555_Decodes()
    {
        // 4×1 image, 16-bit RGB555. Pack: A(1) R(5) G(5) B(5) bits (high to low).
        ushort w = 4, h = 1;
        // White: R=G=B=0x1F → bit-replicated to 0xFF in each output channel.
        // Red: R=0x1F, G=B=0
        // Green: G=0x1F, R=B=0
        // Black: all zeros
        ushort[] words = {
            (ushort)((0x1F << 10) | (0x1F << 5) | 0x1F),  // white
            (ushort)(0x1F << 10),                          // red
            (ushort)(0x1F << 5),                           // green
            0,                                              // black
        };

        using var ms = new MemoryStream();
        ms.Write(Header(imageType: 2, cmLen: 0, cmEntryBits: 0, w, h, depth: 16));
        Span<byte> wordBuf = stackalloc byte[2];
        foreach (var word in words)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(wordBuf, word);
            ms.Write(wordBuf);
        }
        var bytes = ms.ToArray();

        var img = await VipsTgaLoader.LoadAsync(Source(bytes));
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(3, img.Bands);

        var got = img.PixelsLazy!.Value;
        // Pixel 0 = white (bit-replicated 0x1F → 0xFF).
        Assert.Equal(0xFF, got[0]); Assert.Equal(0xFF, got[1]); Assert.Equal(0xFF, got[2]);
        // Pixel 1 = red.
        Assert.Equal(0xFF, got[3]); Assert.Equal(0x00, got[4]); Assert.Equal(0x00, got[5]);
        // Pixel 2 = green.
        Assert.Equal(0x00, got[6]); Assert.Equal(0xFF, got[7]); Assert.Equal(0x00, got[8]);
        // Pixel 3 = black.
        Assert.Equal(0x00, got[9]); Assert.Equal(0x00, got[10]); Assert.Equal(0x00, got[11]);
    }

    [Fact]
    public async Task Type10_Depth16_Rle_Rgb555_Decodes()
    {
        // 4×1 image, RLE 16-bit. Single run of 4 white pixels.
        ushort w = 4, h = 1;
        ushort whiteWord = (ushort)((0x1F << 10) | (0x1F << 5) | 0x1F);

        using var ms = new MemoryStream();
        ms.Write(Header(imageType: 10, cmLen: 0, cmEntryBits: 0, w, h, depth: 16));
        ms.WriteByte(0x80 | 3);  // RLE run, count = 4
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, whiteWord);
        ms.Write(b);
        var bytes = ms.ToArray();

        var img = await VipsTgaLoader.LoadAsync(Source(bytes));
        Assert.NotNull(img);
        var got = img!.PixelsLazy!.Value;
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(0xFF, got[i * 3]);
            Assert.Equal(0xFF, got[i * 3 + 1]);
            Assert.Equal(0xFF, got[i * 3 + 2]);
        }
    }

    [Fact]
    public async Task Type1_Paletted_32BitColorMap_OutputsRgba()
    {
        // 32-bit color map → 4-band RGBA output.
        ushort w = 2, h = 1;
        // Palette: index 0 = (R=10, G=20, B=30, A=40); index 1 = opaque red.
        var palette = new byte[]
        {
            30, 20, 10, 40,    // BGRA → RGBA(10, 20, 30, 40)
            0, 0, 0xFF, 0xFF,  // BGRA → RGBA(0xFF, 0, 0, 0xFF)
        };
        var indices = new byte[] { 0, 1 };

        using var ms = new MemoryStream();
        ms.Write(Header(imageType: 1, cmLen: 2, cmEntryBits: 32, w, h, depth: 8));
        ms.Write(palette);
        ms.Write(indices);
        var bytes = ms.ToArray();

        var img = await VipsTgaLoader.LoadAsync(Source(bytes));
        Assert.NotNull(img);
        Assert.Equal(4, img!.Bands);
        var got = img.PixelsLazy!.Value;
        Assert.Equal(10, got[0]); Assert.Equal(20, got[1]); Assert.Equal(30, got[2]); Assert.Equal(40, got[3]);
        Assert.Equal(0xFF, got[4]); Assert.Equal(0, got[5]); Assert.Equal(0, got[6]); Assert.Equal(0xFF, got[7]);
    }
}
