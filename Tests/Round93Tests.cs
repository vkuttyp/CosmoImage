using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using CosmoImage.Core;
using CosmoImage.Loaders;
using Xunit;

namespace CosmoImage.Tests;

public class Round93Tests
{
    /// <summary>
    /// Custom format that always claims, returns a tiny 1×1 image.
    /// Used to verify the loaded image picks up the configuration's
    /// allocator setting.
    /// </summary>
    private sealed class TrivialFormat : IVipsImageFormat
    {
        public string Name => "TRIVIAL";
        public ValueTask<bool> CanDecodeAsync(IVipsSource s, CancellationToken ct = default)
            => ValueTask.FromResult(true);
        public ValueTask<VipsImage?> LoadAsync(IVipsSource s, CancellationToken ct = default)
            => ValueTask.FromResult<VipsImage?>(new VipsImage
            {
                Width = 1, Height = 1, Bands = 3,
                BandFormat = VipsBandFormat.UChar,
            });
    }

    /// <summary>Counts allocations to verify the allocator was actually used.</summary>
    private sealed class CountingAllocator : IVipsAllocator
    {
        public int RentCount;
        public byte[] Rent(int minLength) { RentCount++; return new byte[Math.Max(1, minLength)]; }
        public void Return(byte[] buffer) { /* no-op */ }
    }

    private static IVipsSource Source() => new PipeVipsSource(PipeReader.Create(new MemoryStream(new byte[] { 0 })));

    // ---- Default ----

    [Fact]
    public void DefaultConfig_UsesArrayPoolAllocator()
    {
        var c = new VipsConfiguration();
        Assert.Same(ArrayPoolAllocator.Shared, c.Allocator);
    }

    [Fact]
    public async Task DefaultLoad_ImageHasArrayPoolAllocator()
    {
        var c = new VipsConfiguration(seedBuiltIns: false);
        c.Register(new TrivialFormat());
        await using var src = Source();
        var img = await VipsIdentify.LoadAsync(src, c);
        Assert.NotNull(img);
        Assert.Same(ArrayPoolAllocator.Shared, img!.Allocator);
    }

    // ---- Custom allocator ----

    [Fact]
    public async Task CustomAllocator_AppliedToLoadedImage()
    {
        var alloc = new CountingAllocator();
        var c = new VipsConfiguration(seedBuiltIns: false) { Allocator = alloc };
        c.Register(new TrivialFormat());
        await using var src = Source();
        var img = await VipsIdentify.LoadAsync(src, c);
        Assert.NotNull(img);
        Assert.Same(alloc, img!.Allocator);
    }

    [Fact]
    public async Task BareAllocator_AppliedToLoadedImage()
    {
        var c = new VipsConfiguration(seedBuiltIns: false) { Allocator = BareAllocator.Shared };
        c.Register(new TrivialFormat());
        await using var src = Source();
        var img = await VipsIdentify.LoadAsync(src, c);
        Assert.NotNull(img);
        Assert.Same(BareAllocator.Shared, img!.Allocator);
    }

    // ---- Allocator survives downstream ops ----

    [Fact]
    public async Task LoadedImageAllocator_PropagatesToTransientBuffers()
    {
        // Counting allocator on a config; load image; force a transient
        // buffer alloc via VipsRegion. RentCount should grow.
        var alloc = new CountingAllocator();
        var c = new VipsConfiguration(seedBuiltIns: false) { Allocator = alloc };
        c.Register(new TrivialFormat());
        await using var src = Source();
        var img = await VipsIdentify.LoadAsync(src, c);

        int beforeRent = alloc.RentCount;
        using (var reg = new VipsRegion(img!))
        {
            reg.Prepare(new VipsRect(0, 0, 1, 1));
        }
        // Region preparation pulls a transient buffer through the
        // image's allocator — count should have grown.
        Assert.True(alloc.RentCount > beforeRent,
            $"expected allocator to be used; rent count {beforeRent} → {alloc.RentCount}");
    }

    // ---- Independence ----

    [Fact]
    public void TwoConfigs_HaveIndependentAllocators()
    {
        var alloc1 = new CountingAllocator();
        var alloc2 = new CountingAllocator();
        var c1 = new VipsConfiguration { Allocator = alloc1 };
        var c2 = new VipsConfiguration { Allocator = alloc2 };
        Assert.NotSame(c1.Allocator, c2.Allocator);
    }

    [Fact]
    public void AllocatorOnConfig_IsSettable()
    {
        var c = new VipsConfiguration();
        var custom = new CountingAllocator();
        c.Allocator = custom;
        Assert.Same(custom, c.Allocator);
    }
}
