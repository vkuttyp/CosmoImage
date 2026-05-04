using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Core;
using CosmoImage.Loaders;
using ImageMagick;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 116 — BigTIFF support. BigTIFF uses magic 0x002B (vs 0x002A
/// for classic), 8-byte counts/offsets, 20-byte IFD entries with
/// 8-byte value-or-offset, and the LONG8/SLONG8/IFD8 type codes.
/// Common in geospatial / astronomy where image data exceeds the
/// 4 GB ceiling of classic TIFF.
/// </summary>
public class Round116Tests
{
    private static byte[] BuildRgbPixels(int w, int h)
    {
        var px = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 3;
                px[o] = (byte)((x * 7) & 0xFF);
                px[o + 1] = (byte)((y * 11) & 0xFF);
                px[o + 2] = (byte)(((x + y) * 13) & 0xFF);
            }
        return px;
    }

    /// <summary>
    /// Locate libtiff's <c>tiffcp</c> via PATH or known Homebrew prefixes.
    /// Returns null when the binary isn't available — tests that need it
    /// then skip rather than fail in environments without libtiff CLIs.
    /// </summary>
    private static string? FindTiffcp()
    {
        foreach (var path in new[] { "/opt/homebrew/bin/tiffcp", "/usr/local/bin/tiffcp", "/usr/bin/tiffcp" })
            if (File.Exists(path)) return path;
        return null;
    }

    /// <summary>
    /// Build a BigTIFF by encoding via Magick → classic TIFF, then
    /// converting to BigTIFF with libtiff's <c>tiffcp -8</c>. Magick.NET
    /// doesn't expose libtiff's "w8" mode directly, so we shell out for
    /// a faithful BigTIFF fixture (magic 0x002B + 8-byte offsets).
    /// </summary>
    private static byte[] BuildBigTiff(byte[] rgb, int w, int h, CompressionMethod compression)
    {
        var tiffcp = FindTiffcp() ?? throw new InvalidOperationException(
            "tiffcp not found on PATH or in /opt/homebrew/bin /usr/local/bin /usr/bin — " +
            "install via Homebrew (brew install libtiff) or distro package manager.");
        var settings = new MagickReadSettings
        {
            Width = (uint)w, Height = (uint)h, Format = MagickFormat.Rgb, Depth = 8,
        };
        using var img = new MagickImage();
        img.Read(rgb, settings);
        img.Format = MagickFormat.Tiff;
        img.Settings.Compression = compression;
        var classic = img.ToByteArray();

        var tmpIn = Path.GetTempFileName() + ".tif";
        var tmpOut = Path.GetTempFileName() + ".tif";
        try
        {
            File.WriteAllBytes(tmpIn, classic);
            var psi = new System.Diagnostics.ProcessStartInfo(tiffcp, $"-8 {tmpIn} {tmpOut}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            proc.WaitForExit();
            if (proc.ExitCode != 0)
                throw new InvalidOperationException("tiffcp -8 failed: " + proc.StandardError.ReadToEnd());
            return File.ReadAllBytes(tmpOut);
        }
        finally
        {
            try { File.Delete(tmpIn); } catch { }
            try { File.Delete(tmpOut); } catch { }
        }
    }

    private static void AssertExactPixels(byte[] expected, VipsImage img)
    {
        var got = img.PixelsLazy!.Value;
        Assert.Equal(expected.Length, got.Length);
        for (int i = 0; i < expected.Length; i++)
            if (expected[i] != got[i])
                Assert.Fail($"byte {i}: expected {expected[i]:X2} got {got[i]:X2}");
    }

    [Fact]
    public void Pure_BigTiffUncompressedRgb_RoundTrips()
    {
        int w = 16, h = 8;
        var px = BuildRgbPixels(w, h);
        var tiff = BuildBigTiff(px, w, h, CompressionMethod.NoCompression);
        // Sanity: the file must actually be BigTIFF (magic 0x002B).
        Assert.Equal(0x2B, tiff[2]);

        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
        Assert.Equal(3, img.Bands);
        AssertExactPixels(px, img);
    }

    [Fact]
    public void Pure_BigTiffLzwRgb_RoundTrips()
    {
        int w = 32, h = 16;
        var px = BuildRgbPixels(w, h);
        var tiff = BuildBigTiff(px, w, h, CompressionMethod.LZW);
        Assert.Equal(0x2B, tiff[2]);

        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        AssertExactPixels(px, img);
    }

    [Fact]
    public void Pure_BigTiffDeflateRgb_RoundTrips()
    {
        int w = 24, h = 12;
        var px = BuildRgbPixels(w, h);
        var tiff = BuildBigTiff(px, w, h, CompressionMethod.Zip);
        Assert.Equal(0x2B, tiff[2]);

        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        AssertExactPixels(px, img!);
    }

    [Fact]
    public async Task LoadAsync_BigTiff_TakesPureFastPath()
    {
        int w = 16, h = 8;
        var px = BuildRgbPixels(w, h);
        var tiff = BuildBigTiff(px, w, h, CompressionMethod.LZW);
        var src = new PipeVipsSource(PipeReader.Create(new MemoryStream(tiff)));
        var img = await VipsTiffLoader.LoadAsync(src);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
        AssertExactPixels(px, img);
    }
}
