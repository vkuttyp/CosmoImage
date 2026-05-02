using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Loaders;
using CosmoImage.Savers;
using Xunit;

namespace CosmoImage.Tests;

public class FitsLoaderTests
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

    private static VipsImage UCharImage(int w, int h, byte value, int bands = 1)
        => new VipsImage
        {
            Width = w, Height = h, Bands = bands, BandFormat = VipsBandFormat.UChar,
            Interpretation = bands == 1 ? VipsInterpretation.BW : VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int i = 0; i < reg.Valid.Width * bands; i++) addr[i] = value;
                }
                return 0;
            }
        };

    private static VipsImage FloatImage(int w, int h, int bands, System.Func<int, int, int, float> fill)
        => new VipsImage
        {
            Width = w, Height = h, Bands = bands, BandFormat = VipsBandFormat.Float,
            Interpretation = bands == 1 ? VipsInterpretation.BW : VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        for (int bnd = 0; bnd < bands; bnd++)
                        {
                            int gx = reg.Valid.Left + x;
                            int gy = reg.Valid.Top + y;
                            BinaryPrimitives.WriteSingleLittleEndian(
                                addr.Slice((x * bands + bnd) * 4, 4),
                                fill(gx, gy, bnd));
                        }
                    }
                }
                return 0;
            }
        };

    private static float ReadFloat(VipsRegion reg, int x, int y, int bnd)
        => BinaryPrimitives.ReadSingleLittleEndian(reg.GetAddress(x, y).Slice(bnd * 4, 4));

    [Fact]
    public async Task IsFits_DetectsSimpleMagic()
    {
        var bytes = await SaveToBytesAsync(w => VipsFitsSaver.SaveAsync(UCharImage(2, 2, 50), w));
        Assert.True(await VipsFitsLoader.IsFitsAsync(SourceFromBytes(bytes)));
    }

    [Fact]
    public async Task IsFits_RejectsNonFits()
    {
        var notFits = System.Text.Encoding.ASCII.GetBytes("PNG\r\n\x1a\nnot FITS");
        Assert.False(await VipsFitsLoader.IsFitsAsync(SourceFromBytes(notFits)));
    }

    [Fact]
    public async Task RoundTrip_UCharGrayscale_PreservesPixels()
    {
        var src = UCharImage(8, 6, value: 175);
        var bytes = await SaveToBytesAsync(w => VipsFitsSaver.SaveAsync(src, w));

        var loaded = await VipsFitsLoader.LoadAsync(SourceFromBytes(bytes));
        Assert.NotNull(loaded);
        Assert.Equal(8, loaded!.Width);
        Assert.Equal(6, loaded.Height);
        Assert.Equal(1, loaded.Bands);
        Assert.Equal(VipsBandFormat.UChar, loaded.BandFormat);

        using var reg = new VipsRegion(loaded);
        reg.Prepare(new VipsRect(0, 0, 8, 6));
        Assert.Equal(175, reg.GetAddress(3, 3)[0]);
    }

    [Fact]
    public async Task RoundTrip_FloatImage_PreservesValues()
    {
        var src = FloatImage(4, 3, 1, (x, y, b) => 0.25f * x + 0.5f * y + 1.0f);
        var bytes = await SaveToBytesAsync(w => VipsFitsSaver.SaveAsync(src, w));

        var loaded = await VipsFitsLoader.LoadAsync(SourceFromBytes(bytes));
        Assert.NotNull(loaded);
        Assert.Equal(VipsBandFormat.Float, loaded!.BandFormat);
        using var reg = new VipsRegion(loaded);
        reg.Prepare(new VipsRect(0, 0, 4, 3));
        Assert.Equal(0.25f * 2 + 0.5f * 1 + 1.0f, ReadFloat(reg, 2, 1, 0), 1e-6f);
    }

    [Fact]
    public async Task RoundTrip_RgbImage_PlanarLayoutIsTransposed()
    {
        // Distinct per-band fills so a planar/interleaved confusion would
        // immediately show up as wrong band values per pixel.
        var src = FloatImage(4, 3, 3, (x, y, b) => b switch { 0 => 1.0f, 1 => 2.0f, _ => 3.0f });
        var bytes = await SaveToBytesAsync(w => VipsFitsSaver.SaveAsync(src, w));

        var loaded = await VipsFitsLoader.LoadAsync(SourceFromBytes(bytes));
        Assert.NotNull(loaded);
        Assert.Equal(3, loaded!.Bands);
        Assert.Equal(VipsBandFormat.Float, loaded.BandFormat);

        using var reg = new VipsRegion(loaded);
        reg.Prepare(new VipsRect(0, 0, 4, 3));
        Assert.Equal(1.0f, ReadFloat(reg, 1, 1, 0));
        Assert.Equal(2.0f, ReadFloat(reg, 1, 1, 1));
        Assert.Equal(3.0f, ReadFloat(reg, 1, 1, 2));
    }

    [Fact]
    public async Task FitsHeaderCards_RoundTripViaMetadata()
    {
        var src = UCharImage(2, 2, 100);
        src.Metadata["fits:OBJECT"] = "M31";
        src.Metadata["fits:OBSERVER"] = "Webb";
        var bytes = await SaveToBytesAsync(w => VipsFitsSaver.SaveAsync(src, w));

        var loaded = await VipsFitsLoader.LoadAsync(SourceFromBytes(bytes));
        Assert.NotNull(loaded);
        Assert.Equal("M31", loaded!.Metadata["fits:OBJECT"]);
        Assert.Equal("Webb", loaded.Metadata["fits:OBSERVER"]);
    }

    [Fact]
    public async Task BscaleBzero_OnLoad_AppliesLinearTransform()
    {
        // Hand-craft a BITPIX=16 image with BSCALE=2, BZERO=10. A stored
        // sample of 5 should decode to 5 * 2 + 10 = 20.
        const int W = 2, H = 1;
        var header = new System.Text.StringBuilder();
        // 80-char cards, padded.
        void Card(string c) => header.Append(c.PadRight(80));
        Card("SIMPLE  =                    T / Standard FITS");
        Card("BITPIX  =                   16 / Bits per sample");
        Card("NAXIS   =                    2 / Number of axes");
        Card("NAXIS1  =                    2 / Image width");
        Card("NAXIS2  =                    1 / Image height");
        Card("BSCALE  =       2.00000000E+00 / Pixel scale");
        Card("BZERO   =       1.00000000E+01 / Pixel offset");
        Card("END");
        // Pad to 2880-byte block.
        while (header.Length % 2880 != 0) header.Append(' ');

        var headerBytes = System.Text.Encoding.ASCII.GetBytes(header.ToString());
        var pixelBytes = new byte[W * H * 2];
        // Big-endian int16 sample = 5.
        BinaryPrimitives.WriteInt16BigEndian(pixelBytes.AsSpan(0, 2), 5);
        BinaryPrimitives.WriteInt16BigEndian(pixelBytes.AsSpan(2, 2), 7);

        // Pad pixel data to 2880-byte boundary.
        var padded = new byte[headerBytes.Length + 2880];
        Array.Copy(headerBytes, padded, headerBytes.Length);
        Array.Copy(pixelBytes, 0, padded, headerBytes.Length, pixelBytes.Length);

        var loaded = await VipsFitsLoader.LoadAsync(SourceFromBytes(padded));
        Assert.NotNull(loaded);
        Assert.Equal(VipsBandFormat.Float, loaded!.BandFormat); // promoted because of BSCALE
        using var reg = new VipsRegion(loaded);
        reg.Prepare(new VipsRect(0, 0, 2, 1));
        Assert.Equal(20.0f, ReadFloat(reg, 0, 0, 0), 1e-6f); // 5*2+10
        Assert.Equal(24.0f, ReadFloat(reg, 1, 0, 0), 1e-6f); // 7*2+10
    }

    [Fact]
    public async Task FitsPipeline_LoadResizeSave_StaysInFloat()
    {
        // FITS → load → resize → FITS round-trip preserves Float precision.
        var src = FloatImage(8, 8, 1, (x, y, b) => 100.0f);
        var encoded = await SaveToBytesAsync(w => VipsFitsSaver.SaveAsync(src, w));

        var loaded = await VipsFitsLoader.LoadAsync(SourceFromBytes(encoded));
        Assert.NotNull(loaded);
        var resized = loaded!.Resize(0.5);
        var reEncoded = await SaveToBytesAsync(w => VipsFitsSaver.SaveAsync(resized, w));
        var reloaded = await VipsFitsLoader.LoadAsync(SourceFromBytes(reEncoded));
        Assert.NotNull(reloaded);
        Assert.Equal(VipsBandFormat.Float, reloaded!.BandFormat);

        using var reg = new VipsRegion(reloaded);
        reg.Prepare(new VipsRect(0, 0, reloaded.Width, reloaded.Height));
        Assert.Equal(100.0f, ReadFloat(reg, 1, 1, 0), 1e-3f);
    }

    [Fact]
    public async Task SavedHeader_StartsAtBlockBoundary_DataAt2880()
    {
        var src = UCharImage(2, 2, 50);
        var bytes = await SaveToBytesAsync(w => VipsFitsSaver.SaveAsync(src, w));
        // First 2880 bytes are the header block.
        Assert.True(bytes.Length >= 2880);
        // SIMPLE card at the start.
        var firstCard = System.Text.Encoding.ASCII.GetString(bytes, 0, 30);
        Assert.StartsWith("SIMPLE  =", firstCard);
        // END must appear before byte 2880.
        var headerText = System.Text.Encoding.ASCII.GetString(bytes, 0, 2880);
        Assert.Contains("END", headerText);
    }
}
