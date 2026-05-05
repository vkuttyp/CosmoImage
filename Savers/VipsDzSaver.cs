using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using CosmoImage.Core;

namespace CosmoImage.Savers;

public enum VipsDzTileFormat
{
    Jpeg = 0,
    Png = 1,
}

/// <summary>
/// Output layout for <see cref="VipsDzSaver"/>. Each layout fixes the
/// on-disk directory tree and descriptor file shape; pyramid generation
/// is identical across all layouts.
/// </summary>
public enum VipsDzLayout
{
    /// <summary>Microsoft Deep Zoom — <c>{base}.dzi</c> + <c>{base}_files/{level}/{col}_{row}.{ext}</c>.</summary>
    Dz = 0,
    /// <summary>Zoomify — <c>{base}/ImageProperties.xml</c> + <c>TileGroup{N}/{level}-{col}-{row}.{ext}</c>.</summary>
    Zoomify = 1,
    /// <summary>Google Maps — <c>{base}/{level}/{col}/{row}.{ext}</c>. No descriptor file.</summary>
    Google = 2,
    /// <summary>
    /// IIIF Image API 2.0 static tiles — <c>{base}/info.json</c> +
    /// <c>{base}/{x},{y},{w},{h}/{tw},/0/default.{ext}</c> where
    /// <c>x,y,w,h</c> are in full-resolution image coordinates and
    /// <c>tw</c> is the tile pixel width at the requested scale.
    /// </summary>
    Iiif = 3,
}

/// <summary>
/// Deep Zoom Image (DZI) writer — multi-resolution pyramid output for
/// OpenSeadragon / IIIF / Microsoft Silverlight Deep Zoom viewers. Output is
/// a *directory tree* rather than a single stream, which is why this saver
/// takes a base path instead of a <see cref="System.IO.Pipelines.PipeWriter"/>.
///
/// <para>Layout for a base path of <c>/tmp/foo</c>:</para>
/// <code>
///   /tmp/foo.dzi              XML descriptor with image dims + tile params
///   /tmp/foo_files/
///     ├── 0/0_0.jpg            top of pyramid (smallest, single tile)
///     ├── 1/{0,1}_0.jpg        each level halves the previous
///     ├── …
///     └── N/{col}_{row}.jpg    full-res level, tiled
/// </code>
///
/// <para>Tile dims default to 256×256 with 1-pixel overlap on right/bottom
/// edges (the standard Deep Zoom values; OpenSeadragon expects them).
/// Quality applies to JPEG tiles only.</para>
///
/// Port of libvips <c>vips_dzsave</c>; supports the four
/// commonly-deployed pyramid layouts: DZ, Zoomify, Google Maps, and
/// IIIF Image API 2.0 static tiles. IIIF tiles use full-resolution
/// region coordinates encoded in the path; we emit the canonical
/// fixed-tile-grid subset that IIIF viewers (Mirador, OpenSeadragon's
/// IIIF tile-source) consume.
/// </summary>
public static class VipsDzSaver
{
    /// <summary>
    /// Render <paramref name="image"/> as a multi-resolution pyramid in the
    /// chosen <paramref name="layout"/>. The directory tree is rooted at
    /// <paramref name="basePath"/>; existing files at those paths are
    /// overwritten.
    /// </summary>
    /// <param name="image">Source image (any band format; JPEG tile output forces UChar via the existing JPEG saver).</param>
    /// <param name="basePath">Output base path with no extension.</param>
    /// <param name="tileSize">Edge length of square tiles. 256 is the universal default.</param>
    /// <param name="overlap">Pixels of overlap between adjacent tiles. DZ default 1; Zoomify/Google use 0.</param>
    /// <param name="format">Tile format: JPEG (smaller, lossy) or PNG (lossless).</param>
    /// <param name="jpegQuality">Quality for JPEG tiles (1..100). Ignored for PNG.</param>
    /// <param name="layout">DZ (default), Zoomify, or Google Maps.</param>
    public static async Task SaveAsync(
        VipsImage image,
        string basePath,
        int tileSize = 256,
        int overlap = 1,
        VipsDzTileFormat format = VipsDzTileFormat.Jpeg,
        int jpegQuality = 85,
        VipsDzLayout layout = VipsDzLayout.Dz,
        CancellationToken cancellationToken = default)
    {
        if (image == null) throw new ArgumentNullException(nameof(image));
        if (string.IsNullOrWhiteSpace(basePath)) throw new ArgumentException("basePath required", nameof(basePath));
        if (tileSize <= 0) throw new ArgumentOutOfRangeException(nameof(tileSize));
        if (overlap < 0) throw new ArgumentOutOfRangeException(nameof(overlap));
        // Zoomify and Google layouts assume 0 overlap; clamp to spec.
        if (layout != VipsDzLayout.Dz) overlap = 0;

        int W = image.Width;
        int H = image.Height;

        // Number of levels: enough so the topmost level is ≤ tileSize on its
        // largest edge. Equivalent to ceil(log2(max(W, H))) + 1 — matches the
        // DZI convention where level 0 is a single 1×1-or-smaller tile.
        int maxDim = Math.Max(W, H);
        int levels = 1;
        int dim = maxDim;
        while (dim > 1) { dim = (dim + 1) / 2; levels++; }

        // Layout-specific root directory. DZ writes the descriptor as a
        // sibling file ({base}.dzi) and tiles under {base}_files/; the
        // other two write everything inside {base}/.
        string rootDir = layout == VipsDzLayout.Dz ? basePath + "_files" : basePath;
        if (Directory.Exists(rootDir)) Directory.Delete(rootDir, recursive: true);
        Directory.CreateDirectory(rootDir);

        // Generate each level top-down — start at full resolution (level N-1)
        // and downsample to level 0. Reusing the source image at full res for
        // the bottom level means the kernel-based Resize chain only runs
        // levels-1 times. Each Resize is a wrapper over Shrink + Resize1D
        // which are Float-aware (rounds 7-8); UChar input stays UChar.
        VipsImage current = image;
        int curW = W, curH = H;

        // Cache levels in a list so we can write them in any order; DZI
        // doesn't require a particular order, so we walk top-down to write
        // in increasing level number for human-readable output.
        var levelImages = new VipsImage[levels];
        var levelDims = new (int W, int H)[levels];
        levelImages[levels - 1] = current;
        levelDims[levels - 1] = (curW, curH);

        for (int k = levels - 2; k >= 0; k--)
        {
            int nextW = Math.Max(1, (curW + 1) / 2);
            int nextH = Math.Max(1, (curH + 1) / 2);
            // Use bilinear by default — the standard Deep Zoom quality target.
            // Higher-quality kernels (Lanczos3) are available via Resize but
            // double the per-level cost; pyramid output is usually viewed
            // at-or-near full-res so the difference is rarely visible.
            current = current.Resize((double)nextW / curW, (double)nextH / curH, kernel: VipsKernel.Linear);
            levelImages[k] = current;
            levelDims[k] = (nextW, nextH);
            curW = nextW;
            curH = nextH;
        }

        // Cumulative tile index for Zoomify TileGroup numbering. Tiles
        // are numbered in (level, row, col) order across all levels.
        int zoomifyTileIndex = 0;
        int totalTiles = 0;

        string ext = format == VipsDzTileFormat.Jpeg ? ".jpg" : ".png";

        for (int k = 0; k < levels; k++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (lw, lh) = levelDims[k];

            int cols = (int)Math.Ceiling((double)lw / tileSize);
            int rows = (int)Math.Ceiling((double)lh / tileSize);

            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    // Tile rect with overlap. Overlap extends the tile on the
                    // right/bottom only (Microsoft DZI convention); left/top
                    // overlap is handled by the *previous* tile in the row.
                    int tx = c * tileSize - (c > 0 ? overlap : 0);
                    int ty = r * tileSize - (r > 0 ? overlap : 0);
                    int tw = Math.Min(tileSize + overlap + (c > 0 ? overlap : 0), lw - tx);
                    int th = Math.Min(tileSize + overlap + (r > 0 ? overlap : 0), lh - ty);
                    if (tw <= 0 || th <= 0) continue;

                    var tile = levelImages[k].ExtractArea(tx, ty, tw, th);

                    string tilePath = layout switch
                    {
                        VipsDzLayout.Dz =>
                            Path.Combine(rootDir, k.ToString(), $"{c}_{r}{ext}"),
                        VipsDzLayout.Zoomify =>
                            Path.Combine(rootDir,
                                $"TileGroup{zoomifyTileIndex / 256}",
                                $"{k}-{c}-{r}{ext}"),
                        VipsDzLayout.Google =>
                            Path.Combine(rootDir, k.ToString(), c.ToString(), $"{r}{ext}"),
                        VipsDzLayout.Iiif =>
                            BuildIiifTilePath(rootDir, k, levels, c, r, tw, th, tileSize, W, H, ext),
                        _ => throw new ArgumentOutOfRangeException(nameof(layout)),
                    };
                    Directory.CreateDirectory(Path.GetDirectoryName(tilePath)!);

                    using var fs = File.Create(tilePath);
                    var writer = PipeWriter.Create(fs);
                    if (format == VipsDzTileFormat.Jpeg)
                        await VipsJpegSaver.SaveAsync(tile, writer, jpegQuality, cancellationToken);
                    else
                        await VipsPngSaver.SaveAsync(tile, writer, palette: null, cancellationToken);

                    zoomifyTileIndex++;
                    totalTiles++;
                }
            }
        }

        // Layout-specific descriptor.
        string ext2 = format == VipsDzTileFormat.Jpeg ? "jpg" : "png";
        switch (layout)
        {
            case VipsDzLayout.Dz:
                string dziXml =
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                    $"<Image TileSize=\"{tileSize}\" Overlap=\"{overlap}\" Format=\"{ext2}\" " +
                    "xmlns=\"http://schemas.microsoft.com/deepzoom/2008\">\n" +
                    $"  <Size Width=\"{W}\" Height=\"{H}\"/>\n" +
                    "</Image>\n";
                await File.WriteAllTextAsync(basePath + ".dzi", dziXml, cancellationToken);
                break;

            case VipsDzLayout.Zoomify:
                // Zoomify ImageProperties.xml. Schema is fixed; viewers
                // (Zoomify, OpenSeadragon-with-Zoomify-plugin) parse this
                // exact attribute list.
                string zoomifyXml =
                    $"<IMAGE_PROPERTIES WIDTH=\"{W}\" HEIGHT=\"{H}\" " +
                    $"NUMTILES=\"{totalTiles}\" NUMIMAGES=\"1\" VERSION=\"1.8\" " +
                    $"TILESIZE=\"{tileSize}\"/>\n";
                await File.WriteAllTextAsync(
                    Path.Combine(rootDir, "ImageProperties.xml"), zoomifyXml, cancellationToken);
                break;

            case VipsDzLayout.Google:
                // Google Maps tiles have no descriptor — viewers wire up
                // tile dims and zoom limits in their config.
                break;

            case VipsDzLayout.Iiif:
                // info.json — minimal IIIF Image API 2.0 descriptor with
                // full-resolution dims, tile width, and the scale-factor
                // sequence that matches our pyramid (1, 2, 4, …).
                var scaleFactors = new System.Text.StringBuilder("[");
                for (int i = 0; i < levels; i++)
                {
                    if (i > 0) scaleFactors.Append(",");
                    scaleFactors.Append(1 << (levels - 1 - i));
                }
                scaleFactors.Append("]");
                string iiifJson =
                    "{\n" +
                    "  \"@context\": \"http://iiif.io/api/image/2/context.json\",\n" +
                    $"  \"@id\": \"\",\n" +
                    "  \"protocol\": \"http://iiif.io/api/image\",\n" +
                    $"  \"width\": {W},\n" +
                    $"  \"height\": {H},\n" +
                    "  \"tiles\": [\n" +
                    $"    {{ \"width\": {tileSize}, \"scaleFactors\": {scaleFactors} }}\n" +
                    "  ],\n" +
                    "  \"profile\": [\"http://iiif.io/api/image/2/level0.json\"]\n" +
                    "}\n";
                await File.WriteAllTextAsync(
                    Path.Combine(rootDir, "info.json"), iiifJson, cancellationToken);
                break;
        }
    }

    /// <summary>
    /// Compute the IIIF tile path for a tile at <paramref name="level"/>
    /// (col, row) in our pyramid. IIIF tile URLs use full-resolution
    /// image coordinates — we map the level's tile back to the
    /// corresponding full-res region by the scale factor 2^(levels-1-level).
    ///
    /// <para>Path shape: <c>{x},{y},{w},{h}/{tileWidth},/0/default.{ext}</c>.
    /// The trailing comma in the size segment is the IIIF "max" hint —
    /// height auto-derived from the region's aspect ratio.</para>
    /// </summary>
    private static string BuildIiifTilePath(string rootDir, int level, int totalLevels,
        int col, int row, int tileWidthPx, int tileHeightPx,
        int requestedTileSize, int fullW, int fullH, string ext)
    {
        int scaleFactor = 1 << (totalLevels - 1 - level);
        int regionX = col * requestedTileSize * scaleFactor;
        int regionY = row * requestedTileSize * scaleFactor;
        // Region width / height in full-res pixels — clamp to the image
        // boundary so edge tiles don't spill past the canvas.
        int regionW = Math.Min(requestedTileSize * scaleFactor, fullW - regionX);
        int regionH = Math.Min(requestedTileSize * scaleFactor, fullH - regionY);
        // The size segment is the requested output width (the actual
        // pixel width of the rendered tile, not the source region).
        return Path.Combine(rootDir,
            $"{regionX},{regionY},{regionW},{regionH}",
            $"{tileWidthPx},",
            "0",
            $"default{ext}");
    }
}
