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
/// Direct port of libvips <c>vips_dzsave</c> in DeepZoom layout. Other
/// layouts (Zoomify, IIIF) are deliberately deferred — DZI is the most
/// widely-supported, and porting the full layout matrix is its own project.
/// </summary>
public static class VipsDzSaver
{
    /// <summary>
    /// Render <paramref name="image"/> as a Deep Zoom pyramid rooted at
    /// <paramref name="basePath"/>. Writes <c>{basePath}.dzi</c> plus a
    /// <c>{basePath}_files</c> directory of per-level tile subdirectories.
    /// Existing files at those paths are overwritten.
    /// </summary>
    /// <param name="image">Source image (any band format; JPEG tile output forces UChar via the existing JPEG saver).</param>
    /// <param name="basePath">Output base path with no extension. <c>foo.dzi</c> + <c>foo_files/</c> are created relative to it.</param>
    /// <param name="tileSize">Edge length of square tiles. 256 is the DZI default.</param>
    /// <param name="overlap">Pixels of overlap between adjacent tiles' edges. 1 is the DZI default; 0 produces visible seams in some viewers.</param>
    /// <param name="format">Tile format: JPEG (smaller, lossy) or PNG (lossless).</param>
    /// <param name="jpegQuality">Quality for JPEG tiles (1..100). Ignored for PNG.</param>
    public static async Task SaveAsync(
        VipsImage image,
        string basePath,
        int tileSize = 256,
        int overlap = 1,
        VipsDzTileFormat format = VipsDzTileFormat.Jpeg,
        int jpegQuality = 85,
        CancellationToken cancellationToken = default)
    {
        if (image == null) throw new ArgumentNullException(nameof(image));
        if (string.IsNullOrWhiteSpace(basePath)) throw new ArgumentException("basePath required", nameof(basePath));
        if (tileSize <= 0) throw new ArgumentOutOfRangeException(nameof(tileSize));
        if (overlap < 0) throw new ArgumentOutOfRangeException(nameof(overlap));

        int W = image.Width;
        int H = image.Height;

        // Number of levels: enough so the topmost level is ≤ tileSize on its
        // largest edge. Equivalent to ceil(log2(max(W, H))) + 1 — matches the
        // DZI convention where level 0 is a single 1×1-or-smaller tile.
        int maxDim = Math.Max(W, H);
        int levels = 1;
        int dim = maxDim;
        while (dim > 1) { dim = (dim + 1) / 2; levels++; }

        // Ensure clean output directory tree.
        string filesDir = basePath + "_files";
        if (Directory.Exists(filesDir)) Directory.Delete(filesDir, recursive: true);
        Directory.CreateDirectory(filesDir);

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

        for (int k = 0; k < levels; k++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (lw, lh) = levelDims[k];
            string levelDir = Path.Combine(filesDir, k.ToString());
            Directory.CreateDirectory(levelDir);

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

                    string ext = format == VipsDzTileFormat.Jpeg ? ".jpg" : ".png";
                    string tilePath = Path.Combine(levelDir, $"{c}_{r}{ext}");

                    using var fs = File.Create(tilePath);
                    var writer = PipeWriter.Create(fs);
                    if (format == VipsDzTileFormat.Jpeg)
                        await VipsJpegSaver.SaveAsync(tile, writer, jpegQuality, cancellationToken);
                    else
                        await VipsPngSaver.SaveAsync(tile, writer, palette: null, cancellationToken);
                }
            }
        }

        // .dzi XML descriptor. Schema is the canonical Microsoft Deep Zoom
        // schema that OpenSeadragon and every other viewer recognises.
        string ext2 = format == VipsDzTileFormat.Jpeg ? "jpg" : "png";
        string dziXml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            $"<Image TileSize=\"{tileSize}\" Overlap=\"{overlap}\" Format=\"{ext2}\" " +
            "xmlns=\"http://schemas.microsoft.com/deepzoom/2008\">\n" +
            $"  <Size Width=\"{W}\" Height=\"{H}\"/>\n" +
            "</Image>\n";
        await File.WriteAllTextAsync(basePath + ".dzi", dziXml, cancellationToken);
    }
}
