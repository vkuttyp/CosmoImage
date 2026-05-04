using System;
using System.Threading.Tasks;
using CosmoImage.Core;
using Xunit;

namespace CosmoImage.Tests;

public class Round94Tests
{
    /// <summary>Counts rents + returns to verify pool lifecycle.</summary>
    private sealed class TrackingAllocator : IVipsAllocator
    {
        public int RentCount;
        public int ReturnCount;
        public byte[] Rent(int minLength) { RentCount++; return new byte[Math.Max(1, minLength)]; }
        public void Return(byte[] buffer) { ReturnCount++; }
    }

    /// <summary>Tiny solid image generator.</summary>
    private static VipsImage Solid(int w, int h, byte v)
    {
        return new VipsImage
        {
            Width = w, Height = h, Bands = 3, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width * 3; x++) addr[x] = v;
                }
                return 0;
            }
        };
    }

    // ---- Default behaviour (BareAllocator) ----

    [Fact]
    public async Task DefaultConstructor_UsesBareAllocator_NoOpDispose()
    {
        // No allocator passed → BareAllocator → buffer is a plain new byte[];
        // Dispose() is essentially a no-op.
        var sink = new MemorySink(Solid(4, 4, 100));
        await sink.RunAsync();
        Assert.Equal(48, sink.LogicalLength);  // 4×4×3
        Assert.Equal(48, sink.Pixels.Length);  // BareAllocator returns exact-size buffers
        Assert.Equal(100, sink.Pixels[0]);
        sink.Dispose();  // no-op for BareAllocator
    }

    [Fact]
    public async Task NoDispose_StillWorksWithBareAllocator()
    {
        // Existing call sites that never call Dispose stay correct
        // because BareAllocator's Return is a GC drop.
        var sink = new MemorySink(Solid(2, 2, 50));
        await sink.RunAsync();
        Assert.Equal(50, sink.Pixels[0]);
        // Don't dispose — BareAllocator means no leak.
    }

    // ---- Opt-in pool ----

    [Fact]
    public async Task PooledAllocator_RentsThenReturnsOnDispose()
    {
        var alloc = new TrackingAllocator();
        var sink = new MemorySink(Solid(3, 3, 42), alloc);
        Assert.Equal(1, alloc.RentCount);
        Assert.Equal(0, alloc.ReturnCount);
        await sink.RunAsync();
        Assert.Equal(42, sink.Pixels[0]);
        sink.Dispose();
        Assert.Equal(1, alloc.ReturnCount);
    }

    [Fact]
    public async Task PooledAllocator_LogicalLengthSeparateFromBufferLength()
    {
        // Allocator might return oversized buffers — Pixels.Length isn't
        // the contents length. Use LogicalLength for the actual extent.
        var alloc = new ArrayPoolAllocator();
        using var sink = new MemorySink(Solid(7, 5, 200), alloc);
        await sink.RunAsync();
        Assert.Equal(105, sink.LogicalLength);  // 7×5×3
        Assert.True(sink.Pixels.Length >= sink.LogicalLength);
        // Content within logical bounds.
        for (int i = 0; i < sink.LogicalLength; i++)
            Assert.Equal(200, sink.Pixels[i]);
    }

    [Fact]
    public async Task PooledAllocator_UsingStatement_ReturnsBuffer()
    {
        var alloc = new TrackingAllocator();
        await using (var sink = WrapAsync(new MemorySink(Solid(2, 2, 1), alloc)))
        {
            await sink.Sink.RunAsync();
        }
        Assert.Equal(1, alloc.RentCount);
        Assert.Equal(1, alloc.ReturnCount);
    }

    // Helper to make MemorySink work with await using (no native IAsyncDisposable).
    private static AsyncSinkWrapper WrapAsync(MemorySink s) => new(s);
    private sealed class AsyncSinkWrapper : IAsyncDisposable
    {
        public MemorySink Sink { get; }
        public AsyncSinkWrapper(MemorySink s) { Sink = s; }
        public ValueTask DisposeAsync() { Sink.Dispose(); return ValueTask.CompletedTask; }
    }

    // ---- Idempotency ----

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var alloc = new TrackingAllocator();
        var sink = new MemorySink(Solid(1, 1, 0), alloc);
        sink.Dispose();
        sink.Dispose();
        sink.Dispose();
        Assert.Equal(1, alloc.ReturnCount);  // returned exactly once
    }

    // ---- Argument validation ----

    [Fact]
    public void NullAllocator_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new MemorySink(Solid(1, 1, 0), null!));
    }

    // ---- Multi-tile pipeline still works ----

    [Fact]
    public async Task PooledAllocator_LargerImage_TilingProducesCorrectPixels()
    {
        // Image larger than a single tile so MemorySink must accept
        // multiple ConsumeTile callbacks. Pool buffer must hold them all.
        var alloc = new ArrayPoolAllocator();
        using var sink = new MemorySink(Solid(64, 64, 77), alloc);
        await sink.RunAsync();
        for (int i = 0; i < sink.LogicalLength; i++)
            Assert.Equal(77, sink.Pixels[i]);
    }
}
