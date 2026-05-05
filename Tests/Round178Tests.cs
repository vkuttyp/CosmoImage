using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Loaders;
using CosmoImage.Savers;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 178 — Matlab v5 writer round-trips through
/// <see cref="VipsMatLoader"/>. Mirror of round 21's reader, completing
/// the .mat read/write pair for numeric arrays. Tests pin: 2D UChar
/// (1-band), 3D UChar (RGB), 2D Float, header magic + version, and
/// rejection paths for the loader on what the saver emits.
/// </summary>
public class Round178Tests
{
    private static VipsImage MakeUChar2D(int w, int h)
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
                        addr[x] = (byte)(((reg.Valid.Top + y) * 7 + (reg.Valid.Left + x) * 11) & 0xFF);
                }
                return 0;
            }
        };

    private static VipsImage MakeUChar3D(int w, int h, int bands)
        => new VipsImage
        {
            Width = w, Height = h, Bands = bands, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) =>
            {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                        for (int c = 0; c < bands; c++)
                            addr[x * bands + c] = (byte)((((reg.Valid.Top + y) * 7
                                + (reg.Valid.Left + x) * 11) ^ (c * 31)) & 0xFF);
                }
                return 0;
            }
        };

    private static VipsImage MakeFloat2D(int w, int h)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 1, BandFormat = VipsBandFormat.Float,
            Interpretation = VipsInterpretation.BW,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) =>
            {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        float v = (reg.Valid.Top + y) * 0.05f + (reg.Valid.Left + x) * 0.13f;
                        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(
                            addr.Slice(x * 4, 4), v);
                    }
                }
                return 0;
            }
        };

    private static byte[] ReadAll(VipsImage img)
    {
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, img.Width, img.Height));
        int rowBytes = img.Width * img.Bands * (img.BandFormat == VipsBandFormat.Float ? 4 : 1);
        var bytes = new byte[rowBytes * img.Height];
        for (int y = 0; y < img.Height; y++)
            reg.GetAddress(0, y).Slice(0, rowBytes).CopyTo(bytes.AsSpan(y * rowBytes, rowBytes));
        return bytes;
    }

    private static async Task<byte[]> SaveAsync(VipsImage img)
    {
        using var ms = new MemoryStream();
        var writer = PipeWriter.Create(ms);
        await VipsMatSaver.SaveAsync(img, writer);
        return ms.ToArray();
    }

    private static async Task<VipsImage> LoadAsync(byte[] bytes)
    {
        var src = new PipeVipsSource(PipeReader.Create(new MemoryStream(bytes)));
        var img = await VipsMatLoader.LoadAsync(src);
        Assert.NotNull(img);
        return img!;
    }

    [Fact]
    public async Task Header_HasV5MagicAndVersion()
    {
        var img = MakeUChar2D(4, 4);
        var bytes = await SaveAsync(img);
        Assert.True(bytes.Length >= 128);
        // Bytes 0..3 should be ASCII text starting with "MATLAB".
        Assert.Equal("MATLAB", System.Text.Encoding.ASCII.GetString(bytes, 0, 6));
        // Version 0x0100 little-endian, then "IM" endian marker.
        Assert.Equal(0x00, bytes[124]);
        Assert.Equal(0x01, bytes[125]);
        Assert.Equal((byte)'I', bytes[126]);
        Assert.Equal((byte)'M', bytes[127]);
    }

    [Fact]
    public async Task UChar2D_RoundTripsExactly()
    {
        var src = MakeUChar2D(8, 6);
        var bytes = await SaveAsync(src);
        var loaded = await LoadAsync(bytes);

        Assert.Equal(VipsBandFormat.UChar, loaded.BandFormat);
        Assert.Equal(8, loaded.Width);
        Assert.Equal(6, loaded.Height);
        Assert.Equal(1, loaded.Bands);
        Assert.Equal(ReadAll(src), ReadAll(loaded));
    }

    [Fact]
    public async Task UChar3D_RGB_RoundTripsExactly()
    {
        var src = MakeUChar3D(5, 4, 3);
        var bytes = await SaveAsync(src);
        var loaded = await LoadAsync(bytes);

        Assert.Equal(VipsBandFormat.UChar, loaded.BandFormat);
        Assert.Equal(5, loaded.Width);
        Assert.Equal(4, loaded.Height);
        Assert.Equal(3, loaded.Bands);
        Assert.Equal(ReadAll(src), ReadAll(loaded));
    }

    [Fact]
    public async Task Float2D_RoundTripsExactly()
    {
        var src = MakeFloat2D(6, 4);
        var bytes = await SaveAsync(src);
        var loaded = await LoadAsync(bytes);

        Assert.Equal(VipsBandFormat.Float, loaded.BandFormat);
        Assert.Equal(6, loaded.Width);
        Assert.Equal(4, loaded.Height);
        Assert.Equal(ReadAll(src), ReadAll(loaded));
    }

    [Fact]
    public async Task LoaderRecognisesOurOutput()
    {
        var img = MakeUChar2D(3, 3);
        var bytes = await SaveAsync(img);
        var src = new PipeVipsSource(PipeReader.Create(new MemoryStream(bytes)));
        Assert.True(await VipsMatLoader.IsMatAsync(src));
    }
}
