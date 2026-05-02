using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Loaders;
using CosmoImage.Savers;
using Xunit;

namespace CosmoImage.Tests;

public class HdrLoaderTests
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

    /// <summary>Build a 3-band Float image with a fill function.</summary>
    private static VipsImage FloatRgb(int w, int h, System.Func<int, int, int, float> fill)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 3, BandFormat = VipsBandFormat.Float,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        for (int bnd = 0; bnd < 3; bnd++)
                        {
                            int gx = reg.Valid.Left + x;
                            int gy = reg.Valid.Top + y;
                            BinaryPrimitives.WriteSingleLittleEndian(
                                addr.Slice((x * 3 + bnd) * 4, 4),
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
    public async Task IsHdr_DetectsRadianceMagic()
    {
        var hdr = System.Text.Encoding.ASCII.GetBytes("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 1 +X 1\n");
        Assert.True(await VipsHdrLoader.IsHdrAsync(SourceFromBytes(hdr)));
    }

    [Fact]
    public async Task IsHdr_RejectsNonHdrInput()
    {
        var notHdr = System.Text.Encoding.ASCII.GetBytes("PNG\r\n\x1a\n... not hdr ...");
        Assert.False(await VipsHdrLoader.IsHdrAsync(SourceFromBytes(notHdr)));
    }

    [Fact]
    public async Task RoundTrip_UniformFloatImage_PreservesValuesWithinPrecision()
    {
        // Use a small image (< 8 wide) so the saver takes the uncompressed
        // scanline path; covers the simpler decoder branch.
        var src = FloatRgb(4, 4, (x, y, b) => b switch { 0 => 0.5f, 1 => 1.0f, _ => 2.0f });
        var bytes = await SaveToBytesAsync(w => VipsHdrSaver.SaveAsync(src, w));

        var loaded = await VipsHdrLoader.LoadAsync(SourceFromBytes(bytes));
        Assert.NotNull(loaded);
        Assert.Equal(VipsBandFormat.Float, loaded!.BandFormat);
        Assert.Equal(4, loaded.Width);
        Assert.Equal(4, loaded.Height);

        using var reg = new VipsRegion(loaded);
        reg.Prepare(new VipsRect(0, 0, 4, 4));
        // RGBE precision is roughly 1 part in 256 of the per-pixel max
        // channel — assert within ~2% absolute tolerance for these values.
        Assert.Equal(0.5f, ReadFloat(reg, 1, 1, 0), 0.02f);
        Assert.Equal(1.0f, ReadFloat(reg, 1, 1, 1), 0.02f);
        Assert.Equal(2.0f, ReadFloat(reg, 1, 1, 2), 0.02f);
    }

    [Fact]
    public async Task RoundTrip_LargerImage_TakesRleScanlinePath()
    {
        // Width > 8 → RLE path activated in the saver, RLE decoder
        // exercised on load.
        var src = FloatRgb(32, 8, (x, y, b) => 0.25f * (b + 1));
        var bytes = await SaveToBytesAsync(w => VipsHdrSaver.SaveAsync(src, w));

        // The marker bytes 0x02 0x02 should appear after the header — a
        // weak but useful smoke test that the RLE path actually fired.
        int headerEnd = -1;
        for (int i = 0; i < bytes.Length - 1; i++)
        {
            if (bytes[i] == (byte)'\n' && i + 1 < bytes.Length && bytes[i + 1] == 0x02)
            {
                headerEnd = i;
                break;
            }
        }
        Assert.True(headerEnd > 0, "expected RLE marker after header");

        var loaded = await VipsHdrLoader.LoadAsync(SourceFromBytes(bytes));
        Assert.NotNull(loaded);
        using var reg = new VipsRegion(loaded!);
        reg.Prepare(new VipsRect(0, 0, 32, 8));
        Assert.Equal(0.25f, ReadFloat(reg, 5, 3, 0), 0.02f);
        Assert.Equal(0.5f, ReadFloat(reg, 5, 3, 1), 0.02f);
        Assert.Equal(0.75f, ReadFloat(reg, 5, 3, 2), 0.02f);
    }

    [Fact]
    public async Task ZeroPixel_DecodesAsAllZero()
    {
        // Hand-craft a minimal HDR file with one black pixel (E=0).
        var header = System.Text.Encoding.ASCII.GetBytes(
            "#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 1 +X 1\n");
        var pixel = new byte[] { 0, 0, 0, 0 }; // R, G, B, E
        var hdr = new byte[header.Length + pixel.Length];
        Buffer.BlockCopy(header, 0, hdr, 0, header.Length);
        Buffer.BlockCopy(pixel, 0, hdr, header.Length, pixel.Length);

        var loaded = await VipsHdrLoader.LoadAsync(SourceFromBytes(hdr));
        Assert.NotNull(loaded);
        using var reg = new VipsRegion(loaded!);
        reg.Prepare(new VipsRect(0, 0, 1, 1));
        Assert.Equal(0f, ReadFloat(reg, 0, 0, 0));
        Assert.Equal(0f, ReadFloat(reg, 0, 0, 1));
        Assert.Equal(0f, ReadFloat(reg, 0, 0, 2));
    }

    [Fact]
    public async Task HdrPipeline_LoadResizeSave_PreservesDynamicRange()
    {
        // A pixel value > 1.0 (HDR territory) survives a resize through the
        // Float pipeline and re-encodes back to a within-tolerance value.
        var src = FloatRgb(16, 16, (x, y, b) => b == 0 ? 4.0f : 0.1f);
        var encoded = await SaveToBytesAsync(w => VipsHdrSaver.SaveAsync(src, w));

        var loaded = await VipsHdrLoader.LoadAsync(SourceFromBytes(encoded));
        Assert.NotNull(loaded);
        var resized = loaded!.Resize(0.5);
        var reEncoded = await SaveToBytesAsync(w => VipsHdrSaver.SaveAsync(resized, w));
        var reloaded = await VipsHdrLoader.LoadAsync(SourceFromBytes(reEncoded));
        Assert.NotNull(reloaded);

        using var reg = new VipsRegion(reloaded!);
        reg.Prepare(new VipsRect(0, 0, reloaded.Width, reloaded.Height));
        // Centre pixel: bilinear of uniform-4.0 → still 4.0; encode/decode
        // preserves it within RGBE precision (~2% relative).
        Assert.Equal(4.0f, ReadFloat(reg, 4, 4, 0), 0.1f);
    }

    [Fact]
    public async Task HeaderMetadata_Captured()
    {
        var hdr = System.Text.Encoding.ASCII.GetBytes(
            "#?RADIANCE\nFORMAT=32-bit_rle_rgbe\nEXPOSURE=2.5\n\n-Y 1 +X 1\n\0\0\0\0");
        var loaded = await VipsHdrLoader.LoadAsync(SourceFromBytes(hdr));
        Assert.NotNull(loaded);
        Assert.Equal("32-bit_rle_rgbe", loaded!.Metadata["hdr:format"]);
        Assert.Equal("2.5", loaded.Metadata["hdr:exposure"]);
    }
}
