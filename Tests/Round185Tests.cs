using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using CosmoImage.Core;
using CosmoImage.Savers;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 185 — <see cref="IVipsTarget"/>: output abstraction symmetric
/// to <see cref="IVipsSource"/>. Each test drives the existing
/// <see cref="VipsPngSaver"/> through the new target via
/// <c>target.AsPipeWriter()</c> and verifies the written bytes match
/// what the same saver produces directly to a MemoryStream.
/// </summary>
public class Round185Tests
{
    private static VipsImage MakeImg(int w, int h)
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
                        addr[x * 3 + 2] = 128;
                    }
                }
                return 0;
            }
        };

    /// <summary>Reference: the same image saved straight to a MemoryStream via PipeWriter.</summary>
    private static async Task<byte[]> SaveReferenceAsync(VipsImage img)
    {
        using var ms = new MemoryStream();
        var writer = PipeWriter.Create(ms);
        await VipsPngSaver.SaveAsync(img, writer);
        return ms.ToArray();
    }

    [Fact]
    public async Task MemoryTarget_CapturesIdenticalBytes()
    {
        var img = MakeImg(8, 8);
        var expected = await SaveReferenceAsync(img);

        await using var target = new MemoryVipsTarget();
        await VipsPngSaver.SaveAsync(img, target.AsPipeWriter());

        var actual = target.ToArray();
        Assert.Equal(expected.Length, actual.Length);
        Assert.Equal(expected, actual);
        Assert.True(target.Position > 0);
    }

    [Fact]
    public async Task StreamTarget_PassesThroughToWrappedStream()
    {
        var img = MakeImg(8, 8);
        var expected = await SaveReferenceAsync(img);

        using var ms = new MemoryStream();
        await using (var target = new StreamVipsTarget(ms, leaveOpen: true))
        {
            await VipsPngSaver.SaveAsync(img, target.AsPipeWriter());
        }

        Assert.Equal(expected, ms.ToArray());
    }

    [Fact]
    public async Task CallbackTarget_AssemblesViaUserDelegate()
    {
        // Custom destination: collect chunks via a delegate, then verify
        // the concatenated bytes match the reference. Mirrors the libvips
        // "write callback" target use case.
        var img = MakeImg(8, 8);
        var expected = await SaveReferenceAsync(img);

        var collector = new List<byte>();
        await using var target = new CallbackVipsTarget(
            onWrite: (data, ct) => { collector.AddRange(data.Span.ToArray()); return ValueTask.CompletedTask; });
        await VipsPngSaver.SaveAsync(img, target.AsPipeWriter());

        Assert.Equal(expected, collector.ToArray());
        Assert.Equal(expected.Length, target.Position);
    }

    [Fact]
    public async Task MemoryTarget_RejectsWriteAfterComplete()
    {
        await using var target = new MemoryVipsTarget();
        await target.WriteAsync(new byte[] { 1, 2, 3 });
        await target.CompleteAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await target.WriteAsync(new byte[] { 4 }));
    }

    [Fact]
    public async Task StreamTarget_DisposeRespectsLeaveOpen()
    {
        using var ms = new MemoryStream();
        await using (var target = new StreamVipsTarget(ms, leaveOpen: true))
        {
            await target.WriteAsync(new byte[] { 1, 2, 3 });
        }
        // Dispose with leaveOpen=true must not close the underlying stream.
        Assert.True(ms.CanWrite);

        // With leaveOpen=false, dispose closes it.
        var ms2 = new MemoryStream();
        await using (var target = new StreamVipsTarget(ms2, leaveOpen: false))
        {
            await target.WriteAsync(new byte[] { 1, 2, 3 });
        }
        Assert.False(ms2.CanWrite);
    }

    [Fact]
    public async Task CallbackTarget_FlushAndCompleteAreOptional()
    {
        // The flush / complete callbacks default to no-ops when null.
        bool wrote = false;
        await using var target = new CallbackVipsTarget(
            onWrite: (data, ct) => { wrote = true; return ValueTask.CompletedTask; });
        await target.WriteAsync(new byte[] { 1 });
        await target.FlushAsync();
        await target.CompleteAsync();
        Assert.True(wrote);
    }

    [Fact]
    public async Task Position_AdvancesByBytesWritten()
    {
        await using var target = new MemoryVipsTarget();
        Assert.Equal(0, target.Position);
        await target.WriteAsync(new byte[] { 1, 2, 3, 4 });
        Assert.Equal(4, target.Position);
        await target.WriteAsync(new byte[] { 5, 6 });
        Assert.Equal(6, target.Position);
    }

    [Fact]
    public void StreamTarget_RejectsReadOnlyStream()
    {
        // Our PNG-encoded reference bytes happen to be a great read-only stream.
        var ms = new MemoryStream(new byte[] { 1, 2, 3 }, writable: false);
        Assert.Throws<ArgumentException>(() => new StreamVipsTarget(ms));
    }
}
