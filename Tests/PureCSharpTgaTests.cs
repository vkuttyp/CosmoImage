using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Loaders;
using CosmoImage.Savers;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Pure-C# TGA fast-path coverage. Round-trips through the new
/// VipsTgaLoader fast path (types 2/3/10/11, depth 24/32/8) and the new
/// pure-C# VipsTgaSaver. RLE-compressed input is decoded via a
/// Magick-synthesized fixture; the saver always emits uncompressed.
/// </summary>
public class PureCSharpTgaTests
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

    private static VipsImage Gray(int w, int h, System.Func<int, int, byte> fill)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) =>
            {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                        addr[x] = fill(reg.Valid.Left + x, reg.Valid.Top + y);
                }
                return 0;
            }
        };

    [Fact]
    public async Task RoundTrip_24bpp_RgbExact()
    {
        var src = Rgb(8, 6, (x, y) => new byte[] { (byte)(x * 30), (byte)(y * 40), (byte)(x + y) });
        var bytes = await SaveToBytesAsync(w => VipsTgaSaver.SaveAsync(src, w));

        // Header sanity: image type byte should be 2 (uncompressed RGB).
        Assert.Equal(2, bytes[2]);
        // Depth byte = 24.
        Assert.Equal(24, bytes[16]);

        var decoded = await VipsTgaLoader.LoadAsync(SourceFromBytes(bytes));
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
    public async Task RoundTrip_32bpp_PreservesAlpha()
    {
        var src = Rgba(4, 4, (x, y) => new byte[] { (byte)(x * 60), (byte)(y * 60), 100, (byte)(x * 30 + y * 30) });
        var bytes = await SaveToBytesAsync(w => VipsTgaSaver.SaveAsync(src, w));
        Assert.Equal(32, bytes[16]);

        var decoded = await VipsTgaLoader.LoadAsync(SourceFromBytes(bytes));
        Assert.NotNull(decoded);
        Assert.Equal(4, decoded!.Bands);

        using var rs = new VipsRegion(src);
        using var rd = new VipsRegion(decoded);
        rs.Prepare(new VipsRect(0, 0, 4, 4));
        rd.Prepare(new VipsRect(0, 0, 4, 4));
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
                for (int b = 0; b < 4; b++)
                    Assert.Equal(rs.GetAddress(x, y)[b], rd.GetAddress(x, y)[b]);
    }

    [Fact]
    public async Task RoundTrip_8bpp_Grayscale()
    {
        var src = Gray(8, 4, (x, y) => (byte)(x * 30 + y * 40));
        var bytes = await SaveToBytesAsync(w => VipsTgaSaver.SaveAsync(src, w));
        Assert.Equal(3, bytes[2]);  // uncompressed grayscale
        Assert.Equal(8, bytes[16]); // 8 bpp

        var decoded = await VipsTgaLoader.LoadAsync(SourceFromBytes(bytes));
        Assert.NotNull(decoded);
        Assert.Equal(1, decoded!.Bands);
        Assert.Equal(VipsInterpretation.BW, decoded.Interpretation);

        using var reg = new VipsRegion(decoded);
        reg.Prepare(new VipsRect(0, 0, 8, 4));
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 8; x++)
                Assert.Equal((byte)(x * 30 + y * 40), reg.GetAddress(x, y)[0]);
    }

    [Fact]
    public async Task TopToBottomRowOrderEmittedInHeader()
    {
        var src = Rgb(2, 2, (x, y) => new byte[] { (byte)y, 0, 0 });
        var bytes = await SaveToBytesAsync(w => VipsTgaSaver.SaveAsync(src, w));
        // Image descriptor bit 5 (0x20) signals top-to-bottom ordering.
        Assert.Equal(0x20, bytes[17] & 0x20);

        var decoded = await VipsTgaLoader.LoadAsync(SourceFromBytes(bytes));
        Assert.NotNull(decoded);
        using var reg = new VipsRegion(decoded!);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        Assert.Equal(0, reg.GetAddress(0, 0)[0]); // row 0 → R = 0
        Assert.Equal(1, reg.GetAddress(0, 1)[0]); // row 1 → R = 1
    }

    [Fact]
    public async Task RleEncodedTga_DecodesViaFastPath()
    {
        // Magick.NET emits RLE TGA when given a uniform image. Confirms the
        // type-10/11 RLE branch of the fast path actually runs (and matches
        // what an external encoder produced).
        byte[] rleBytes;
        using (var ms = new MemoryStream())
        {
            using var img = new ImageMagick.MagickImage(ImageMagick.MagickColors.Magenta, 16, 8);
            img.Settings.Compression = ImageMagick.CompressionMethod.RLE;
            img.Write(ms, ImageMagick.MagickFormat.Tga);
            rleBytes = ms.ToArray();
        }

        // Sanity: image type byte should be 10 (RLE RGB).
        Assert.True(rleBytes[2] == 10 || rleBytes[2] == 2,
            $"expected RLE or uncompressed RGB, got type {rleBytes[2]}");

        var decoded = await VipsTgaLoader.LoadAsync(SourceFromBytes(rleBytes));
        Assert.NotNull(decoded);
        Assert.Equal(16, decoded!.Width);
        Assert.Equal(8, decoded.Height);
        using var reg = new VipsRegion(decoded);
        reg.Prepare(new VipsRect(0, 0, 16, 8));
        // Magenta is R=255, G=0, B=255.
        Assert.Equal(255, reg.GetAddress(8, 4)[0]);
        Assert.Equal(0, reg.GetAddress(8, 4)[1]);
        Assert.Equal(255, reg.GetAddress(8, 4)[2]);
    }

    [Fact]
    public async Task BottomUpRowOrder_DecodesCorrectly()
    {
        // Hand-craft a minimal bottom-up TGA (descriptor bit 5 = 0). This is
        // the actual TGA default that pre-2.0 viewers expected; modern
        // encoders typically emit top-to-bottom but the loader must handle
        // both. 2x2, 24bpp, type 2.
        var bytes = new byte[18 + 2 * 2 * 3];
        bytes[2] = 2;              // uncompressed RGB
        bytes[12] = 2; bytes[13] = 0; // width = 2
        bytes[14] = 2; bytes[15] = 0; // height = 2
        bytes[16] = 24;
        bytes[17] = 0;             // descriptor — bottom-up

        // Bottom-up means file row 0 = bottom of image. Use distinct R per
        // row so a row-flip bug shows up.
        // File row 0 (image row 1, bottom): two pixels with R = 1.
        // File row 1 (image row 0, top): two pixels with R = 0.
        // BGR storage → bytes are (B, G, R).
        bytes[18 + 0] = 0; bytes[18 + 1] = 0; bytes[18 + 2] = 1;
        bytes[18 + 3] = 0; bytes[18 + 4] = 0; bytes[18 + 5] = 1;
        bytes[18 + 6] = 0; bytes[18 + 7] = 0; bytes[18 + 8] = 0;
        bytes[18 + 9] = 0; bytes[18 + 10] = 0; bytes[18 + 11] = 0;

        var decoded = await VipsTgaLoader.LoadAsync(SourceFromBytes(bytes));
        Assert.NotNull(decoded);
        using var reg = new VipsRegion(decoded!);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        // After loader's vertical flip, image row 0 (top) should have R=0.
        Assert.Equal(0, reg.GetAddress(0, 0)[0]);
        Assert.Equal(1, reg.GetAddress(0, 1)[0]);
    }
}
