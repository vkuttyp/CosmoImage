using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CosmoImage.Core;
using CosmoImage.Loaders;
using Xunit;

namespace CosmoImage.Tests;

public class Round92Tests
{
    /// <summary>Custom format with magic "RND92". Returns a sentinel image for verification.</summary>
    private sealed class Round92Format : IVipsImageFormat
    {
        public string Name => "RND92";
        public int LoadInvocations { get; private set; }

        public async ValueTask<bool> CanDecodeAsync(IVipsSource source, CancellationToken ct = default)
        {
            var sniff = await source.SniffAsync(5, ct);
            if (sniff.Length < 5) return false;
            var s = sniff.Span;
            return s[0] == 'R' && s[1] == 'N' && s[2] == 'D' && s[3] == '9' && s[4] == '2';
        }

        public ValueTask<VipsImage?> LoadAsync(IVipsSource source, CancellationToken ct = default)
        {
            LoadInvocations++;
            return ValueTask.FromResult<VipsImage?>(new VipsImage { Width = 92, Height = 92, Bands = 1 });
        }
    }

    private static IVipsSource SourceForMagic(string magic)
        => new PipeVipsSource(PipeReader.Create(new MemoryStream(System.Text.Encoding.ASCII.GetBytes(magic))));

    // ---- Constructor ----

    [Fact]
    public void DefaultConstructor_SeedsBuiltIns()
    {
        var c = new VipsConfiguration();
        Assert.True(c.Formats.Count >= 15, $"expected built-ins, got {c.Formats.Count}");
        Assert.NotNull(c.FindByName("PNG"));
    }

    [Fact]
    public void EmptyConstructor_HasNoFormats()
    {
        var c = new VipsConfiguration(seedBuiltIns: false);
        Assert.Empty(c.Formats);
    }

    // ---- Per-instance LoadAsync ----

    [Fact]
    public async Task LoadAsync_PerInstanceConfig_DoesntTouchDefault()
    {
        // Custom config has Round92Format; Default doesn't.
        var perInstance = new VipsConfiguration(seedBuiltIns: false);
        var fmt = new Round92Format();
        perInstance.Register(fmt);

        await using var source = SourceForMagic("RND92");
        var image = await VipsIdentify.LoadAsync(source, perInstance);
        Assert.NotNull(image);
        Assert.Equal(92, image!.Width);
        Assert.Equal(1, fmt.LoadInvocations);

        // Default registry doesn't have this format.
        Assert.Null(VipsConfiguration.Default.FindByName("RND92"));
    }

    [Fact]
    public async Task LoadAsync_DefaultConfig_StillWorksUnchanged()
    {
        // Truncated PNG bytes — built-in PNG sniffer claims (no custom
        // override on Default).
        var pngSig = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        await using var source = new PipeVipsSource(PipeReader.Create(new MemoryStream(pngSig)));
        try { await VipsIdentify.LoadAsync(source); }
        catch (NotSupportedException ex) when (ex.Message.Contains("Could not detect"))
        {
            Assert.Fail("Default registry should still recognise PNG bytes");
        }
        catch { /* truncated body throws downstream — fine */ }
    }

    [Fact]
    public async Task LoadAsync_EmptyConfig_ThrowsForKnownBuiltInBytes()
    {
        // Empty registry shouldn't recognise even built-in formats.
        var empty = new VipsConfiguration(seedBuiltIns: false);
        var pngSig = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        await using var source = new PipeVipsSource(PipeReader.Create(new MemoryStream(pngSig)));
        var ex = await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await VipsIdentify.LoadAsync(source, empty));
        Assert.Contains("Could not detect", ex.Message);
    }

    [Fact]
    public async Task LoadAsync_NullConfig_Throws()
    {
        await using var source = SourceForMagic("RND92");
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await VipsIdentify.LoadAsync(source, configuration: null!));
    }

    // ---- Stream overload ----

    [Fact]
    public async Task LoadAsync_StreamOverload_UsesProvidedConfig()
    {
        var perInstance = new VipsConfiguration(seedBuiltIns: false);
        perInstance.Register(new Round92Format());
        var stream = new MemoryStream(System.Text.Encoding.ASCII.GetBytes("RND92EXTRA"));
        var image = await VipsIdentify.LoadAsync(stream, perInstance);
        Assert.NotNull(image);
        Assert.Equal(92, image!.Width);
    }

    [Fact]
    public async Task VipsImageOps_LoadAsync_ConfigOverloadWorks()
    {
        var perInstance = new VipsConfiguration(seedBuiltIns: false);
        perInstance.Register(new Round92Format());
        var stream = new MemoryStream(System.Text.Encoding.ASCII.GetBytes("RND92"));
        var image = await VipsImageOps.LoadAsync(stream, perInstance);
        Assert.NotNull(image);
        Assert.Equal(92, image!.Width);
    }

    // ---- Isolation ----

    [Fact]
    public async Task TwoConfigs_DontInterfere()
    {
        var configA = new VipsConfiguration(seedBuiltIns: false);
        var configB = new VipsConfiguration(seedBuiltIns: false);
        configA.Register(new Round92Format());
        // configB is empty.

        await using var sourceA = SourceForMagic("RND92");
        var imageA = await VipsIdentify.LoadAsync(sourceA, configA);
        Assert.NotNull(imageA);

        await using var sourceB = SourceForMagic("RND92");
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await VipsIdentify.LoadAsync(sourceB, configB));
    }

    [Fact]
    public void RegisterOnInstance_DoesntPolluteDefault()
    {
        var perInstance = new VipsConfiguration(seedBuiltIns: false);
        perInstance.Register(new Round92Format());
        Assert.NotNull(perInstance.FindByName("RND92"));
        Assert.Null(VipsConfiguration.Default.FindByName("RND92"));
    }

    // ---- Reset on per-instance ----

    [Fact]
    public void Reset_OnPerInstance_RestoresBuiltIns()
    {
        var c = new VipsConfiguration(seedBuiltIns: false);
        Assert.Empty(c.Formats);
        c.Reset();
        Assert.True(c.Formats.Count >= 15);
    }
}
