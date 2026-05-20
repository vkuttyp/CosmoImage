using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Loaders;
using CosmoImage.Savers;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Pure-C# BMP coverage. Round-trips through the native VipsBmpSaver and
/// VipsBmpLoader across direct-color and paletted variants.
/// </summary>
public class PureCSharpBmpTests
{
    private static IVipsSource SourceFromBytes(byte[] bytes)
        => new PipeVipsSource(PipeReader.Create(new MemoryStream(bytes)));

    private static async Task<byte[]> SaveToBytesAsync(System.Func<PipeWriter, Task> save)
    {
        using var ms = new MemoryStream();
        var writer = PipeWriter.Create(ms, new StreamPipeWriterOptions(leaveOpen: true));
        await save(writer);
        return ms.ToArray();
    }

    private static VipsImage Rgb(int w, int h, System.Func<int, int, byte[]> fill)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 3, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) =>
            {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        var rgb = fill(reg.Valid.Left + x, reg.Valid.Top + y);
                        addr[x * 3] = rgb[0]; addr[x * 3 + 1] = rgb[1]; addr[x * 3 + 2] = rgb[2];
                    }
                }
                return 0;
            }
        };

    private static VipsImage Rgba(int w, int h, System.Func<int, int, byte[]> fill)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 4, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) =>
            {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        var px = fill(reg.Valid.Left + x, reg.Valid.Top + y);
                        addr[x * 4] = px[0]; addr[x * 4 + 1] = px[1]; addr[x * 4 + 2] = px[2]; addr[x * 4 + 3] = px[3];
                    }
                }
                return 0;
            }
        };

    [Fact]
    public async Task RoundTrip_24bpp_Rgb_PreservesPixels()
    {
        var src = Rgb(8, 6, (x, y) => new byte[] { (byte)(x * 30), (byte)(y * 40), 100 });
        var bytes = await SaveToBytesAsync(w => VipsBmpSaver.SaveAsync(src, w));

        // BM magic and 24bpp written.
        Assert.Equal((byte)'B', bytes[0]);
        Assert.Equal((byte)'M', bytes[1]);
        Assert.Equal(24, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28, 2)));

        var decoded = await VipsBmpLoader.LoadAsync(SourceFromBytes(bytes));
        Assert.NotNull(decoded);
        Assert.Equal(8, decoded!.Width);
        Assert.Equal(6, decoded.Height);
        Assert.Equal(3, decoded.Bands);

        using var rs = new VipsRegion(src);
        using var rd = new VipsRegion(decoded);
        rs.Prepare(new VipsRect(0, 0, 8, 6));
        rd.Prepare(new VipsRect(0, 0, 8, 6));
        for (int y = 0; y < 6; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                var sp = rs.GetAddress(x, y);
                var dp = rd.GetAddress(x, y);
                Assert.Equal(sp[0], dp[0]);
                Assert.Equal(sp[1], dp[1]);
                Assert.Equal(sp[2], dp[2]);
            }
        }
    }

    [Fact]
    public async Task RoundTrip_32bpp_Rgba_PreservesAlpha()
    {
        var src = Rgba(4, 4, (x, y) => new byte[] { (byte)(x * 60), (byte)(y * 60), 100, (byte)(x * 30 + y * 30) });
        var bytes = await SaveToBytesAsync(w => VipsBmpSaver.SaveAsync(src, w));
        Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28, 2)));

        var decoded = await VipsBmpLoader.LoadAsync(SourceFromBytes(bytes));
        Assert.NotNull(decoded);
        Assert.Equal(4, decoded!.Bands);

        using var rs = new VipsRegion(src);
        using var rd = new VipsRegion(decoded);
        rs.Prepare(new VipsRect(0, 0, 4, 4));
        rd.Prepare(new VipsRect(0, 0, 4, 4));
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                var sp = rs.GetAddress(x, y);
                var dp = rd.GetAddress(x, y);
                for (int b = 0; b < 4; b++) Assert.Equal(sp[b], dp[b]);
            }
        }
    }

    [Fact]
    public async Task SaveBmp_OneBandGrayscale_ReplicatesToRgb()
    {
        var src = new VipsImage
        {
            Width = 4, Height = 1, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                var addr = reg.GetAddress(0, 0);
                addr[0] = 10; addr[1] = 100; addr[2] = 200; addr[3] = 250;
                return 0;
            }
        };

        var bytes = await SaveToBytesAsync(w => VipsBmpSaver.SaveAsync(src, w));
        var decoded = await VipsBmpLoader.LoadAsync(SourceFromBytes(bytes));
        Assert.NotNull(decoded);
        Assert.Equal(3, decoded!.Bands); // expanded to RGB
        using var reg = new VipsRegion(decoded);
        reg.Prepare(new VipsRect(0, 0, 4, 1));
        // Each pixel: gray replicated to R=G=B.
        Assert.Equal(10, reg.GetAddress(0, 0)[0]);
        Assert.Equal(10, reg.GetAddress(0, 0)[1]);
        Assert.Equal(10, reg.GetAddress(0, 0)[2]);
        Assert.Equal(250, reg.GetAddress(3, 0)[2]);
    }

    [Fact]
    public async Task RoundTrip_HandlesBmpRowPadding()
    {
        // Width × 3 bytes is not a multiple of 4 → row stride includes
        // padding. Width = 5, 24bpp → row = 15 bytes, padded to 16.
        var src = Rgb(5, 3, (x, y) => new byte[] { (byte)x, (byte)y, 50 });
        var bytes = await SaveToBytesAsync(w => VipsBmpSaver.SaveAsync(src, w));

        var decoded = await VipsBmpLoader.LoadAsync(SourceFromBytes(bytes));
        Assert.NotNull(decoded);
        using var reg = new VipsRegion(decoded!);
        reg.Prepare(new VipsRect(0, 0, 5, 3));
        Assert.Equal(0, reg.GetAddress(0, 0)[0]);
        Assert.Equal(0, reg.GetAddress(0, 0)[1]);
        Assert.Equal(4, reg.GetAddress(4, 2)[0]);
        Assert.Equal(2, reg.GetAddress(4, 2)[1]);
        Assert.Equal(50, reg.GetAddress(4, 2)[2]);
    }

    [Fact]
    public async Task RoundTrip_BottomUpRowOrder_ReadsCorrectly()
    {
        // BMPs are bottom-up by default. Distinct per-row fills shake out
        // any row-ordering bug in either the saver or the loader.
        var src = Rgb(4, 4, (x, y) => new byte[] { (byte)y, 0, 0 });
        var bytes = await SaveToBytesAsync(w => VipsBmpSaver.SaveAsync(src, w));

        // Header: positive height = bottom-up (the BMP default we emit).
        int height = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(22, 4));
        Assert.Equal(4, height);

        var decoded = await VipsBmpLoader.LoadAsync(SourceFromBytes(bytes));
        Assert.NotNull(decoded);
        using var reg = new VipsRegion(decoded!);
        reg.Prepare(new VipsRect(0, 0, 4, 4));
        // Row 0 should still have R = 0, row 3 should have R = 3.
        Assert.Equal(0, reg.GetAddress(0, 0)[0]);
        Assert.Equal(1, reg.GetAddress(0, 1)[0]);
        Assert.Equal(2, reg.GetAddress(0, 2)[0]);
        Assert.Equal(3, reg.GetAddress(0, 3)[0]);
    }

    [Fact]
    public async Task TopDownBmp_DecodesWithCorrectRowOrder()
    {
        // Hand-craft a top-down BMP (negative height) to exercise that
        // branch of the loader without going through our saver (which
        // always emits bottom-up).
        const int W = 2, H = 2;
        const int bpp = 24;
        int rowStride = ((W * bpp + 31) / 32) * 4;
        int pixelOffset = 14 + 40;
        int fileSize = pixelOffset + rowStride * H;

        var bytes = new byte[fileSize];
        bytes[0] = (byte)'B'; bytes[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(2, 4), (uint)fileSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(10, 4), (uint)pixelOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(14, 4), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18, 4), W);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22, 4), -H); // negative = top-down
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(26, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28, 2), 24);

        // Row 0 of file = top row of image. BGR pixels: row 0 red, row 1 blue.
        bytes[pixelOffset + 0] = 0; bytes[pixelOffset + 1] = 0; bytes[pixelOffset + 2] = 255; // (0,0) red
        bytes[pixelOffset + 3] = 0; bytes[pixelOffset + 4] = 0; bytes[pixelOffset + 5] = 255; // (1,0) red
        bytes[pixelOffset + rowStride + 0] = 255; bytes[pixelOffset + rowStride + 1] = 0; bytes[pixelOffset + rowStride + 2] = 0; // (0,1) blue
        bytes[pixelOffset + rowStride + 3] = 255; bytes[pixelOffset + rowStride + 4] = 0; bytes[pixelOffset + rowStride + 5] = 0; // (1,1) blue

        var img = await VipsBmpLoader.LoadAsync(SourceFromBytes(bytes));
        Assert.NotNull(img);
        using var reg = new VipsRegion(img!);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        // Top row = red.
        Assert.Equal(255, reg.GetAddress(0, 0)[0]);
        Assert.Equal(0, reg.GetAddress(0, 0)[2]);
        // Bottom row = blue.
        Assert.Equal(0, reg.GetAddress(0, 1)[0]);
        Assert.Equal(255, reg.GetAddress(0, 1)[2]);
    }

    [Fact]
    public async Task PalettedBmp_DecodesNatively()
    {
        const int width = 2;
        const int height = 2;
        const int bpp = 8;
        int pixelOffset = 14 + 40 + 4 * 4;
        int rowStride = 4;
        int fileSize = pixelOffset + rowStride * height;
        var paletted = new byte[fileSize];

        paletted[0] = (byte)'B'; paletted[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(paletted.AsSpan(2, 4), (uint)fileSize);
        BinaryPrimitives.WriteUInt32LittleEndian(paletted.AsSpan(10, 4), (uint)pixelOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(paletted.AsSpan(14, 4), 40);
        BinaryPrimitives.WriteInt32LittleEndian(paletted.AsSpan(18, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(paletted.AsSpan(22, 4), height);
        BinaryPrimitives.WriteUInt16LittleEndian(paletted.AsSpan(26, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(paletted.AsSpan(28, 2), bpp);
        BinaryPrimitives.WriteUInt32LittleEndian(paletted.AsSpan(30, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(paletted.AsSpan(46, 4), 4);

        // Palette entries are BGRA.
        paletted[54] = 0; paletted[55] = 0; paletted[56] = 255; paletted[57] = 0;     // red
        paletted[58] = 0; paletted[59] = 255; paletted[60] = 0; paletted[61] = 0;     // green
        paletted[62] = 255; paletted[63] = 0; paletted[64] = 0; paletted[65] = 0;     // blue
        paletted[66] = 255; paletted[67] = 255; paletted[68] = 255; paletted[69] = 0; // white

        // Bottom row then top row, padded to 4-byte stride.
        paletted[pixelOffset + 0] = 2; // blue
        paletted[pixelOffset + 1] = 3; // white
        paletted[pixelOffset + rowStride + 0] = 0; // red
        paletted[pixelOffset + rowStride + 1] = 1; // green

        var img2 = await VipsBmpLoader.LoadAsync(SourceFromBytes(paletted));
        Assert.NotNull(img2);
        Assert.Equal(width, img2!.Width);
        Assert.Equal(3, img2.Bands);
        using var reg = new VipsRegion(img2);
        reg.Prepare(new VipsRect(0, 0, width, height));
        Assert.Equal(255, reg.GetAddress(0, 0)[0]);
        Assert.Equal(0, reg.GetAddress(0, 0)[1]);
        Assert.Equal(0, reg.GetAddress(0, 0)[2]);
        Assert.Equal(0, reg.GetAddress(1, 0)[0]);
        Assert.Equal(255, reg.GetAddress(1, 0)[1]);
        Assert.Equal(255, reg.GetAddress(1, 1)[0]);
    }
}
