using System;
using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Operations.Analysis;
using CosmoImage.Operations.Convolution;
using CosmoImage.Operations.Misc;

namespace CosmoImage.Core;

/// <summary>
/// Fluent extension methods over <see cref="VipsImageOps"/>. Lets pipelines
/// read top-down: <c>image.AutoOrient().Resize(0.5, kernel: VipsKernel.Lanczos3).SaveJpegAsync(writer)</c>
/// instead of the inside-out functional form. Pure delegation — no behavior
/// difference from the static methods. Loaders aren't here because they take
/// a source rather than an image.
/// </summary>
public static class VipsImageExtensions
{
    // --- Geometric ---

    public static VipsImage Resize(this VipsImage image, double scale, double vScale = 0, VipsKernel kernel = VipsKernel.Linear)
        => VipsImageOps.Resize(image, scale, vScale, kernel);

    public static VipsImage Resize1D(this VipsImage image, double scale, bool vertical, VipsKernel kernel = VipsKernel.Linear)
        => VipsImageOps.Resize1D(image, scale, vertical, kernel);

    public static VipsImage Shrink(this VipsImage image, int hShrink, int vShrink)
        => VipsImageOps.Shrink(image, hShrink, vShrink);

    public static VipsImage Rotate(this VipsImage image, VipsAngle angle)
        => VipsImageOps.Rotate(image, angle);

    public static VipsImage Rotate(this VipsImage image, double degrees, VipsKernel kernel = VipsKernel.Linear)
        => VipsImageOps.Rotate(image, degrees, kernel);

    public static VipsImage Flip(this VipsImage image, VipsDirection direction)
        => VipsImageOps.Flip(image, direction);

    public static VipsImage Affine(this VipsImage image, double a, double b, double c, double d, double idx = 0, double idy = 0, VipsKernel interpolate = VipsKernel.Linear)
        => VipsImageOps.Affine(image, a, b, c, d, idx, idy, interpolate);

    public static VipsImage ExtractArea(this VipsImage image, int left, int top, int width, int height)
        => VipsImageOps.ExtractArea(image, left, top, width, height);

    /// <summary>Alias for <see cref="ExtractArea"/> — common name in other libs.</summary>
    public static VipsImage Crop(this VipsImage image, int left, int top, int width, int height)
        => VipsImageOps.ExtractArea(image, left, top, width, height);

    /// <summary>Content-aware crop: greedy entropy-driven shrink to target dims.</summary>
    public static VipsImage EntropyCrop(this VipsImage image, int width, int height)
        => VipsImageOps.EntropyCrop(image, width, height);

    public static VipsImage AutoOrient(this VipsImage image)
        => VipsImageOps.AutoOrient(image);

    // --- Pointwise / arithmetic ---

    public static VipsImage Invert(this VipsImage image)
        => VipsImageOps.Invert(image);

    public static VipsImage Linear(this VipsImage image, double[] a, double[] b)
        => VipsImageOps.Linear(image, a, b);

    public static VipsImage Gamma(this VipsImage image, double exponent)
        => VipsImageOps.Gamma(image, exponent);

    public static VipsImage Brightness(this VipsImage image, double amount)
        => VipsImageOps.Brightness(image, amount);

    public static VipsImage Contrast(this VipsImage image, double amount)
        => VipsImageOps.Contrast(image, amount);

    public static VipsImage Saturate(this VipsImage image, double s)
        => VipsImageOps.Saturate(image, s);

    public static VipsImage Greyscale(this VipsImage image)
        => VipsImageOps.Greyscale(image);

    /// <summary>Alias for <see cref="Greyscale"/>.</summary>
    public static VipsImage Grayscale(this VipsImage image)
        => VipsImageOps.Greyscale(image);

    public static VipsImage Hue(this VipsImage image, double degrees)
        => VipsImageOps.Hue(image, degrees);

    public static VipsImage Sepia(this VipsImage image)
        => VipsImageOps.Sepia(image);

    /// <summary>Darken corners with quadratic radial falloff. Strength 0..1.</summary>
    public static VipsImage Vignette(this VipsImage image, double strength = 0.5)
        => VipsImageOps.Vignette(image, strength);

    /// <summary>Pixelate into blockSize×blockSize blocks (box-average per block).</summary>
    public static VipsImage Pixelate(this VipsImage image, int blockSize)
        => VipsImageOps.Pixelate(image, blockSize);

    /// <summary>Bloom-style glow halo. Sigma controls width, strength controls intensity.</summary>
    public static VipsImage Glow(this VipsImage image, double sigma = 5.0, double strength = 0.3)
        => VipsImageOps.Glow(image, sigma, strength);

    /// <summary>Shift HSL Lightness by <paramref name="amount"/> (range -1..+1).</summary>
    public static VipsImage Lightness(this VipsImage image, double amount)
        => VipsImageOps.Lightness(image, amount);

    /// <summary>Oil-paint effect (Magick.NET).</summary>
    public static VipsImage OilPaint(this VipsImage image, double radius = 3.0, double sigma = 1.0)
        => VipsImageOps.OilPaint(image, radius, sigma);

    /// <summary>Charcoal-sketch effect (Magick.NET).</summary>
    public static VipsImage Charcoal(this VipsImage image, double radius = 1.0, double sigma = 0.5)
        => VipsImageOps.Charcoal(image, radius, sigma);

    /// <summary>Pencil-sketch effect (Magick.NET).</summary>
    public static VipsImage Sketch(this VipsImage image, double radius = 1.0, double sigma = 0.5, double angle = 0.0)
        => VipsImageOps.Sketch(image, radius, sigma, angle);

    /// <summary>Polaroid effect — white frame + rotation, RGBA output (Magick.NET).</summary>
    public static VipsImage Polaroid(this VipsImage image, double angle = -5.0)
        => VipsImageOps.Polaroid(image, angle);

    /// <summary>
    /// sRGB → linear-light. Place before Resize/GaussBlur/Composite for
    /// gamma-correct blending; close the pipeline with <see cref="Delinearize"/>.
    /// </summary>
    public static VipsImage Linearize(this VipsImage image)
        => VipsImageOps.Linearize(image);

    /// <summary>linear-light → sRGB. Inverse of <see cref="Linearize"/>.</summary>
    public static VipsImage Delinearize(this VipsImage image)
        => VipsImageOps.Delinearize(image);

    public static VipsImage Recomb(this VipsImage image, double[,] matrix)
        => VipsImageOps.Recomb(image, matrix);

    public static VipsImage Maplut(this VipsImage image, VipsImage lut)
        => VipsImageOps.Maplut(image, lut);

    /// <summary>
    /// Reduce the image to at most <paramref name="colors"/> distinct colors
    /// (Wu/median-cut quantizer), optionally with Floyd-Steinberg dithering.
    /// </summary>
    public static VipsImage Quantize(this VipsImage image, int colors = 256, bool dither = true)
        => VipsImageOps.Quantize(image, colors, dither);

    // --- Convolution / morphology ---

    public static VipsImage Conv(this VipsImage image, double[,] mask)
        => VipsImageOps.Conv(image, mask);

    public static VipsImage Conv1D(this VipsImage image, double[] kernel, bool vertical)
        => VipsImageOps.Conv1D(image, kernel, vertical);

    public static VipsImage GaussBlur(this VipsImage image, double sigma)
        => VipsImageOps.GaussBlur(image, sigma);

    public static VipsImage UnsharpMask(this VipsImage image, double sigma = 1.0, double amount = 1.0)
        => VipsImageOps.UnsharpMask(image, sigma, amount);

    public static VipsImage Morph(this VipsImage image, double[,] mask, VipsMorphMethod method)
        => VipsImageOps.Morph(image, mask, method);

    public static VipsImage Dilate(this VipsImage image, double[,] mask)
        => VipsImageOps.Dilate(image, mask);

    public static VipsImage Erode(this VipsImage image, double[,] mask)
        => VipsImageOps.Erode(image, mask);

    /// <summary>Morphological opening: erode then dilate. Removes small specks.</summary>
    public static VipsImage Open(this VipsImage image, double[,] mask)
        => VipsImageOps.Open(image, mask);

    /// <summary>Morphological closing: dilate then erode. Fills small gaps.</summary>
    public static VipsImage Close(this VipsImage image, double[,] mask)
        => VipsImageOps.Close(image, mask);

    /// <summary>Rank (order-statistic) filter over a windowWidth×windowHeight window.</summary>
    public static VipsImage Rank(this VipsImage image, int windowWidth, int windowHeight, int index)
        => VipsImageOps.Rank(image, windowWidth, windowHeight, index);

    /// <summary>Median filter (Rank with index at window center).</summary>
    public static VipsImage Median(this VipsImage image, int windowSize = 3)
        => VipsImageOps.Median(image, windowSize);

    /// <summary>Hexagonal-aperture bokeh blur. Radius in pixels.</summary>
    public static VipsImage BokehBlur(this VipsImage image, int radius)
        => VipsImageOps.BokehBlur(image, radius);

    // --- Color ---

    public static VipsImage Colourspace(this VipsImage image, VipsInterpretation space)
        => VipsImageOps.Colourspace(image, space);

    public static VipsImage IccTransform(this VipsImage image, byte[] outputProfile, byte[]? inputProfile = null)
        => VipsImageOps.IccTransform(image, outputProfile, inputProfile);

    // --- Composition / drawing ---

    public static VipsImage Composite(this VipsImage @base, VipsImage overlay, double x, double y)
        => VipsImageOps.Composite(@base, overlay, x, y);

    public static VipsImage DrawLine(this VipsImage image, int x1, int y1, int x2, int y2, byte[] ink)
        => VipsImageOps.DrawLine(image, x1, y1, x2, y2, ink);

    public static VipsImage DrawRect(this VipsImage image, int left, int top, int width, int height, byte[] ink, bool fill = false)
        => VipsImageOps.DrawRect(image, left, top, width, height, ink, fill);

    // --- Analysis ---

    public static VipsImage HistFind(this VipsImage image)
        => VipsImageOps.HistFind(image);

    public static VipsImage HistCum(this VipsImage image)
        => VipsImageOps.HistCum(image);

    public static VipsImage HistNorm(this VipsImage image)
        => VipsImageOps.HistNorm(image);

    public static VipsImage HistEqual(this VipsImage image)
        => VipsImageOps.HistEqual(image);

    public static VipsImage FwFft(this VipsImage image)
        => VipsImageOps.FwFft(image);

    /// <summary>Inverse 2D FFT — DPComplex spectrum back to UChar spatial image.</summary>
    public static VipsImage InvFft(this VipsImage image) => VipsImageOps.InvFft(image);

    /// <summary>Centered log-magnitude spectrum of a DPComplex image.</summary>
    public static VipsImage Spectrum(this VipsImage image) => VipsImageOps.Spectrum(image);

    /// <summary>Per-band + aggregate min/max/avg/deviate over the whole image.</summary>
    public static VipsStatsResult Stats(this VipsImage image) => VipsImageOps.Stats(image);
    public static double Avg(this VipsImage image) => VipsImageOps.Avg(image);
    public static double Min(this VipsImage image) => VipsImageOps.Min(image);
    public static double Max(this VipsImage image) => VipsImageOps.Max(image);
    public static double Deviate(this VipsImage image) => VipsImageOps.Deviate(image);

    // --- Cast / Format conversion ---

    /// <summary>Numeric band-format conversion. UChar↔Float supported.</summary>
    public static VipsImage Cast(this VipsImage image, VipsBandFormat target)
        => VipsImageOps.Cast(image, target);

    /// <summary>Convert to Float band format (no auto-normalization).</summary>
    public static VipsImage CastFloat(this VipsImage image) => VipsImageOps.CastFloat(image);

    /// <summary>Convert to UChar band format (clamps + rounds).</summary>
    public static VipsImage CastUChar(this VipsImage image) => VipsImageOps.CastUChar(image);

    // --- Math / Boolean / Relational ---

    public static VipsImage Abs(this VipsImage image) => VipsImageOps.Abs(image);
    public static VipsImage Sin(this VipsImage image) => VipsImageOps.Sin(image);
    public static VipsImage Cos(this VipsImage image) => VipsImageOps.Cos(image);
    public static VipsImage Tan(this VipsImage image) => VipsImageOps.Tan(image);
    public static VipsImage Log(this VipsImage image) => VipsImageOps.Log(image);
    public static VipsImage Log10(this VipsImage image) => VipsImageOps.Log10(image);
    public static VipsImage Exp(this VipsImage image) => VipsImageOps.Exp(image);
    public static VipsImage Exp10(this VipsImage image) => VipsImageOps.Exp10(image);
    public static VipsImage Sqrt(this VipsImage image) => VipsImageOps.Sqrt(image);
    public static VipsImage Pow(this VipsImage image, double exponent) => VipsImageOps.Pow(image, exponent);

    public static VipsImage AndConst(this VipsImage image, params double[] c) => VipsImageOps.AndConst(image, c);
    public static VipsImage OrConst(this VipsImage image, params double[] c) => VipsImageOps.OrConst(image, c);
    public static VipsImage XorConst(this VipsImage image, params double[] c) => VipsImageOps.XorConst(image, c);
    public static VipsImage And(this VipsImage left, VipsImage right) => VipsImageOps.And(left, right);
    public static VipsImage Or(this VipsImage left, VipsImage right) => VipsImageOps.Or(left, right);
    public static VipsImage Xor(this VipsImage left, VipsImage right) => VipsImageOps.Xor(left, right);

    public static VipsImage EqualConst(this VipsImage image, params double[] c) => VipsImageOps.EqualConst(image, c);
    public static VipsImage NotEqualConst(this VipsImage image, params double[] c) => VipsImageOps.NotEqualConst(image, c);
    public static VipsImage LessConst(this VipsImage image, params double[] c) => VipsImageOps.LessConst(image, c);
    public static VipsImage LessEqConst(this VipsImage image, params double[] c) => VipsImageOps.LessEqConst(image, c);
    public static VipsImage MoreConst(this VipsImage image, params double[] c) => VipsImageOps.MoreConst(image, c);
    public static VipsImage MoreEqConst(this VipsImage image, params double[] c) => VipsImageOps.MoreEqConst(image, c);

    // --- Image-image arithmetic ---

    public static VipsImage Add(this VipsImage left, VipsImage right) => VipsImageOps.Add(left, right);
    public static VipsImage Subtract(this VipsImage left, VipsImage right) => VipsImageOps.Subtract(left, right);
    public static VipsImage Multiply(this VipsImage left, VipsImage right) => VipsImageOps.Multiply(left, right);
    public static VipsImage Divide(this VipsImage left, VipsImage right) => VipsImageOps.Divide(left, right);
    public static VipsImage Remainder(this VipsImage left, VipsImage right) => VipsImageOps.Remainder(left, right);

    // --- Alpha management ---

    /// <summary>Multiply colour bands by alpha — alpha-correct compositing prep.</summary>
    public static VipsImage Premultiply(this VipsImage image) => VipsImageOps.Premultiply(image);

    /// <summary>Divide colour bands by alpha — inverse of <see cref="Premultiply"/>.</summary>
    public static VipsImage Unpremultiply(this VipsImage image) => VipsImageOps.Unpremultiply(image);

    // --- Embed / Bandjoin ---

    /// <summary>Place this image at (x, y) inside a (w, h) canvas with extension fill.</summary>
    public static VipsImage Embed(this VipsImage image, int x, int y, int width, int height,
        CosmoImage.Operations.Geometric.VipsExtend extend = CosmoImage.Operations.Geometric.VipsExtend.Black,
        double[]? background = null)
        => VipsImageOps.Embed(image, x, y, width, height, extend, background);

    /// <summary>Concatenate this image's bands with one or more others.</summary>
    public static VipsImage Bandjoin(this VipsImage left, params VipsImage[] others)
    {
        var all = new VipsImage[1 + others.Length];
        all[0] = left;
        for (int i = 0; i < others.Length; i++) all[i + 1] = others[i];
        return VipsImageOps.Bandjoin(all);
    }

    /// <summary>Composite alpha-bearing image onto background, drop alpha.</summary>
    public static VipsImage Flatten(this VipsImage image, params double[] background)
        => VipsImageOps.Flatten(image, background.Length == 0 ? null : background);

    /// <summary>Add an opaque alpha channel; pass-through if alpha already present.</summary>
    public static VipsImage AddAlpha(this VipsImage image, double alpha = 255.0)
        => VipsImageOps.AddAlpha(image, alpha);

    /// <summary>Place into a larger canvas with background fill (compass-anchored).</summary>
    public static VipsImage Pad(this VipsImage image, int width, int height,
        double[]? background = null, VipsCompass position = VipsCompass.Centre)
        => VipsImageOps.Pad(image, width, height, background, position);

    /// <summary>Composite onto background colour at same dimensions; alpha kept and set opaque.</summary>
    public static VipsImage BackgroundColor(this VipsImage image, params double[] background)
        => VipsImageOps.BackgroundColor(image, background);

    /// <summary>Contrast-limited adaptive histogram equalization (CLAHE).</summary>
    public static VipsImage HistLocal(this VipsImage image, int tileGridSize = 8, double clipLimit = 3.0)
        => VipsImageOps.HistLocal(image, tileGridSize, clipLimit);

    /// <summary>Pull <paramref name="n"/> consecutive bands starting at <paramref name="band"/>.</summary>
    public static VipsImage ExtractBand(this VipsImage image, int band, int n = 1)
        => VipsImageOps.ExtractBand(image, band, n);

    /// <summary>Reduce across bands with a bitwise op (UChar only).</summary>
    public static VipsImage Bandbool(this VipsImage image,
        CosmoImage.Operations.Misc.VipsBooleanOperation op)
        => VipsImageOps.Bandbool(image, op);

    /// <summary>Average bands → single-band output.</summary>
    public static VipsImage Bandmean(this VipsImage image)
        => VipsImageOps.Bandmean(image);

    /// <summary>Per-pixel ternary using this image as the condition mask.</summary>
    public static VipsImage Ifthenelse(this VipsImage condition, VipsImage then, VipsImage @else)
        => VipsImageOps.Ifthenelse(condition, then, @else);

    /// <summary>Tile <paramref name="across"/>×<paramref name="down"/> copies of the input.</summary>
    public static VipsImage Replicate(this VipsImage image, int across, int down)
        => VipsImageOps.Replicate(image, across, down);

    /// <summary>Map a 1-band UChar image to RGB via the built-in jet colour ramp.</summary>
    public static VipsImage Falsecolor(this VipsImage image)
        => VipsImageOps.Falsecolor(image);

    /// <summary>Reshape (W, H, B) → (W/factor, H, B*factor).</summary>
    public static VipsImage Bandfold(this VipsImage image, int factor = 0)
        => VipsImageOps.Bandfold(image, factor);

    /// <summary>Reshape (W, H, B*factor) → (W*factor, H, B).</summary>
    public static VipsImage Bandunfold(this VipsImage image, int factor = 0)
        => VipsImageOps.Bandunfold(image, factor);

    /// <summary>Append constant bands.</summary>
    public static VipsImage BandjoinConst(this VipsImage image, params double[] c)
        => VipsImageOps.BandjoinConst(image, c);

    /// <summary>Toroidal shift (default centres the image at origin).</summary>
    public static VipsImage Wrap(this VipsImage image, int x = int.MinValue, int y = int.MinValue)
        => VipsImageOps.Wrap(image, x, y);

    /// <summary>Integer scale-up by replication (nearest-neighbour enlarge).</summary>
    public static VipsImage Zoom(this VipsImage image, int xfac, int yfac)
        => VipsImageOps.Zoom(image, xfac, yfac);

    /// <summary>Linear-stretch input to UChar 0..255.</summary>
    public static VipsImage Scale(this VipsImage image, bool log = false, double exponent = 0.25)
        => VipsImageOps.Scale(image, log, exponent);

    /// <summary>
    /// Block-scoped fluent wrapper. ImageSharp users prefer this style:
    /// <c>image.Mutate(im => im.Resize(0.5).Sepia())</c>. Equivalent to
    /// <c>image.Resize(0.5).Sepia()</c>.
    /// </summary>
    public static VipsImage Mutate(this VipsImage image, Func<VipsImage, VipsImage> action)
        => VipsImageOps.Mutate(image, action);

    // --- Typed pixel access ---

    /// <summary>
    /// Materialize this image into a strongly-typed <see cref="TypedImage{TPixel}"/>
    /// for direct pixel reads/writes. The lazy pipeline runs once via
    /// <see cref="MemorySink"/>; subsequent indexer/RowSpan calls are pure
    /// memory access. Throws when <typeparamref name="TPixel"/>'s band/format
    /// doesn't match this image.
    /// </summary>
    public static TypedImage<TPixel> ToTypedImage<TPixel>(this VipsImage image)
        where TPixel : struct, IPixel<TPixel>
        => new TypedImage<TPixel>(image);

    /// <summary>
    /// Convenience single-pixel read. Materializes the whole image — fine for
    /// a one-shot probe, but for tight loops use
    /// <see cref="ToTypedImage{TPixel}"/> + <see cref="TypedImage{TPixel}.RowSpan"/>
    /// so the materialization happens once.
    /// </summary>
    public static TPixel GetPixel<TPixel>(this VipsImage image, int x, int y)
        where TPixel : struct, IPixel<TPixel>
        => image.ToTypedImage<TPixel>()[x, y];

    // --- Savers ---

    public static Task SaveJpegAsync(this VipsImage image, PipeWriter writer, int quality = 75)
        => VipsImageOps.SaveJpegAsync(image, writer, quality);

    public static Task SavePngAsync(this VipsImage image, PipeWriter writer, int? palette = null)
        => VipsImageOps.SavePngAsync(image, writer, palette);

    public static Task SaveWebpAsync(this VipsImage image, PipeWriter writer, int quality = 75, bool lossless = false)
        => VipsImageOps.SaveWebpAsync(image, writer, quality, lossless);

    public static Task SaveTiffAsync(this VipsImage image, PipeWriter writer, bool pyramid = false)
        => VipsImageOps.SaveTiffAsync(image, writer, pyramid);

    public static Task SaveHeifAsync(this VipsImage image, PipeWriter writer, int quality = 75, bool lossless = false)
        => VipsImageOps.SaveHeifAsync(image, writer, quality, lossless);

    public static Task SaveAvifAsync(this VipsImage image, PipeWriter writer, int quality = 75, bool lossless = false)
        => VipsImageOps.SaveAvifAsync(image, writer, quality, lossless);

    /// <summary>
    /// Save as GIF, single or animated. Multi-frame output is triggered by
    /// the n-pages / page-height / animation-delays metadata convention used
    /// by <see cref="VipsGifLoader"/> — pixels can be loaded from one GIF,
    /// transformed, and saved back as an animated GIF.
    /// </summary>
    public static Task SaveGifAsync(this VipsImage image, PipeWriter writer)
        => VipsImageOps.SaveGifAsync(image, writer);

    /// <summary>
    /// Save as APNG (animated PNG). Same multi-frame metadata convention as
    /// SaveGifAsync. Single-frame inputs produce a regular PNG.
    /// </summary>
    public static Task SaveApngAsync(this VipsImage image, PipeWriter writer)
        => VipsImageOps.SaveApngAsync(image, writer);

    public static Task SaveTgaAsync(this VipsImage image, PipeWriter writer)
        => VipsImageOps.SaveTgaAsync(image, writer);

    public static Task SaveQoiAsync(this VipsImage image, PipeWriter writer)
        => VipsImageOps.SaveQoiAsync(image, writer);

    /// <summary>Save as Netpbm (PBM/PGM/PPM/PAM). Auto picks by band count.</summary>
    public static Task SavePnmAsync(this VipsImage image, PipeWriter writer, CosmoImage.Savers.VipsPnmVariant variant = CosmoImage.Savers.VipsPnmVariant.Auto)
        => VipsImageOps.SavePnmAsync(image, writer, variant);

    /// <summary>
    /// Save as a Deep Zoom Image (DZI) pyramid — directory tree at
    /// <paramref name="basePath"/>. <c>basePath.dzi</c> is the descriptor;
    /// <c>basePath_files/</c> holds per-level tile subdirectories.
    /// </summary>
    public static Task SaveDeepZoomAsync(
        this VipsImage image,
        string basePath,
        int tileSize = 256,
        int overlap = 1,
        CosmoImage.Savers.VipsDzTileFormat format = CosmoImage.Savers.VipsDzTileFormat.Jpeg,
        int jpegQuality = 85)
        => VipsImageOps.SaveDeepZoomAsync(image, basePath, tileSize, overlap, format, jpegQuality);

    /// <summary>Save as Radiance HDR. 3-band image; UChar auto-cast to Float.</summary>
    public static Task SaveHdrAsync(this VipsImage image, PipeWriter writer)
        => VipsImageOps.SaveHdrAsync(image, writer);

    /// <summary>Save as FITS. UChar → BITPIX 8; Float → BITPIX -32. Multi-band → planar.</summary>
    public static Task SaveFitsAsync(this VipsImage image, PipeWriter writer)
        => VipsImageOps.SaveFitsAsync(image, writer);

    /// <summary>Save as BMP. 24/32 bpp BI_RGB; 1-band gray replicated to 24bpp.</summary>
    public static Task SaveBmpAsync(this VipsImage image, PipeWriter writer)
        => VipsImageOps.SaveBmpAsync(image, writer);

    /// <summary>Save as single-file NIfTI-1 (.nii). UChar → datatype 2; Float → datatype 16.</summary>
    public static Task SaveNiftiAsync(this VipsImage image, PipeWriter writer)
        => VipsImageOps.SaveNiftiAsync(image, writer);
}
