using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CosmoImage.Savers;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 186 — IIIF Image API 2.0 static-tile layout in
/// <see cref="VipsDzSaver"/>. Closes the IIIF gap deferred in round 181.
/// Tests pin: info.json descriptor shape (context, dims, tiles, profile),
/// per-tile path naming convention <c>{x},{y},{w},{h}/{tw},/0/default.ext</c>,
/// and that the top-of-pyramid tile covers the full image at the
/// largest scale factor.
/// </summary>
public class Round186Tests : IDisposable
{
    private readonly string _tmpDir;
    public Round186Tests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "cosmo_dz_round186_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }
    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best-effort cleanup */ }
    }

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
                    for (int x = 0; x < reg.Valid.Width * 3; x++) addr[x] = 128;
                }
                return 0;
            }
        };

    [Fact]
    public async Task Iiif_EmitsInfoJsonWithImageApi2Context()
    {
        string basePath = Path.Combine(_tmpDir, "i");
        var img = MakeImage(600, 400);
        await VipsDzSaver.SaveAsync(img, basePath, tileSize: 256,
            format: VipsDzTileFormat.Jpeg, layout: VipsDzLayout.Iiif);

        string infoPath = Path.Combine(basePath, "info.json");
        Assert.True(File.Exists(infoPath));
        string json = await File.ReadAllTextAsync(infoPath);
        // Spot-check key fields.
        Assert.Contains("\"@context\": \"http://iiif.io/api/image/2/context.json\"", json);
        Assert.Contains("\"protocol\": \"http://iiif.io/api/image\"", json);
        Assert.Contains("\"width\": 600", json);
        Assert.Contains("\"height\": 400", json);
        Assert.Contains("\"tiles\":", json);
        Assert.Contains("\"width\": 256", json);
        Assert.Contains("\"scaleFactors\":", json);
        Assert.Contains("\"profile\":", json);
        Assert.Contains("level0", json);
    }

    [Fact]
    public async Task Iiif_TilesUseRegionCoordinatePathScheme()
    {
        // Small image so the pyramid has a manageable shape: 256×256
        // → 2 levels (1×1 at level 0 covering the whole image at scale
        // 2, then 1×1 at level 1 at scale 1).
        string basePath = Path.Combine(_tmpDir, "i2");
        var img = MakeImage(256, 256);
        await VipsDzSaver.SaveAsync(img, basePath, tileSize: 256,
            format: VipsDzTileFormat.Png, layout: VipsDzLayout.Iiif);

        // Level 0 tile (smallest): covers the full 256×256 region at
        // scale factor 2; the rendered tile is 128×128 px (rounded down
        // by VipsResize when halving).
        // The path is rootDir / "0,0,256,256" / "{tw}," / "0" / "default.png"
        // where tw is the rendered tile width at this level.
        var fullCoverageDirs = Directory.GetDirectories(basePath)
            .Select(Path.GetFileName)
            .Where(n => n!.StartsWith("0,0,"))
            .ToList();
        Assert.NotEmpty(fullCoverageDirs);

        // The 0,0,W,H region directory contains a {tw}, subdirectory,
        // which contains a 0 directory, which contains default.png.
        foreach (var dir in fullCoverageDirs)
        {
            var sizeDirs = Directory.GetDirectories(Path.Combine(basePath, dir!));
            Assert.NotEmpty(sizeDirs);
            foreach (var sizeDir in sizeDirs)
            {
                Assert.EndsWith(",", Path.GetFileName(sizeDir));  // size = "{tw},"
                Assert.True(Directory.Exists(Path.Combine(sizeDir, "0")));
                Assert.True(File.Exists(Path.Combine(sizeDir, "0", "default.png")));
            }
        }
    }

    [Fact]
    public async Task Iiif_TopOfPyramidCoversFullImage()
    {
        // 512×384 image. Level 0 must cover the entire 512×384 region.
        string basePath = Path.Combine(_tmpDir, "i3");
        var img = MakeImage(512, 384);
        await VipsDzSaver.SaveAsync(img, basePath, tileSize: 512,
            format: VipsDzTileFormat.Jpeg, layout: VipsDzLayout.Iiif);

        // Look for the region directory matching the full image dims.
        // (Width 512 fits in one tile-grid cell at the top; the path's
        // region segment will be "0,0,512,384" at the lowest scale.)
        bool found = Directory.GetDirectories(basePath)
            .Select(Path.GetFileName)
            .Any(n => n == "0,0,512,384");
        Assert.True(found, "top-of-pyramid tile should cover full image dims");
    }

    [Fact]
    public async Task Iiif_NoLegacyDescriptorFiles()
    {
        // IIIF only emits info.json. Make sure none of the other
        // layouts' descriptors leak through.
        string basePath = Path.Combine(_tmpDir, "i4");
        var img = MakeImage(256, 256);
        await VipsDzSaver.SaveAsync(img, basePath, tileSize: 256,
            format: VipsDzTileFormat.Jpeg, layout: VipsDzLayout.Iiif);

        Assert.False(File.Exists(basePath + ".dzi"));
        Assert.False(File.Exists(Path.Combine(basePath, "ImageProperties.xml")));
    }
}
