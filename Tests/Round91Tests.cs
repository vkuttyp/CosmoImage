using System;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Threading.Tasks;
using CosmoImage.Core;
using CosmoImage.Loaders;
using Xunit;

namespace CosmoImage.Tests;

[Collection("VipsConfiguration")]
public class Round91Tests : IDisposable
{
    public Round91Tests() => VipsConfiguration.Default.Reset();
    public void Dispose() => VipsConfiguration.Default.Reset();

    private static IVipsSource SourceFromBytes(byte[] bytes)
        => new PipeVipsSource(PipeReader.Create(new MemoryStream(bytes)));

    // ---- Default state ----

    [Fact]
    public void Default_HasBuiltInsAfterReset()
    {
        // After Reset, the registry should have all 18 built-ins seeded.
        Assert.True(VipsConfiguration.Default.Formats.Count >= 15,
            $"Expected at least 15 built-ins, got {VipsConfiguration.Default.Formats.Count}");
    }

    [Theory]
    [InlineData("PNG")]
    [InlineData("JPEG")]
    [InlineData("WEBP")]
    [InlineData("GIF")]
    [InlineData("TIFF")]
    [InlineData("BMP")]
    [InlineData("QOI")]
    [InlineData("HEIF")]
    [InlineData("JXL")]
    [InlineData("JP2K")]
    [InlineData("PDF")]
    [InlineData("SVG")]
    [InlineData("HDR")]
    [InlineData("FITS")]
    [InlineData("NIFTI")]
    [InlineData("MAT")]
    [InlineData("PNM")]
    [InlineData("TGA")]
    public void Default_FindsEachBuiltInByName(string name)
    {
        var fmt = VipsConfiguration.Default.FindByName(name);
        Assert.NotNull(fmt);
        Assert.Equal(name, fmt!.Name);
    }

    // ---- Reset ----

    [Fact]
    public void Reset_RestoresBuiltInsAfterClear()
    {
        VipsConfiguration.Default.Clear();
        Assert.Empty(VipsConfiguration.Default.Formats);
        VipsConfiguration.Default.Reset();
        Assert.NotEmpty(VipsConfiguration.Default.Formats);
        Assert.NotNull(VipsConfiguration.Default.FindByName("PNG"));
    }

    [Fact]
    public void Reset_RemovesUserRegistrations()
    {
        var sentinel = new SentinelFormat();
        VipsConfiguration.Default.Register(sentinel);
        Assert.NotNull(VipsConfiguration.Default.FindByName("SENTINEL"));
        VipsConfiguration.Default.Reset();
        Assert.Null(VipsConfiguration.Default.FindByName("SENTINEL"));
    }

    private sealed class SentinelFormat : IVipsImageFormat
    {
        public string Name => "SENTINEL";
        public ValueTask<bool> CanDecodeAsync(IVipsSource s, System.Threading.CancellationToken ct = default)
            => ValueTask.FromResult(false);
        public ValueTask<VipsImage?> LoadAsync(IVipsSource s, System.Threading.CancellationToken ct = default)
            => ValueTask.FromResult<VipsImage?>(null);
    }

    // ---- Built-in dispatch via registry ----

    [Fact]
    public async Task LoadAsync_PngBytes_DispatchesToPngBuiltIn()
    {
        // Truncated PNG: enough magic for the sniffer to claim it; the
        // loader will then throw on the truncated body. We're checking
        // dispatch reaches PNG, not full decode.
        var pngSig = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        await using var source = SourceFromBytes(pngSig);
        // Either the loader succeeds (unlikely on 8 bytes) or throws —
        // but it must not throw NotSupportedException at the dispatch
        // layer (would mean PNG sniff missed).
        try { await VipsIdentify.LoadAsync(source); }
        catch (NotSupportedException ex) when (ex.Message.Contains("Could not detect"))
        {
            Assert.Fail("PNG sniff should have claimed the bytes");
        }
        catch { /* truncated body is expected to fail somewhere downstream */ }
    }

    [Fact]
    public async Task LoadAsync_UnknownBytes_ThrowsNotSupported()
    {
        // 0xFF... is not a known magic for any built-in.
        var bogus = Enumerable.Repeat((byte)0xFF, 32).ToArray();
        await using var source = SourceFromBytes(bogus);
        var ex = await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await VipsIdentify.LoadAsync(source));
        Assert.Contains("Could not detect", ex.Message);
    }

    // ---- User overrides built-in ----

    private sealed class CustomPngBeater : IVipsImageFormat
    {
        public string Name => "CUSTOM_PNG_BEATER";
        public int LoadInvocations { get; private set; }

        public async ValueTask<bool> CanDecodeAsync(IVipsSource source, System.Threading.CancellationToken ct = default)
        {
            // Sniff the same PNG magic — registers AFTER PNG so reverse-walk hits us first.
            var sniff = await source.SniffAsync(8, ct);
            if (sniff.Length < 8) return false;
            var s = sniff.Span;
            return s[0] == 0x89 && s[1] == 0x50 && s[2] == 0x4E && s[3] == 0x47;
        }

        public ValueTask<VipsImage?> LoadAsync(IVipsSource source, System.Threading.CancellationToken ct = default)
        {
            LoadInvocations++;
            return ValueTask.FromResult<VipsImage?>(new VipsImage { Width = 7, Height = 7, Bands = 1 });
        }
    }

    [Fact]
    public async Task LoadAsync_UserRegisteredFormat_OverridesBuiltInForSameMagic()
    {
        var custom = new CustomPngBeater();
        VipsConfiguration.Default.Register(custom);

        var pngSig = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        await using var source = SourceFromBytes(pngSig);
        var image = await VipsIdentify.LoadAsync(source);
        Assert.NotNull(image);
        Assert.Equal(7, image!.Width);  // sentinel value from CustomPngBeater
        Assert.Equal(1, custom.LoadInvocations);
    }

    // ---- Unregister built-in ----

    [Fact]
    public async Task UnregisterBuiltIn_DisablesThatFormat()
    {
        var png = VipsConfiguration.Default.FindByName("PNG");
        Assert.NotNull(png);
        Assert.True(VipsConfiguration.Default.Unregister(png!));
        Assert.Null(VipsConfiguration.Default.FindByName("PNG"));

        var pngSig = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        await using var source = SourceFromBytes(pngSig);
        var ex = await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await VipsIdentify.LoadAsync(source));
        Assert.Contains("Could not detect", ex.Message);
    }

    // ---- Built-in priority ordering ----

    [Fact]
    public void BuiltIns_PngWinsOverWeakHeuristics()
    {
        // PNG and TGA are both registered. PNG should be findable BEFORE
        // TGA in reverse-walk (i.e., later-registered). The Formats list
        // shows registration order; PNG should appear AFTER TGA.
        var formats = VipsConfiguration.Default.Formats;
        int pngIdx = -1, tgaIdx = -1;
        for (int i = 0; i < formats.Count; i++)
        {
            if (formats[i].Name == "PNG") pngIdx = i;
            if (formats[i].Name == "TGA") tgaIdx = i;
        }
        Assert.True(pngIdx > tgaIdx,
            $"PNG should be registered after TGA (priority order); PNG={pngIdx}, TGA={tgaIdx}");
    }

    // ---- JXL/JP2K still surface NotSupported on body decode ----

    [Fact]
    public async Task JxlBytes_SniffsButLoadThrowsNotSupported()
    {
        // JXL codestream box magic — enough for the JXL sniffer.
        var jxl = new byte[] { 0x00, 0x00, 0x00, 0x0C, 0x4A, 0x58, 0x4C, 0x20, 0x0D, 0x0A, 0x87, 0x0A };
        await using var source = SourceFromBytes(jxl);
        var ex = await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await VipsIdentify.LoadAsync(source));
        // JXL builtin claims the sniff but its load function throws with
        // a "JXL pixel load is not supported" message — distinct from
        // the "Could not detect" message at dispatch level.
        Assert.Contains("JXL", ex.Message);
    }
}
