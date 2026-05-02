using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Loaders;
using CosmoImage.Savers;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round-trip tests for the pure-C# QOI codec. QOI is lossless, so any
/// Save→Load chain must preserve pixels exactly. The patterns below
/// deliberately exercise each of the 6 spec ops (RGB / RGBA / INDEX /
/// DIFF / LUMA / RUN) so a regression in encoder dispatch shows up
/// immediately on the decode side.
/// </summary>
public class PureCSharpQoiTests
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

    /// <summary>3-band UChar image with a per-pixel fill function.</summary>
    private static VipsImage Rgb(int w, int h, System.Func<int, int, byte[]> fill)
    {
        return new VipsImage
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
    }

    private static VipsImage Rgba(int w, int h, System.Func<int, int, byte[]> fill)
    {
        return new VipsImage
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
    }

    private static void AssertRoundTrip(VipsImage src, VipsImage decoded)
    {
        Assert.Equal(src.Width, decoded.Width);
        Assert.Equal(src.Height, decoded.Height);
        Assert.Equal(src.Bands, decoded.Bands);

        using var rs = new VipsRegion(src);
        using var rd = new VipsRegion(decoded);
        rs.Prepare(new VipsRect(0, 0, src.Width, src.Height));
        rd.Prepare(new VipsRect(0, 0, decoded.Width, decoded.Height));
        for (int y = 0; y < src.Height; y++)
        {
            for (int x = 0; x < src.Width; x++)
            {
                var sp = rs.GetAddress(x, y);
                var dp = rd.GetAddress(x, y);
                for (int b = 0; b < src.Bands; b++)
                    Assert.Equal(sp[b], dp[b]);
            }
        }
    }

    [Fact]
    public async Task RoundTrip_UniformRgb_HitsRunOp()
    {
        // Uniform image → first pixel emits RGB or DIFF, then a single huge
        // RUN spanning the rest. Round-trip must restore exactly.
        var src = Rgb(16, 16, (x, y) => new byte[] { 50, 100, 150 });
        var bytes = await SaveToBytesAsync(w => VipsQoiSaver.SaveAsync(src, w));
        var decoded = await VipsQoiLoader.LoadAsync(SourceFromBytes(bytes));
        Assert.NotNull(decoded);
        AssertRoundTrip(src, decoded!);
    }

    [Fact]
    public async Task RoundTrip_DiffPattern_HitsDiffOp()
    {
        // Smooth ramp where each pixel differs by ±1 from the previous in
        // all three channels — fits in QOI_OP_DIFF (range -2..1).
        var src = Rgb(8, 1, (x, y) => new byte[] { (byte)(100 + x), (byte)(120 + x), (byte)(140 + x) });
        var bytes = await SaveToBytesAsync(w => VipsQoiSaver.SaveAsync(src, w));
        var decoded = await VipsQoiLoader.LoadAsync(SourceFromBytes(bytes));
        Assert.NotNull(decoded);
        AssertRoundTrip(src, decoded!);
    }

    [Fact]
    public async Task RoundTrip_LumaPattern_HitsLumaOp()
    {
        // Mid-range luminance shifts that exceed DIFF range but fit LUMA.
        // LUMA: dg in [-32, 31], dr-dg in [-8, 7], db-dg in [-8, 7].
        var src = Rgb(8, 1, (x, y) => new byte[]
        {
            (byte)(100 + x * 5),
            (byte)(100 + x * 5),
            (byte)(100 + x * 5),
        });
        var bytes = await SaveToBytesAsync(w => VipsQoiSaver.SaveAsync(src, w));
        var decoded = await VipsQoiLoader.LoadAsync(SourceFromBytes(bytes));
        Assert.NotNull(decoded);
        AssertRoundTrip(src, decoded!);
    }

    [Fact]
    public async Task RoundTrip_LargeJump_HitsRgbOp()
    {
        // Differences too big for DIFF or LUMA → RGB op.
        var src = Rgb(4, 1, (x, y) => x switch
        {
            0 => new byte[] { 10, 200, 50 },
            1 => new byte[] { 200, 30, 250 },
            2 => new byte[] { 50, 200, 10 },
            _ => new byte[] { 0, 0, 255 },
        });
        var bytes = await SaveToBytesAsync(w => VipsQoiSaver.SaveAsync(src, w));
        var decoded = await VipsQoiLoader.LoadAsync(SourceFromBytes(bytes));
        Assert.NotNull(decoded);
        AssertRoundTrip(src, decoded!);
    }

    [Fact]
    public async Task RoundTrip_AlphaChange_HitsRgbaOp()
    {
        // Varying alpha forces RGBA op.
        var src = Rgba(4, 1, (x, y) => new byte[] { 100, 100, 100, (byte)(64 * (x + 1)) });
        var bytes = await SaveToBytesAsync(w => VipsQoiSaver.SaveAsync(src, w));
        var decoded = await VipsQoiLoader.LoadAsync(SourceFromBytes(bytes));
        Assert.NotNull(decoded);
        AssertRoundTrip(src, decoded!);
    }

    [Fact]
    public async Task RoundTrip_RepeatedPalette_HitsIndexOp()
    {
        // Repeating two distinct colours — second occurrence of each colour
        // should hit the hash table and emit INDEX.
        var palette = new[]
        {
            new byte[] { 200, 100, 50 },
            new byte[] { 30, 30, 250 },
        };
        var src = Rgb(8, 1, (x, y) => palette[x % 2]);
        var bytes = await SaveToBytesAsync(w => VipsQoiSaver.SaveAsync(src, w));
        var decoded = await VipsQoiLoader.LoadAsync(SourceFromBytes(bytes));
        Assert.NotNull(decoded);
        AssertRoundTrip(src, decoded!);
    }

    [Fact]
    public async Task RoundTrip_LongerThanMaxRun_SplitsAcrossMultipleRuns()
    {
        // 200 identical pixels — must split into multiple RUN ops since
        // a single RUN can only encode ≤62 pixels.
        var src = Rgb(200, 1, (x, y) => new byte[] { 75, 175, 75 });
        var bytes = await SaveToBytesAsync(w => VipsQoiSaver.SaveAsync(src, w));
        var decoded = await VipsQoiLoader.LoadAsync(SourceFromBytes(bytes));
        Assert.NotNull(decoded);
        AssertRoundTrip(src, decoded!);
    }

    [Fact]
    public async Task RoundTrip_RandomPattern_PreservesEveryPixel()
    {
        // Deterministic pseudo-random pattern — exercises the encoder/
        // decoder state machine with realistic mixed content.
        var rng = new Random(42);
        var data = new byte[16 * 16 * 4];
        rng.NextBytes(data);

        var src = new VipsImage
        {
            Width = 16, Height = 16, Bands = 4, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            PixelsLazy = new Lazy<byte[]>(() => data),
        };

        var bytes = await SaveToBytesAsync(w => VipsQoiSaver.SaveAsync(src, w));
        var decoded = await VipsQoiLoader.LoadAsync(SourceFromBytes(bytes));
        Assert.NotNull(decoded);
        Assert.Equal(16, decoded!.Width);
        Assert.Equal(16, decoded.Height);

        using var reg = new VipsRegion(decoded);
        reg.Prepare(new VipsRect(0, 0, 16, 16));
        for (int y = 0; y < 16; y++)
        {
            var p = reg.GetAddress(0, y);
            for (int x = 0; x < 64; x++) // 16 px × 4 bytes
                Assert.Equal(data[y * 64 + x], p[x]);
        }
    }

    [Fact]
    public async Task SavedHeader_HasCorrectMagicAndDimensions()
    {
        var src = Rgb(7, 11, (x, y) => new byte[] { 1, 2, 3 });
        var bytes = await SaveToBytesAsync(w => VipsQoiSaver.SaveAsync(src, w));
        Assert.Equal((byte)'q', bytes[0]);
        Assert.Equal((byte)'o', bytes[1]);
        Assert.Equal((byte)'i', bytes[2]);
        Assert.Equal((byte)'f', bytes[3]);
        // width = 7 (big-endian uint32 at offset 4)
        Assert.Equal(0, bytes[4]); Assert.Equal(0, bytes[5]); Assert.Equal(0, bytes[6]); Assert.Equal(7, bytes[7]);
        // height = 11
        Assert.Equal(0, bytes[8]); Assert.Equal(0, bytes[9]); Assert.Equal(0, bytes[10]); Assert.Equal(11, bytes[11]);
        Assert.Equal(3, bytes[12]); // channels
    }

    [Fact]
    public async Task EndMarker_PresentAtEndOfStream()
    {
        var src = Rgb(4, 4, (x, y) => new byte[] { 10, 20, 30 });
        var bytes = await SaveToBytesAsync(w => VipsQoiSaver.SaveAsync(src, w));
        // Last 8 bytes: 7 zeros + 0x01.
        for (int i = 0; i < 7; i++) Assert.Equal(0, bytes[bytes.Length - 8 + i]);
        Assert.Equal(1, bytes[bytes.Length - 1]);
    }
}
