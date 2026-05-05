using System;
using System.IO;
using System.Threading.Tasks;
using CosmoImage.Core;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 189 — <see cref="DiscBackedSink"/>: spill an image to a temp
/// file, return a new VipsImage whose Generate reads back from that
/// file via concurrent-safe RandomAccess.
///
/// <para>Tests verify: round-trip pixel-exactness; the disc-backed
/// image works as input to downstream ops (Resize, ExtractArea);
/// the temp file gets cleaned up on dispose.</para>
/// </summary>
public class Round189Tests
{
    /// <summary>3-band UChar image with deterministic per-pixel values.</summary>
    private static VipsImage MakeRgb(int w, int h)
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
                        addr[x * 3 + 0] = (byte)((reg.Valid.Left + x) & 0xFF);
                        addr[x * 3 + 1] = (byte)((reg.Valid.Top + y) & 0xFF);
                        addr[x * 3 + 2] = (byte)(((reg.Valid.Left + x) ^ (reg.Valid.Top + y)) & 0xFF);
                    }
                }
                return 0;
            }
        };

    private static byte[] MaterialiseBytes(VipsImage img)
    {
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, img.Width, img.Height));
        int rowBytes = img.Width * img.Bands;
        var bytes = new byte[rowBytes * img.Height];
        for (int y = 0; y < img.Height; y++)
            reg.GetAddress(0, y).Slice(0, rowBytes).CopyTo(bytes.AsSpan(y * rowBytes, rowBytes));
        return bytes;
    }

    [Fact]
    public async Task Spill_RoundTripsExactly()
    {
        var src = MakeRgb(16, 12);
        var srcBytes = MaterialiseBytes(src);

        await using var sink = await DiscBackedSink.CreateAsync(src);
        var spilled = sink.Image;

        Assert.Equal(src.Width, spilled.Width);
        Assert.Equal(src.Height, spilled.Height);
        Assert.Equal(src.Bands, spilled.Bands);
        Assert.Equal(src.BandFormat, spilled.BandFormat);

        var spilledBytes = MaterialiseBytes(spilled);
        Assert.Equal(srcBytes, spilledBytes);
    }

    [Fact]
    public async Task DiscBackedImage_DrivesDownstreamOps()
    {
        // Verify the disc-backed image plays nicely as input to a
        // pipeline op that does its own region preparation.
        var src = MakeRgb(32, 24);
        await using var sink = await DiscBackedSink.CreateAsync(src);

        var resized = sink.Image.Resize(0.5);
        Assert.Equal(16, resized.Width);
        Assert.Equal(12, resized.Height);

        // Force materialisation so the GenerateFn actually pulls from disk.
        var resizedBytes = MaterialiseBytes(resized);
        Assert.Equal(16 * 12 * 3, resizedBytes.Length);
    }

    [Fact]
    public async Task PartialRegion_OnlyReadsRequestedRows()
    {
        // ExtractArea-style reads should hit a sub-rectangle of the
        // backing file; the result must match the source's same region.
        var src = MakeRgb(20, 20);
        await using var sink = await DiscBackedSink.CreateAsync(src);

        var sub = sink.Image.ExtractArea(5, 7, 8, 6);
        Assert.Equal(8, sub.Width);
        Assert.Equal(6, sub.Height);

        // Verify a few sample pixels match the source exactly.
        using var subReg = new VipsRegion(sub);
        subReg.Prepare(new VipsRect(0, 0, 8, 6));
        for (int dy = 0; dy < 6; dy++)
        {
            var subAddr = subReg.GetAddress(0, dy);
            for (int dx = 0; dx < 8; dx++)
            {
                int gx = 5 + dx, gy = 7 + dy;
                Assert.Equal((byte)(gx & 0xFF), subAddr[dx * 3 + 0]);
                Assert.Equal((byte)(gy & 0xFF), subAddr[dx * 3 + 1]);
                Assert.Equal((byte)((gx ^ gy) & 0xFF), subAddr[dx * 3 + 2]);
            }
        }
    }

    [Fact]
    public async Task Dispose_DeletesTempFile()
    {
        var src = MakeRgb(8, 8);
        string path;
        await using (var sink = await DiscBackedSink.CreateAsync(src))
        {
            path = sink.TempPath;
            Assert.True(File.Exists(path));
        }
        // After dispose, the file should be gone.
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Dispose_IdempotentAndSafeAfterDoubleCall()
    {
        var src = MakeRgb(8, 8);
        var sink = await DiscBackedSink.CreateAsync(src);
        await sink.DisposeAsync();
        await sink.DisposeAsync();  // must not throw
    }

    [Fact]
    public async Task ConcurrentReads_AreSafe()
    {
        // Multiple concurrent VipsRegion.Prepare calls on the same
        // disc-backed image should not corrupt each other (RandomAccess
        // is thread-safe per .NET docs).
        var src = MakeRgb(64, 64);
        await using var sink = await DiscBackedSink.CreateAsync(src);

        var tasks = new Task[8];
        for (int i = 0; i < tasks.Length; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                using var reg = new VipsRegion(sink.Image);
                reg.Prepare(new VipsRect(0, 0, sink.Image.Width, sink.Image.Height));
                // Spot-check a few pixels per worker.
                var addr0 = reg.GetAddress(0, 0);
                Assert.Equal(0, addr0[0]);
                Assert.Equal(0, addr0[1]);
                var addr1 = reg.GetAddress(10, 5);
                Assert.Equal(10, addr1[0]);
                Assert.Equal(5, addr1[1]);
            });
        }
        await Task.WhenAll(tasks);
    }
}
