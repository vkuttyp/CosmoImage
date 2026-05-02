using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Savers;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Counting allocator used in tests to assert pool ownership: every Rent
/// must be matched by exactly one Return. Wraps plain `new byte[]` so the
/// underlying buffers are still GC-tracked if the assertions slip.
/// </summary>
internal sealed class CountingAllocator : IVipsAllocator
{
    public int Rents;
    public int Returns;
    private readonly HashSet<byte[]> _outstanding = new();

    public byte[] Rent(int minLength)
    {
        var buf = new byte[System.Math.Max(1, minLength)];
        Rents++;
        _outstanding.Add(buf);
        return buf;
    }

    public void Return(byte[] buffer)
    {
        Returns++;
        Assert.True(_outstanding.Remove(buffer), "Returned a buffer that was never rented (or was returned twice)");
    }

    public void AssertBalanced()
    {
        Assert.Equal(Rents, Returns);
        Assert.Empty(_outstanding);
    }
}

public class AllocatorTests
{
    private static VipsImage Generated(int w, int h, byte value, IVipsAllocator allocator)
    {
        return new VipsImage
        {
            Width = w, Height = h, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Allocator = allocator,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++) addr[x] = value;
                }
                return 0;
            }
        };
    }

    [Fact]
    public void VipsRegion_DisposeReturnsRentedBuffer()
    {
        var alloc = new CountingAllocator();
        var img = Generated(8, 8, 50, alloc);

        using (var reg = new VipsRegion(img))
        {
            reg.Prepare(new VipsRect(0, 0, 8, 8));
            Assert.Equal(50, reg.GetAddress(0, 0)[0]);
            Assert.Equal(1, alloc.Rents);
            Assert.Equal(0, alloc.Returns);
        }

        alloc.AssertBalanced();
    }

    [Fact]
    public void VipsRegion_RegrowReturnsOldBuffer()
    {
        var alloc = new CountingAllocator();
        var img = Generated(64, 64, 1, alloc);

        var reg = new VipsRegion(img);
        // Small tile first — small rent.
        reg.Prepare(new VipsRect(0, 0, 4, 4));
        // Larger tile that won't fit — must release old + rent new.
        reg.Prepare(new VipsRect(0, 0, 64, 64));
        reg.Dispose();

        Assert.Equal(2, alloc.Rents);
        Assert.Equal(2, alloc.Returns);
        alloc.AssertBalanced();
    }

    [Fact]
    public async Task PngSaverViaOrderedStripSink_RentsAndReturnsAllStrips()
    {
        var alloc = new CountingAllocator();
        var src = Generated(32, 32, 200, alloc);
        src.Bands = 3;
        src.Interpretation = VipsInterpretation.RGB;
        src.GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
            for (int y = 0; y < reg.Valid.Height; y++)
            {
                var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                for (int i = 0; i < reg.Valid.Width * 3; i++) addr[i] = 200;
            }
            return 0;
        };

        using var ms = new MemoryStream();
        var writer = PipeWriter.Create(ms, new StreamPipeWriterOptions(leaveOpen: true));
        await VipsPngSaver.SaveAsync(src, writer);

        // PNG save uses VipsRegion (per-worker) AND OrderedStripSink (per-tile
        // copy). Both rent from the allocator; both must return.
        Assert.True(alloc.Rents > 0);
        alloc.AssertBalanced();
    }

    [Fact]
    public void Pipeline_PropagatesCustomAllocatorThroughSetPipeline()
    {
        var alloc = new CountingAllocator();
        var src = Generated(8, 8, 30, alloc);

        var inverted = src.Invert();
        Assert.Same(alloc, inverted.Allocator);

        // A region over the inverted image rents from src's pool because the
        // pipeline propagation hands it down through SetPipeline.
        using (var reg = new VipsRegion(inverted))
        {
            reg.Prepare(new VipsRect(0, 0, 8, 8));
        }
        Assert.True(alloc.Rents > 0);
        alloc.AssertBalanced();
    }

    [Fact]
    public void DefaultAllocator_IsArrayPoolShared()
    {
        var img = new VipsImage { Width = 1, Height = 1, Bands = 1, BandFormat = VipsBandFormat.UChar };
        Assert.Same(ArrayPoolAllocator.Shared, img.Allocator);
    }
}
