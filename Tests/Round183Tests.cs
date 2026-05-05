using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Loaders;
using CosmoImage.Savers;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 183 — pure-C# palette-PNG-8 saver. Replaces the Magick-backed
/// path with a quantize-via-octree + hand-emit-PLTE-and-tRNS pipeline.
/// Drops the last Magick dependency in <see cref="VipsPngSaver"/>.
/// Tests verify the wire format (CT 3, PLTE, optional tRNS) and
/// round-trip via <see cref="VipsPngLoader"/> (which routes palette
/// PNGs through the existing pure-C# <c>PurePngDecoder</c>).
/// </summary>
public class Round183Tests
{
    private static VipsImage MakeRgb(int w, int h, Func<int, int, (byte R, byte G, byte B)> pixel)
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
                        var (r, g, bl) = pixel(reg.Valid.Left + x, reg.Valid.Top + y);
                        addr[x * 3 + 0] = r;
                        addr[x * 3 + 1] = g;
                        addr[x * 3 + 2] = bl;
                    }
                }
                return 0;
            }
        };

    private static VipsImage MakeRgba(int w, int h, Func<int, int, (byte R, byte G, byte B, byte A)> pixel)
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
                        var (r, g, bl, al) = pixel(reg.Valid.Left + x, reg.Valid.Top + y);
                        addr[x * 4 + 0] = r;
                        addr[x * 4 + 1] = g;
                        addr[x * 4 + 2] = bl;
                        addr[x * 4 + 3] = al;
                    }
                }
                return 0;
            }
        };

    private static async Task<byte[]> SaveAsync(VipsImage img, int colors)
    {
        using var ms = new MemoryStream();
        var writer = PipeWriter.Create(ms);
        await VipsPngSaver.SaveAsync(img, writer, palette: colors);
        return ms.ToArray();
    }

    private static bool ContainsChunk(byte[] png, string type)
    {
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        int p = 8;
        while (p + 8 <= png.Length)
        {
            int len = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(p, 4));
            bool match = true;
            for (int i = 0; i < 4; i++) if (png[p + 4 + i] != typeBytes[i]) { match = false; break; }
            if (match) return true;
            p += 8 + len + 4;
        }
        return false;
    }

    private static byte ReadIhdrColorType(byte[] png)
    {
        // IHDR is the first chunk after the signature; data starts at offset 16.
        // Layout: width (4) + height (4) + bitDepth (1) + colorType (1) + ...
        return png[8 + 8 + 9];
    }

    [Fact]
    public async Task PalettePng_OutputsCT3WithPLTE()
    {
        var src = MakeRgb(8, 8, (x, y) => ((byte)(x * 32), (byte)(y * 32), 100));
        var bytes = await SaveAsync(src, colors: 16);

        Assert.Equal(3, ReadIhdrColorType(bytes)); // CT 3 = palette
        Assert.True(ContainsChunk(bytes, "PLTE"));
        Assert.True(ContainsChunk(bytes, "IDAT"));
        Assert.True(ContainsChunk(bytes, "IEND"));
        Assert.False(ContainsChunk(bytes, "tRNS")); // no alpha → no tRNS
    }

    [Fact]
    public async Task PalettePng_RgbaInputEmitsTRNS()
    {
        // RGBA with mid-alpha forces a tRNS chunk.
        var src = MakeRgba(4, 4, (x, y) => ((byte)(x * 64), (byte)(y * 64), 0, (byte)(128)));
        var bytes = await SaveAsync(src, colors: 8);

        Assert.Equal(3, ReadIhdrColorType(bytes));
        Assert.True(ContainsChunk(bytes, "PLTE"));
        Assert.True(ContainsChunk(bytes, "tRNS"));
    }

    [Fact]
    public async Task PalettePng_RoundTripsThroughLoader()
    {
        // Use a small palette so the quantizer's reconstruction is
        // close to ground truth. 4×4 with 4 distinct colours stays
        // exact through the round trip.
        var src = MakeRgb(4, 4, (x, y) =>
        {
            int idx = (y * 4 + x) % 4;
            return idx switch
            {
                0 => ((byte)0, (byte)0, (byte)0),
                1 => ((byte)255, (byte)0, (byte)0),
                2 => ((byte)0, (byte)255, (byte)0),
                _ => ((byte)0, (byte)0, (byte)255),
            };
        });
        var bytes = await SaveAsync(src, colors: 4);
        var loadedSrc = new PipeVipsSource(PipeReader.Create(new MemoryStream(bytes)));
        var loaded = await VipsPngLoader.LoadAsync(loadedSrc);
        Assert.NotNull(loaded);

        Assert.Equal(4, loaded!.Width);
        Assert.Equal(4, loaded.Height);
        // PurePngDecoder expands palette PNG to 3-band RGB by default.
        Assert.Equal(3, loaded.Bands);

        using var reg = new VipsRegion(loaded);
        reg.Prepare(new VipsRect(0, 0, 4, 4));
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                var addr = reg.GetAddress(0, y);
                int idx = (y * 4 + x) % 4;
                var expected = idx switch
                {
                    0 => (0, 0, 0),
                    1 => (255, 0, 0),
                    2 => (0, 255, 0),
                    _ => (0, 0, 255),
                };
                Assert.Equal(expected.Item1, addr[x * 3 + 0]);
                Assert.Equal(expected.Item2, addr[x * 3 + 1]);
                Assert.Equal(expected.Item3, addr[x * 3 + 2]);
            }
    }

    [Fact]
    public async Task PalettePng_PreservesIccProfile()
    {
        var src = MakeRgb(4, 4, (x, y) => ((byte)100, (byte)100, (byte)100));
        // Synthetic ICC blob — saver shouldn't introspect the bytes; it
        // just round-trips them through the iCCP chunk.
        var iccBytes = new byte[64];
        for (int i = 0; i < iccBytes.Length; i++) iccBytes[i] = (byte)i;
        src.MetadataBlobs["icc"] = iccBytes;

        var bytes = await SaveAsync(src, colors: 4);
        Assert.True(ContainsChunk(bytes, "iCCP"));
    }

    [Fact]
    public async Task PalettePng_RejectsOutOfRangeColors()
    {
        var src = MakeRgb(4, 4, (x, y) => ((byte)0, (byte)0, (byte)0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await SaveAsync(src, colors: 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await SaveAsync(src, colors: 257));
    }
}
