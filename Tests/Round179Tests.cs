using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Loaders;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 179 — BMP and TGA fast-path coverage. The pure-C# loaders
/// already handle paletted, RLE-compressed, and 16bpp variants of
/// both formats; these tests pin the specific decoded pixels for
/// each variant so regressions are caught directly instead of
/// through the format-fallback path.
///
/// <para>Each test crafts the file bytes by hand (no dependencies on
/// disk fixtures) and runs them through the loader, asserting the
/// output Width/Height/Bands and a handful of pixel values that
/// uniquely identify the variant's decode path.</para>
/// </summary>
public class Round179Tests
{
    private static async Task<VipsImage?> LoadBmpAsync(byte[] bytes)
    {
        var src = new PipeVipsSource(PipeReader.Create(new MemoryStream(bytes)));
        return await VipsBmpLoader.LoadAsync(src);
    }

    private static async Task<VipsImage?> LoadTgaAsync(byte[] bytes)
    {
        var src = new PipeVipsSource(PipeReader.Create(new MemoryStream(bytes)));
        return await VipsTgaLoader.LoadAsync(src);
    }

    private static byte[] ReadPixel(VipsImage img, int x, int y)
    {
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, img.Width, img.Height));
        return reg.GetAddress(x, y).Slice(0, img.Bands).ToArray();
    }

    // ---------- BMP tests ----------

    [Fact]
    public async Task Bmp_8bppPaletted_DecodesViaPaletteLookup()
    {
        // 2×2 image with 8-bit palette. Indexes: [0, 1; 2, 3].
        // Palette: 0=red, 1=green, 2=blue, 3=white. (BMP palette is BGRA.)
        var bmp = BuildBmp(
            width: 2, height: 2, bpp: 8, compression: 0,
            palette: new byte[]
            {
                0x00, 0x00, 0xFF, 0x00, // 0: red   (B, G, R, _)
                0x00, 0xFF, 0x00, 0x00, // 1: green
                0xFF, 0x00, 0x00, 0x00, // 2: blue
                0xFF, 0xFF, 0xFF, 0x00, // 3: white
            },
            // BMP rows are padded to 4 bytes, bottom-up. Row 0 (bottom)
            // = [2, 3], row 1 (top) = [0, 1].
            rowsBottomUp: new[]
            {
                new byte[] { 2, 3, 0, 0 }, // padded
                new byte[] { 0, 1, 0, 0 },
            });

        var img = await LoadBmpAsync(bmp);
        Assert.NotNull(img);
        Assert.Equal(2, img!.Width);
        Assert.Equal(2, img.Height);
        Assert.Equal(3, img.Bands);

        Assert.Equal(new byte[] { 0xFF, 0x00, 0x00 }, ReadPixel(img, 0, 0)); // red
        Assert.Equal(new byte[] { 0x00, 0xFF, 0x00 }, ReadPixel(img, 1, 0)); // green
        Assert.Equal(new byte[] { 0x00, 0x00, 0xFF }, ReadPixel(img, 0, 1)); // blue
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF }, ReadPixel(img, 1, 1)); // white
    }

    [Fact]
    public async Task Bmp_16bpp_Bitfields_DecodesRgb555()
    {
        // 16bpp BMP with BI_BITFIELDS compression and standard 5-5-5
        // masks (R=0x7C00, G=0x03E0, B=0x001F). Single pure-red pixel,
        // padded to BMP's 4-byte row stride (1 px × 2 bytes = 2; pad +2).
        // 0x7C00 = R=31, G=0, B=0. 5→8 bit-replication: R = 0xFF.
        ushort red555 = 0x7C00;
        var pixelData = new byte[] { (byte)red555, (byte)(red555 >> 8), 0, 0 };
        var bmp = BuildBmp16Bitfields(
            width: 1, height: 1,
            redMask: 0x7C00u, greenMask: 0x03E0u, blueMask: 0x001Fu,
            pixelBytes: pixelData);

        var img = await LoadBmpAsync(bmp);
        Assert.NotNull(img);
        Assert.Equal(1, img!.Width);
        Assert.Equal(1, img.Height);
        Assert.Equal(3, img.Bands);
        // Red bit-replicated should be 0xFF.
        var pel = ReadPixel(img, 0, 0);
        Assert.Equal(0xFF, pel[0]);
        Assert.Equal(0x00, pel[1]);
        Assert.Equal(0x00, pel[2]);
    }

    // ---------- TGA tests ----------

    [Fact]
    public async Task Tga_8bppPaletted_TopDown_DecodesViaPaletteLookup()
    {
        // 2×2 paletted (image type 1, no RLE). Top-down so we don't
        // need to flip. Palette: 4 entries, 24bpp BGR order.
        // 0=red, 1=green, 2=blue, 3=white.
        // Pixels (top-down): [0, 1; 2, 3].
        var tga = BuildTga(
            imageType: 1,
            width: 2, height: 2, depth: 8,
            descriptor: 0x20, // top-down
            palette: new byte[]
            {
                0x00, 0x00, 0xFF, // 0: red   (B, G, R)
                0x00, 0xFF, 0x00, // 1: green
                0xFF, 0x00, 0x00, // 2: blue
                0xFF, 0xFF, 0xFF, // 3: white
            },
            paletteEntryBits: 24,
            pixelData: new byte[] { 0, 1, 2, 3 });

        var img = await LoadTgaAsync(tga);
        Assert.NotNull(img);
        Assert.Equal(2, img!.Width);
        Assert.Equal(2, img.Height);
        Assert.Equal(3, img.Bands);
        Assert.Equal(new byte[] { 0xFF, 0x00, 0x00 }, ReadPixel(img, 0, 0)); // red
        Assert.Equal(new byte[] { 0x00, 0xFF, 0x00 }, ReadPixel(img, 1, 0)); // green
        Assert.Equal(new byte[] { 0x00, 0x00, 0xFF }, ReadPixel(img, 0, 1)); // blue
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF }, ReadPixel(img, 1, 1)); // white
    }

    [Fact]
    public async Task Tga_16bpp_Rgb555_TopDown_BitReplicates()
    {
        // 1×1 16bpp (image type 2). Pure red 555 = 0x7C00.
        // 5→8 bit-replication on the 5-bit channel: R should be 0xFF.
        ushort red555 = 0x7C00;
        var tga = BuildTga(
            imageType: 2,
            width: 1, height: 1, depth: 16,
            descriptor: 0x20,
            palette: null,
            paletteEntryBits: 0,
            pixelData: new byte[] { (byte)red555, (byte)(red555 >> 8) });

        var img = await LoadTgaAsync(tga);
        Assert.NotNull(img);
        Assert.Equal(3, img!.Bands);
        var pel = ReadPixel(img, 0, 0);
        Assert.Equal(0xFF, pel[0]);
        Assert.Equal(0x00, pel[1]);
        Assert.Equal(0x00, pel[2]);
    }

    // ---------- File-format builders (for the tests above) ----------

    /// <summary>
    /// Build a paletted BMP (BITMAPINFOHEADER, BI_RGB compression).
    /// <paramref name="rowsBottomUp"/> is the raw padded row data in
    /// bottom-up order (BMP convention).
    /// </summary>
    private static byte[] BuildBmp(int width, int height, ushort bpp, uint compression,
        byte[] palette, byte[][] rowsBottomUp)
    {
        const int FileHeaderSize = 14;
        const int DibSize = 40; // BITMAPINFOHEADER
        int paletteBytes = palette.Length;
        int paletteEntries = paletteBytes / 4;
        int pixelOff = FileHeaderSize + DibSize + paletteBytes;
        int pixelLen = 0;
        foreach (var row in rowsBottomUp) pixelLen += row.Length;
        int fileSize = pixelOff + pixelLen;

        var bmp = new byte[fileSize];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(2, 4), (uint)fileSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(10, 4), (uint)pixelOff);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(14, 4), DibSize);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22, 4), height);
        BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(26, 2), 1); // planes
        BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(28, 2), bpp);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(30, 4), compression);
        // colorsUsed must match the actual palette size — otherwise the
        // loader assumes the default 1<<bpp entries and reads OOB.
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(46, 4), (uint)paletteEntries);
        Buffer.BlockCopy(palette, 0, bmp, FileHeaderSize + DibSize, paletteBytes);
        int p = pixelOff;
        foreach (var row in rowsBottomUp)
        {
            Buffer.BlockCopy(row, 0, bmp, p, row.Length);
            p += row.Length;
        }
        return bmp;
    }

    /// <summary>
    /// Build a 16bpp BI_BITFIELDS BMP. Colour masks live in 12 bytes
    /// just after the BITMAPINFOHEADER per spec.
    /// </summary>
    private static byte[] BuildBmp16Bitfields(int width, int height, uint redMask, uint greenMask, uint blueMask,
        byte[] pixelBytes)
    {
        const int FileHeaderSize = 14;
        const int DibSize = 40;
        const int MaskBytes = 12;
        int pixelOff = FileHeaderSize + DibSize + MaskBytes;
        int fileSize = pixelOff + pixelBytes.Length;

        var bmp = new byte[fileSize];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(2, 4), (uint)fileSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(10, 4), (uint)pixelOff);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(14, 4), DibSize);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22, 4), height);
        BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(26, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(28, 2), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(30, 4), 3); // BI_BITFIELDS
        // Masks at offset DibSize.
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(FileHeaderSize + DibSize + 0, 4), redMask);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(FileHeaderSize + DibSize + 4, 4), greenMask);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(FileHeaderSize + DibSize + 8, 4), blueMask);
        Buffer.BlockCopy(pixelBytes, 0, bmp, pixelOff, pixelBytes.Length);
        return bmp;
    }

    /// <summary>
    /// Build a TGA (uncompressed, all variants). 18-byte header + optional
    /// palette + pixel data.
    /// </summary>
    private static byte[] BuildTga(byte imageType, int width, int height, byte depth, byte descriptor,
        byte[]? palette, int paletteEntryBits, byte[] pixelData)
    {
        bool hasPalette = imageType == 1 || imageType == 9;
        int paletteBytes = hasPalette ? palette!.Length : 0;
        int paletteEntries = hasPalette ? paletteBytes / (paletteEntryBits / 8) : 0;
        int totalLen = 18 + paletteBytes + pixelData.Length;
        var tga = new byte[totalLen];

        tga[0] = 0; // ID length
        tga[1] = (byte)(hasPalette ? 1 : 0); // colour-map type
        tga[2] = imageType;
        // Bytes 3..7 are colour-map spec: 2-byte first index, 2-byte length, 1-byte entry size.
        BinaryPrimitives.WriteUInt16LittleEndian(tga.AsSpan(3, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(tga.AsSpan(5, 2), (ushort)paletteEntries);
        tga[7] = (byte)paletteEntryBits;
        BinaryPrimitives.WriteUInt16LittleEndian(tga.AsSpan(8, 2), 0); // X origin
        BinaryPrimitives.WriteUInt16LittleEndian(tga.AsSpan(10, 2), 0); // Y origin
        BinaryPrimitives.WriteUInt16LittleEndian(tga.AsSpan(12, 2), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(tga.AsSpan(14, 2), (ushort)height);
        tga[16] = depth;
        tga[17] = descriptor;
        if (hasPalette) Buffer.BlockCopy(palette!, 0, tga, 18, paletteBytes);
        Buffer.BlockCopy(pixelData, 0, tga, 18 + paletteBytes, pixelData.Length);
        return tga;
    }
}
