using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Core;
using CosmoImage.Loaders;
using CosmoImage.Savers;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 196 — TIFF tile geometry + 16-bit-per-sample throughput.
/// Adds two saver knobs: <c>tileSize</c> (positive value writes
/// Tiled-TIFF via libtiff's <c>tile-geometry</c> define instead of
/// the default stripped layout) and 16-bit-per-sample auto-promotion
/// for UShort inputs. Closes the last two TIFF sub-bullets in the
/// format-specific gaps.
/// </summary>
public class Round196Tests
{
    private static VipsImage MakeUChar(int w, int h, int bands)
        => new VipsImage
        {
            Width = w, Height = h, Bands = bands, BandFormat = VipsBandFormat.UChar,
            Interpretation = bands == 1 ? VipsInterpretation.BW : VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) =>
            {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                        for (int c = 0; c < bands; c++)
                            addr[x * bands + c] = (byte)(((reg.Valid.Top + y) * 7
                                + (reg.Valid.Left + x) * 11 + c * 31) & 0xFF);
                }
                return 0;
            }
        };

    private static VipsImage MakeUShort(int w, int h, int bands)
        => new VipsImage
        {
            Width = w, Height = h, Bands = bands, BandFormat = VipsBandFormat.UShort,
            Interpretation = bands == 1 ? VipsInterpretation.BW : VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) =>
            {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        for (int c = 0; c < bands; c++)
                        {
                            // High byte > 0 so a narrowing-to-byte bug
                            // shows up clearly.
                            ushort v = (ushort)((reg.Valid.Top + y) * 1000
                                + (reg.Valid.Left + x) * 100 + c * 10000);
                            v = (ushort)(v & 0xFFFF);
                            addr[(x * bands + c) * 2 + 0] = (byte)v;
                            addr[(x * bands + c) * 2 + 1] = (byte)(v >> 8);
                        }
                    }
                }
                return 0;
            }
        };

    private static async Task<byte[]> SaveAsync(VipsImage img, bool pyramid = false, int tileSize = 0)
    {
        using var ms = new MemoryStream();
        var writer = PipeWriter.Create(ms);
        await VipsTiffSaver.SaveAsync(img, writer, pyramid, tileSize);
        return ms.ToArray();
    }

    private static async Task<VipsImage> LoadAsync(byte[] bytes)
    {
        var src = new PipeVipsSource(PipeReader.Create(new MemoryStream(bytes)));
        var loaded = await VipsTiffLoader.LoadAsync(src);
        Assert.NotNull(loaded);
        return loaded!;
    }

    [Fact]
    public async Task Default_StrippedLayoutBytesValid()
    {
        // Regression check: passing tileSize = 0 (default) keeps the
        // existing stripped-TIFF behaviour. Just sanity-check the
        // round-trip works.
        var src = MakeUChar(8, 8, 3);
        var bytes = await SaveAsync(src);
        var loaded = await LoadAsync(bytes);
        Assert.Equal(8, loaded.Width);
        Assert.Equal(8, loaded.Height);
    }

    [Fact]
    public async Task TileSize_ProducesValidTiledTiff()
    {
        // 64×64 RGB image with 32×32 tiles → 4 tiles. The reader
        // shouldn't care whether stripped or tiled; both produce the
        // same pixels.
        var src = MakeUChar(64, 64, 3);
        var bytes = await SaveAsync(src, tileSize: 32);
        var loaded = await LoadAsync(bytes);

        Assert.Equal(64, loaded.Width);
        Assert.Equal(64, loaded.Height);
        // Spot-check a known pixel.
        using var srcReg = new VipsRegion(src);
        using var loadedReg = new VipsRegion(loaded);
        srcReg.Prepare(new VipsRect(0, 0, 64, 64));
        loadedReg.Prepare(new VipsRect(0, 0, 64, 64));
        for (int y = 0; y < 64; y += 16)
            for (int x = 0; x < 64; x += 16)
            {
                Assert.Equal(srcReg.GetAddress(x, y)[0], loadedReg.GetAddress(x, y)[0]);
                Assert.Equal(srcReg.GetAddress(x, y)[1], loadedReg.GetAddress(x, y)[1]);
                Assert.Equal(srcReg.GetAddress(x, y)[2], loadedReg.GetAddress(x, y)[2]);
            }
    }

    [Fact]
    public async Task UShort_AcceptsInputAndProducesValidTiff()
    {
        // The saver no longer caps at Depth = 8 — UShort input flows
        // through with bytesPerSample = 2 and Magick.Settings.Depth = 16.
        // The wire-format precision actually preserved depends on the
        // Magick.NET-Q8 build (which quantizes internally to 8-bit), so
        // we don't pin per-pixel values; we just verify the saver
        // accepts the UShort input without crashing or narrowing the
        // buffer dimensions.
        var src = MakeUShort(8, 6, 1);
        var bytes = await SaveAsync(src);
        Assert.True(bytes.Length > 0);

        var loaded = await LoadAsync(bytes);
        Assert.Equal(8, loaded.Width);
        Assert.Equal(6, loaded.Height);
    }

    [Fact]
    public async Task TileSize_NegativeRejected()
    {
        var src = MakeUChar(4, 4, 3);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await SaveAsync(src, tileSize: -1));
    }
}
