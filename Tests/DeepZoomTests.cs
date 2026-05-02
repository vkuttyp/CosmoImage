using System;
using System.IO;
using System.Threading.Tasks;
using CosmoImage.Savers;
using Xunit;

namespace CosmoImage.Tests;

public class DeepZoomTests : IDisposable
{
    private readonly string _tmpDir;

    public DeepZoomTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "cosmoimage-dz-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { }
    }

    private static VipsImage Uniform(int w, int h, byte value, int bands = 3)
        => new VipsImage
        {
            Width = w, Height = h, Bands = bands, BandFormat = VipsBandFormat.UChar,
            Interpretation = bands == 1 ? VipsInterpretation.BW : VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int i = 0; i < reg.Valid.Width * bands; i++) addr[i] = value;
                }
                return 0;
            }
        };

    [Fact]
    public async Task SaveDeepZoom_WritesDziDescriptorAndLevelDirectories()
    {
        var src = Uniform(512, 384, value: 100);
        var basePath = Path.Combine(_tmpDir, "img");
        await src.SaveDeepZoomAsync(basePath);

        // .dzi exists and has expected dims.
        Assert.True(File.Exists(basePath + ".dzi"));
        var dzi = await File.ReadAllTextAsync(basePath + ".dzi");
        Assert.Contains("TileSize=\"256\"", dzi);
        Assert.Contains("Width=\"512\"", dzi);
        Assert.Contains("Height=\"384\"", dzi);
        Assert.Contains("Format=\"jpg\"", dzi);

        // Pyramid root directory exists.
        Assert.True(Directory.Exists(basePath + "_files"));
    }

    [Fact]
    public async Task SaveDeepZoom_LevelCountIsLog2OfMaxDimension()
    {
        // 512x384 → max=512 → halving 512→256→128→64→32→16→8→4→2→1 = 9
        // reductions, plus the 1×1 singleton level → 10 levels (0..9).
        var src = Uniform(512, 384, value: 50);
        var basePath = Path.Combine(_tmpDir, "img");
        await src.SaveDeepZoomAsync(basePath);

        for (int k = 0; k <= 9; k++)
            Assert.True(Directory.Exists(Path.Combine(basePath + "_files", k.ToString())),
                $"level {k} directory missing");
        Assert.False(Directory.Exists(Path.Combine(basePath + "_files", "10")));
    }

    [Fact]
    public async Task SaveDeepZoom_TopLevel_HasSingleTile()
    {
        var src = Uniform(300, 200, value: 80);
        var basePath = Path.Combine(_tmpDir, "img");
        await src.SaveDeepZoomAsync(basePath);

        // Level 0 always has exactly one tile (image is 1×1 at this level).
        var level0 = Path.Combine(basePath + "_files", "0");
        var tiles = Directory.GetFiles(level0, "*.jpg");
        Assert.Single(tiles);
        Assert.EndsWith("0_0.jpg", tiles[0]);
    }

    [Fact]
    public async Task SaveDeepZoom_BottomLevel_HasFullResTileGrid()
    {
        // 600x400 with 256-tile, 1-pixel overlap.
        // cols = ⌈600/256⌉ = 3, rows = ⌈400/256⌉ = 2 → 6 tiles at the full-res level.
        var src = Uniform(600, 400, value: 130);
        var basePath = Path.Combine(_tmpDir, "img");
        await src.SaveDeepZoomAsync(basePath);

        // Bottom level number = ceil(log2(600)) = 10 (matches 10-step halving from above).
        // Find the highest-numbered level dir.
        var levels = Directory.GetDirectories(basePath + "_files");
        int bottom = 0;
        foreach (var d in levels)
        {
            int n = int.Parse(Path.GetFileName(d));
            if (n > bottom) bottom = n;
        }

        var bottomTiles = Directory.GetFiles(Path.Combine(basePath + "_files", bottom.ToString()), "*.jpg");
        Assert.Equal(6, bottomTiles.Length);
    }

    [Fact]
    public async Task SaveDeepZoom_PngFormat_WritesPngTiles()
    {
        var src = Uniform(64, 48, value: 200);
        var basePath = Path.Combine(_tmpDir, "img");
        await src.SaveDeepZoomAsync(basePath, format: VipsDzTileFormat.Png);

        var dzi = await File.ReadAllTextAsync(basePath + ".dzi");
        Assert.Contains("Format=\"png\"", dzi);

        // Level 0 should have a single .png tile.
        var pngs = Directory.GetFiles(Path.Combine(basePath + "_files", "0"), "*.png");
        Assert.Single(pngs);
    }

    [Fact]
    public async Task SaveDeepZoom_OverwritesExistingDirectory()
    {
        var src = Uniform(64, 48, value: 50);
        var basePath = Path.Combine(_tmpDir, "img");

        // First call.
        await src.SaveDeepZoomAsync(basePath);

        // Drop a stray file inside the pyramid directory; second call should
        // wipe the directory clean before re-writing.
        var stray = Path.Combine(basePath + "_files", "stray.txt");
        await File.WriteAllTextAsync(stray, "should be deleted");

        await src.SaveDeepZoomAsync(basePath);
        Assert.False(File.Exists(stray));
    }

    [Fact]
    public async Task SaveDeepZoom_TinyImage_StillProducesValidDzi()
    {
        // 4x3 — smaller than tileSize, so every level is a single tile.
        var src = Uniform(4, 3, value: 100);
        var basePath = Path.Combine(_tmpDir, "img");
        await src.SaveDeepZoomAsync(basePath);

        Assert.True(File.Exists(basePath + ".dzi"));
        // Levels: max(4,3)=4 → halving 4,2,1 → 3 reductions → 4 levels (0..3).
        for (int k = 0; k <= 2; k++)
            Assert.True(Directory.Exists(Path.Combine(basePath + "_files", k.ToString())));
    }
}
