using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CosmoImage.Savers;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 181 — Zoomify and Google Maps pyramid layouts in
/// <see cref="VipsDzSaver"/>. The DZ layout was already shipped (see
/// <c>DeepZoomTests</c>); these tests pin the new layouts' on-disk
/// directory tree shape and the per-layout descriptor file (or
/// absence thereof for Google).
/// </summary>
public class Round181Tests : IDisposable
{
    private readonly string _tmpDir;
    public Round181Tests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "cosmo_dz_round181_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }
    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    /// <summary>
    /// 600×400 image — wide enough to require multiple tiles at the
    /// largest level (3 cols × 2 rows for the bottom level with
    /// tileSize=256), which makes the Zoomify TileGroup math non-trivial.
    /// </summary>
    private static VipsImage MakeImage(int w, int h)
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

    [Fact]
    public async Task Zoomify_EmitsTileGroupDirsAndImageProperties()
    {
        string basePath = Path.Combine(_tmpDir, "z");
        var img = MakeImage(600, 400);
        await VipsDzSaver.SaveAsync(img, basePath, tileSize: 256,
            format: VipsDzTileFormat.Jpeg, layout: VipsDzLayout.Zoomify);

        // ImageProperties.xml lives at the root and contains the WIDTH/HEIGHT
        // + the cumulative NUMTILES count.
        string descriptor = Path.Combine(basePath, "ImageProperties.xml");
        Assert.True(File.Exists(descriptor));
        string xml = await File.ReadAllTextAsync(descriptor);
        Assert.Contains("WIDTH=\"600\"", xml);
        Assert.Contains("HEIGHT=\"400\"", xml);
        Assert.Contains("TILESIZE=\"256\"", xml);
        Assert.Contains("NUMTILES=\"", xml);

        // At least one TileGroup0 should exist; tiles named {level}-{col}-{row}.jpg.
        string group0 = Path.Combine(basePath, "TileGroup0");
        Assert.True(Directory.Exists(group0));
        var groupFiles = Directory.GetFiles(group0).Select(Path.GetFileName).ToList();
        Assert.Contains("0-0-0.jpg", groupFiles);  // top-of-pyramid single tile
    }

    [Fact]
    public async Task Zoomify_TileGroupSplitsAtMultiplesOf256()
    {
        // Force a pyramid with > 256 total tiles: 2048×2048 at tileSize=64
        // produces (2048/64)² = 1024 tiles at the bottom level alone.
        // Use PNG to skip JPEG-quality variability.
        string basePath = Path.Combine(_tmpDir, "z2");
        var img = MakeImage(512, 512);
        await VipsDzSaver.SaveAsync(img, basePath, tileSize: 32,
            format: VipsDzTileFormat.Png, layout: VipsDzLayout.Zoomify);

        // Should have at least TileGroup0 and TileGroup1.
        Assert.True(Directory.Exists(Path.Combine(basePath, "TileGroup0")));
        Assert.True(Directory.Exists(Path.Combine(basePath, "TileGroup1")));
    }

    [Fact]
    public async Task Google_EmitsLevelColRowTreeWithoutDescriptor()
    {
        string basePath = Path.Combine(_tmpDir, "g");
        var img = MakeImage(600, 400);
        await VipsDzSaver.SaveAsync(img, basePath, tileSize: 256,
            format: VipsDzTileFormat.Png, layout: VipsDzLayout.Google);

        // No descriptor file at the root.
        Assert.False(File.Exists(basePath + ".dzi"));
        Assert.False(File.Exists(Path.Combine(basePath, "ImageProperties.xml")));

        // Top of pyramid: level 0, single tile at {0}/{0}/0.png.
        string topTile = Path.Combine(basePath, "0", "0", "0.png");
        Assert.True(File.Exists(topTile));

        // Bottom level should have a multi-column structure.
        var levelDirs = Directory.GetDirectories(basePath)
            .Select(Path.GetFileName)
            .Where(n => int.TryParse(n, out _))
            .Select(n => int.Parse(n!))
            .OrderBy(n => n)
            .ToList();
        Assert.True(levelDirs.Count >= 2);
        int maxLevel = levelDirs.Max();
        // At max level, level dir contains col subdirs each with row .png files.
        var colDirs = Directory.GetDirectories(Path.Combine(basePath, maxLevel.ToString()));
        Assert.True(colDirs.Length >= 2);  // 600/256 = 3 cols at full res
    }

    [Fact]
    public async Task Dz_DefaultLayoutUnchanged_ShipsDziAndFilesDir()
    {
        // Regression: don't break the existing DZ layout that was
        // already shipped.
        string basePath = Path.Combine(_tmpDir, "d");
        var img = MakeImage(300, 300);
        await VipsDzSaver.SaveAsync(img, basePath, tileSize: 256,
            format: VipsDzTileFormat.Jpeg);  // layout omitted = default Dz

        Assert.True(File.Exists(basePath + ".dzi"));
        Assert.True(Directory.Exists(basePath + "_files"));
        Assert.True(File.Exists(Path.Combine(basePath + "_files", "0", "0_0.jpg")));
    }
}
