using System;
using System.IO.Pipelines;
using System.Threading.Tasks;
using ImageMagick;
using CosmoImage.Loaders;
using CosmoImage.Savers;
// Bring VipsPnmVariant enum into scope.
using CosmoImage.Operations.Geometric;
using CosmoImage.Operations.Color;
using CosmoImage.Operations.Convolution;
using CosmoImage.Operations.Drawing;
using CosmoImage.Operations.Effects;
using CosmoImage.Operations.Analysis;
using CosmoImage.Operations.Misc;
using CosmoImage.Operations.Mosaicing;
using CosmoImage.Operations.Create;
using CosmoImage.Operations.Iofuncs;

namespace CosmoImage.Core;

/// <summary>
/// Static factory and pipeline-runner API. Every implementation class lives
/// in its <c>Operations/{Category}</c> folder; this class is the unified
/// surface that the fluent extensions in <see cref="VipsImageExtensions"/>
/// also delegate to. Consolidated here from per-op partial-class blocks so
/// the <c>partial</c> declarations all share one namespace.
/// </summary>
public static partial class VipsImageOps
{
    // ===== Core =====
    // From Core/VipsOperation.cs
    private static VipsImage Run(VipsOperation op)
    {
        int key = op.GetCacheKey();
        var cached = VipsCache.Get(key);
        if (cached != null) return cached;

        // Profiling scope: zero cost when disabled, single timer pair otherwise.
        // Wraps Build + cache-add so we measure the full op-construction path.
        using var _profile = VipsProfiler.Enter(op.GetType().Name);

        if (op.Build() != 0) throw new Exception($"Failed to build {op.GetType().Name}");

        // Use reflection to get the 'Out' property value
        var result = (VipsImage)op.GetType().GetProperty("Out")!.GetValue(op)!;
        VipsCache.Add(key, result);
        return result;
    }

    public static VipsImage Invert(VipsImage input)
    {
        return Run(new VipsInvert { In = input });
    }

    public static Task SaveJpegAsync(VipsImage image, System.IO.Pipelines.PipeWriter writer, int quality = 75)
    {
        return VipsJpegSaver.SaveAsync(image, writer, quality);
    }

    public static Task SavePngAsync(VipsImage image, System.IO.Pipelines.PipeWriter writer, int? palette = null)
    {
        return VipsPngSaver.SaveAsync(image, writer, palette);
    }

    public static Task SaveWebpAsync(VipsImage image, System.IO.Pipelines.PipeWriter writer, int quality = 75, bool lossless = false)
    {
        return VipsWebpSaver.SaveAsync(image, writer, quality, lossless);
    }

    /// <summary>
    /// Save as TIFF. <paramref name="pyramid"/> writes tiled pyramidal TIFF
    /// (Ptif) for deep-zoom viewers; only effective on single-page input.
    /// </summary>
    public static Task SaveTiffAsync(VipsImage image, System.IO.Pipelines.PipeWriter writer, bool pyramid = false)
    {
        return VipsTiffSaver.SaveAsync(image, writer, pyramid);
    }

    // ===== Geometric =====
    // From Operations/Geometric/VipsAutoOrient.cs
    /// <summary>
    /// Apply the EXIF orientation tag to bring the image to canonical (top-left)
    /// orientation. Reads the raw int from <c>image.Metadata["orientation"]</c>
    /// (set by loaders that parse EXIF) and emits the corresponding flip/rotate
    /// combination. Other metadata (rest of EXIF, XMP, ICC) is preserved
    /// because every Flip/Rotate op now <c>CopyMetadataFrom</c>s its input.
    /// The orientation tag inside the raw EXIF blob is also patched to 1 so
    /// an EXIF-aware viewer that re-applies orientation on display doesn't
    /// rotate the (now canonically-oriented) pixels a second time. Direct
    /// port of libvips <c>vips_autorot</c>.
    ///
    /// Orientation reference (EXIF TIFF/JPEG tag 0x0112):
    ///   1: Top-left (no transform)            5: Mirror horizontal + 270 CW
    ///   2: Mirror horizontal                  6: Rotate 90 CW
    ///   3: Rotate 180                         7: Mirror horizontal + 90 CW
    ///   4: Mirror vertical                    8: Rotate 270 CW (= 90 CCW)
    /// </summary>
    public static VipsImage AutoOrient(VipsImage image)
    {
        if (!image.Metadata.TryGetValue("orientation", out var orientStr) ||
            !int.TryParse(orientStr, out int orient) ||
            orient <= 1 || orient > 8)
        {
            return image;
        }

        VipsImage result = orient switch
        {
            2 => Flip(image, VipsDirection.Horizontal),
            3 => Rotate(image, VipsAngle.D180),
            4 => Flip(image, VipsDirection.Vertical),
            5 => Rotate(Flip(image, VipsDirection.Horizontal), VipsAngle.D90),
            6 => Rotate(image, VipsAngle.D90),
            7 => Rotate(Flip(image, VipsDirection.Horizontal), VipsAngle.D270),
            8 => Rotate(image, VipsAngle.D270),
            _ => image
        };

        // Mark the output as canonically oriented so a second AutoOrient is a no-op.
        result.Metadata["orientation"] = "1";

        // Patch the orientation tag inside the raw EXIF blob too. Other tags
        // (datetime, GPS, camera info) are unchanged.
        if (result.MetadataBlobs.TryGetValue("exif", out var exif))
        {
            var patched = ExifPatcher.SetOrientation(exif, 1);
            if (!ReferenceEquals(patched, exif))
                result.MetadataBlobs["exif"] = patched;
        }

        return result;
    }

    // From Operations/Geometric/VipsAffine.cs
    public static VipsImage Affine(VipsImage input, double a, double b, double c, double d, double idx = 0, double idy = 0, VipsKernel interpolate = VipsKernel.Linear)
    {
        return Run(new VipsAffine { In = input, A = a, B = b, C = c, D = d, Idx = idx, Idy = idy, Interpolate = interpolate });
    }

    // From Operations/Geometric/VipsFlip.cs
    public static VipsImage Flip(VipsImage input, VipsDirection direction)
    {
        return Run(new VipsFlip { In = input, Direction = direction });
    }

    // From Operations/Geometric/VipsRotateAngle.cs
    /// <summary>
    /// Rotate by an arbitrary angle in degrees (positive = clockwise visual
    /// rotation, matching <see cref="VipsAngle.D90"/> = 90°). Output dimensions
    /// expand to the bounding box of the rotated input. Out-of-source samples
    /// are filled with transparent background, matching <see cref="VipsAffine"/>.
    /// </summary>
    /// <param name="image">Source image.</param>
    /// <param name="degrees">Rotation angle in degrees, clockwise.</param>
    /// <param name="kernel">Interpolation kernel; Lanczos3 for highest quality.</param>
    public static VipsImage Rotate(VipsImage image, double degrees, VipsKernel kernel = VipsKernel.Linear)
    {
        // Normalize to [-180, 180] and short-circuit common cases.
        degrees %= 360.0;
        if (degrees == 0.0) return image;
        if (degrees == 90.0 || degrees == -270.0) return Rotate(image, VipsAngle.D90);
        if (degrees == 180.0 || degrees == -180.0) return Rotate(image, VipsAngle.D180);
        if (degrees == 270.0 || degrees == -90.0) return Rotate(image, VipsAngle.D270);

        double rad = degrees * Math.PI / 180.0;
        double cos = Math.Cos(rad);
        double sin = Math.Sin(rad);

        int W = image.Width;
        int H = image.Height;

        // Bounding box of the rotated input. Ceil so we never lose pixels at
        // the corners; the extra perimeter is filled with the affine
        // out-of-source background (transparent).
        int newW = (int)Math.Ceiling(Math.Abs(cos) * W + Math.Abs(sin) * H);
        int newH = (int)Math.Ceiling(Math.Abs(sin) * W + Math.Abs(cos) * H);

        // VipsAffine maps output → input via (srcX, srcY) = M·(out) + (Idx, Idy).
        // Forward CW rotation in image-coords (y increases down) is
        // [[cos, -sin], [sin, cos]]; we want the inverse for the affine.
        double a = cos, b = sin, c = -sin, d = cos;

        // Center the rotation: source-center maps to dest-center.
        double cx = W / 2.0, cy = H / 2.0;
        double ocx = newW / 2.0, ocy = newH / 2.0;
        double idx = cx - a * ocx - b * ocy;
        double idy = cy - c * ocx - d * ocy;

        return Run(new VipsAffine
        {
            In = image,
            A = a, B = b, C = c, D = d,
            Idx = idx, Idy = idy,
            Interpolate = kernel,
            OutWidth = newW,
            OutHeight = newH,
        });
    }

    // From Operations/Geometric/VipsExtractArea.cs
    public static VipsImage ExtractArea(VipsImage input, int left, int top, int width, int height)
    {
        return Run(new VipsExtractArea { In = input, Left = left, Top = top, Width = width, Height = height });
    }

    // From Operations/Geometric/VipsShrink.cs
    public static VipsImage Shrink(VipsImage input, int hShrink, int vShrink)
    {
        return Run(new VipsShrink { In = input, HShrink = hShrink, VShrink = vShrink });
    }

    // From Operations/Geometric/VipsRotate.cs
    public static VipsImage Rotate(VipsImage input, VipsAngle angle)
    {
        return Run(new VipsRotate { In = input, Angle = angle });
    }

    // From Operations/Geometric/VipsDeskew.cs
    /// <summary>
    /// Auto-detect the document skew, rotate to straighten, and (by
    /// default) crop to the page bounding box. Skips resampling if the
    /// detected skew is below <paramref name="minAngleToCorrect"/>; the
    /// crop pass still runs since a non-rotated scan-bed image has
    /// margin to trim.
    /// </summary>
    public static VipsImage Deskew(VipsImage input,
        double maxAngleDegrees = VipsDeskew.DefaultMaxAngleDegrees,
        double minAngleToCorrect = 0.1,
        VipsKernel interpolate = VipsKernel.Linear,
        bool autoCrop = true,
        int autoCropThreshold = VipsDeskew.DefaultAutoCropThreshold)
        => VipsDeskew.Compute(input, maxAngleDegrees, minAngleToCorrect, interpolate, autoCrop, autoCropThreshold);

    /// <summary>
    /// Return the detected skew in degrees without rotating. Positive =
    /// image is rotated clockwise relative to upright.
    /// </summary>
    public static double DetectSkewDegrees(VipsImage input,
        double maxAngleDegrees = VipsDeskew.DefaultMaxAngleDegrees)
        => VipsDeskew.DetectSkewDegrees(input, maxAngleDegrees);

    /// <summary>
    /// Crop the image to its detected page bounding box (Canny + Hough).
    /// </summary>
    public static VipsImage AutoCropDocument(VipsImage input,
        int threshold = VipsDeskew.DefaultAutoCropThreshold)
        => VipsDeskew.AutoCropDocument(input, threshold);

    // From Operations/Analysis/VipsDetectPageBounds.cs
    /// <summary>
    /// Locate the page bounding rectangle by per-row/column counts of
    /// bright pixels (paper, including white margins). Returns the
    /// full-image rect when no row/column accumulates enough bright pixels.
    /// </summary>
    public static VipsRect DetectPageBounds(VipsImage input,
        int brightThreshold = VipsDetectPageBounds.DefaultBrightThreshold,
        int darkThreshold = VipsDetectPageBounds.DefaultDarkThreshold,
        double minNonBedFraction = VipsDetectPageBounds.DefaultMinNonBedFraction,
        double insetFraction = VipsDetectPageBounds.DefaultInsetFraction,
        int gapTolerance = VipsDetectPageBounds.DefaultGapTolerance)
        => VipsDetectPageBounds.Compute(input, brightThreshold, darkThreshold, minNonBedFraction, insetFraction, gapTolerance);

    // From Operations/Analysis/VipsDetectUpright.cs
    /// <summary>
    /// Return the upright-confidence signal for a document image.
    /// Positive ⇒ upright; negative ⇒ upside-down; near-zero ⇒ ambiguous.
    /// </summary>
    public static double DetectUpright(VipsImage input)
        => VipsDetectUpright.Compute(input);

    /// <summary>True if the document is rotated 180° from upright.</summary>
    public static bool IsUpsideDown(VipsImage input,
        double decisionThreshold = VipsDetectUpright.DefaultFlipDecisionThreshold)
        => VipsDetectUpright.IsUpsideDown(input, decisionThreshold);

    // From Operations/Geometric/VipsRot45.cs
    /// <summary>
    /// Rotate a square odd-sided image by a 45° increment. Mirrors libvips'
    /// <c>rot45</c> — chiefly for non-axis-aligned structuring elements
    /// (mathematical morphology). Out-of-bounds samples zero-fill.
    /// </summary>
    public static VipsImage Rot45(VipsImage input, VipsAngle45 angle = VipsAngle45.D45)
    {
        return Run(new VipsRot45 { In = input, Angle = angle });
    }

    // From Operations/Geometric/VipsThumbnail.cs
    public static VipsImage Thumbnail(VipsImage input, int width, int height = 0, bool crop = false)
    {
        if (height == 0) height = width;

        double hScale = (double)width / input.Width;
        double vScale = (double)height / input.Height;

        double scale = crop ? Math.Max(hScale, vScale) : Math.Min(hScale, vScale);

        // 1. Resize to target size (Resize handles internal Shrink + Bilinear)
        var resized = Resize(input, scale);

        if (crop)
        {
            // 2. Center crop if requested
            int left = (resized.Width - width) / 2;
            int top = (resized.Height - height) / 2;
            return ExtractArea(resized, left, top, width, height);
        }

        return resized;
    }

    // From Operations/Geometric/VipsResize1D.cs
    public static VipsImage Resize1D(VipsImage input, double scale, bool vertical, VipsKernel kernel = VipsKernel.Linear)
    {
        return Run(new VipsResize1D { In = input, Scale = scale, Vertical = vertical, Kernel = kernel });
    }

    // From Operations/Geometric/VipsEntropyCrop.cs
    /// <param name="input">Source image.</param>
    /// <param name="width">Target crop width (must be ≤ input.Width).</param>
    /// <param name="height">Target crop height (must be ≤ input.Height).</param>
    public static VipsImage EntropyCrop(VipsImage input, int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Crop dims must be positive.");
        if (width > input.Width || height > input.Height)
            throw new ArgumentOutOfRangeException(
                nameof(width),
                $"Crop {width}x{height} larger than source {input.Width}x{input.Height}.");
        if (width == input.Width && height == input.Height) return input;

        // Materialize source pixels — entropy needs full image data, not tiles.
        byte[] pixels;
        if (input.Pixels is { } existing)
        {
            pixels = existing;
        }
        else
        {
            var sink = new MemorySink(input);
            sink.RunAsync().GetAwaiter().GetResult();
            pixels = sink.Pixels;
        }

        int W = input.Width;
        int H = input.Height;
        int pelSize = input.SizeOfPel;

        // Iteratively trim from whichever edge contributes least entropy.
        int top = 0, bottom = H, left = 0, right = W;

        while (bottom - top > height)
        {
            double tE = RowEntropy(pixels, W, pelSize, top, left, right);
            double bE = RowEntropy(pixels, W, pelSize, bottom - 1, left, right);
            if (tE < bE) top++;
            else bottom--;
        }
        while (right - left > width)
        {
            double lE = ColEntropy(pixels, W, pelSize, left, top, bottom);
            double rE = ColEntropy(pixels, W, pelSize, right - 1, top, bottom);
            if (lE < rE) left++;
            else right--;
        }

        return ExtractArea(input, left, top, width, height);
    }

    private static double RowEntropy(byte[] pixels, int W, int pelSize, int row, int left, int right)
    {
        Span<int> hist = stackalloc int[256];
        int rowBase = row * W * pelSize;
        // Use the first band as a brightness proxy. For RGB this is R, which
        // correlates well enough with luma for entropy ranking.
        for (int x = left; x < right; x++)
            hist[pixels[rowBase + x * pelSize]]++;
        return Entropy(hist, right - left);
    }

    private static double ColEntropy(byte[] pixels, int W, int pelSize, int col, int top, int bottom)
    {
        Span<int> hist = stackalloc int[256];
        int colOffset = col * pelSize;
        int stride = W * pelSize;
        for (int y = top; y < bottom; y++)
            hist[pixels[y * stride + colOffset]]++;
        return Entropy(hist, bottom - top);
    }

    private static double Entropy(ReadOnlySpan<int> hist, int total)
    {
        if (total <= 0) return 0;
        double inv = 1.0 / total;
        double e = 0;
        for (int i = 0; i < 256; i++)
        {
            int c = hist[i];
            if (c == 0) continue;
            double p = c * inv;
            e -= p * Math.Log2(p);
        }
        return e;
    }

    // From Operations/Geometric/VipsResize.cs
    public static VipsImage Resize(VipsImage input, double scale, double vScale = 0, VipsKernel kernel = VipsKernel.Linear)
    {
        return Run(new VipsResize { In = input, Scale = scale, VScale = vScale, Kernel = kernel });
    }

    /// <summary>
    /// Resize with full mode + anchor + pad-colour control. Mirrors
    /// ImageSharp's <c>image.Mutate(c =&gt; c.Resize(new ResizeOptions { ... }))</c>.
    /// See <see cref="VipsResizeMode"/> for fit modes.
    /// </summary>
    public static VipsImage Resize(VipsImage input, VipsResizeOptions options)
        => VipsResizeWithOptions.Apply(input, options);

    // ===== Color =====
    // From Operations/Color/VipsLinearize.cs
    /// <summary>
    /// sRGB → linear-light. Pair with <see cref="Delinearize"/>: apply
    /// Linearize before resize/blur/composite operations and Delinearize
    /// at the end so blending happens in physically-meaningful linear space.
    /// </summary>
    public static VipsImage Linearize(VipsImage input)
        => Run(new VipsLinearize { In = input });

    /// <summary>linear-light → sRGB. Inverse of <see cref="Linearize"/>.</summary>
    public static VipsImage Delinearize(VipsImage input)
        => Run(new VipsDelinearize { In = input });

    // From Operations/Color/VipsLightness.cs
    /// <summary>
    /// Shift HSL Lightness by <paramref name="amount"/> (range -1..+1).
    /// Preserves hue and saturation, unlike <see cref="Brightness"/> which
    /// scales raw RGB.
    /// </summary>
    public static VipsImage Lightness(VipsImage input, double amount)
        => Run(new VipsLightness { In = input, Amount = amount });

    // From Operations/Color/VipsLinear.cs
    public static VipsImage Linear(VipsImage input, double[] a, double[] b)
    {
        return Run(new VipsLinear { In = input, A = a, B = b });
    }

    // From Operations/Color/VipsGamma.cs
    public static VipsImage Gamma(VipsImage input, double exponent)
    {
        return Run(new VipsGamma { In = input, Exponent = exponent });
    }

    // From Operations/Color/VipsColourspace.cs
    public static VipsImage Colourspace(VipsImage input, VipsInterpretation space)
    {
        return Run(new VipsColourspace { In = input, Space = space });
    }

    // From Operations/Color/VipsRecomb.cs
    public static VipsImage Recomb(VipsImage input, double[,] matrix)
    {
        return Run(new VipsRecomb { In = input, Matrix = matrix });
    }

    /// <summary>
    /// HSL-style saturation. <paramref name="s"/> = 0 → greyscale, 1 → identity,
    /// &gt; 1 → boost. Implemented as a 3×3 RGB matrix using Rec.709 luma weights;
    /// alpha passes through untouched on RGBA inputs.
    /// </summary>
    public static VipsImage Saturate(VipsImage input, double s)
    {
        const double lr = 0.2126, lg = 0.7152, lb = 0.0722; // Rec.709
        double inv = 1 - s;
        var matrix = new double[,]
        {
            { inv * lr + s, inv * lg,     inv * lb     },
            { inv * lr,     inv * lg + s, inv * lb     },
            { inv * lr,     inv * lg,     inv * lb + s }
        };
        return Recomb(input, matrix);
    }

    /// <summary>Convert to grey while keeping the input's band count (RGB→RGB or RGBA→RGBA).</summary>
    public static VipsImage Greyscale(VipsImage input) => Saturate(input, 0);

    /// <summary>
    /// Rotate hue around the (1,1,1) gray axis. <paramref name="degrees"/> = 0
    /// is identity. Closed-form 3×3 RGB matrix; alpha passes through.
    /// </summary>
    public static VipsImage Hue(VipsImage input, double degrees)
    {
        double rad = degrees * Math.PI / 180.0;
        double cos = Math.Cos(rad);
        double sin = Math.Sin(rad);
        double sqrt3 = Math.Sqrt(3.0);
        double aa = (1 + 2 * cos) / 3;
        double bb = (1 - cos - sqrt3 * sin) / 3;
        double cc = (1 - cos + sqrt3 * sin) / 3;
        var matrix = new double[,]
        {
            { aa, bb, cc },
            { cc, aa, bb },
            { bb, cc, aa }
        };
        return Recomb(input, matrix);
    }

    /// <summary>
    /// Apply a sepia tone using the standard Photoshop-derived 3×3 matrix.
    /// Alpha passes through.
    /// </summary>
    public static VipsImage Sepia(VipsImage input)
    {
        var matrix = new double[,]
        {
            { 0.393, 0.769, 0.189 },
            { 0.349, 0.686, 0.168 },
            { 0.272, 0.534, 0.131 }
        };
        return Recomb(input, matrix);
    }

    // From Operations/Color/VipsIccTransform.cs
    public static VipsImage IccTransform(VipsImage input, byte[] outputProfile, byte[]? inputProfile = null)
    {
        return Run(new VipsIccTransform { In = input, OutputProfile = outputProfile, InputProfile = inputProfile });
    }

    // From Operations/Color/VipsColorAdjust.cs
    /// <summary>
    /// Multiply pixels by <paramref name="amount"/>. 1.0 = identity, 0.5 darker,
    /// 1.5 brighter. Alpha (band 2 of grey-alpha or band 4 of RGBA) is left
    /// alone; only color bands are scaled.
    /// </summary>
    public static VipsImage Brightness(VipsImage input, double amount)
    {
        var (a, b) = ColorBandLinear(input.Bands, amount, 0.0);
        return Linear(input, a, b);
    }

    /// <summary>
    /// Stretch contrast around the midpoint 128. <paramref name="amount"/> = 1.0
    /// is identity, &gt; 1.0 boosts contrast, &lt; 1.0 reduces. Alpha is preserved.
    /// </summary>
    public static VipsImage Contrast(VipsImage input, double amount)
    {
        var (a, b) = ColorBandLinear(input.Bands, amount, 128.0 * (1 - amount));
        return Linear(input, a, b);
    }

    /// <summary>
    /// Build per-band <c>(a, b)</c> arrays for a Linear op that scales the
    /// color bands and leaves alpha (last band on grey-alpha or RGBA) at
    /// identity. Single-band and 3-band images get the scale on every band.
    /// </summary>
    private static (double[] a, double[] b) ColorBandLinear(int bands, double colorA, double colorB)
    {
        bool hasAlpha = bands == 2 || bands == 4;
        var a = new double[bands];
        var b = new double[bands];
        for (int i = 0; i < bands; i++)
        {
            bool isAlpha = hasAlpha && i == bands - 1;
            a[i] = isAlpha ? 1.0 : colorA;
            b[i] = isAlpha ? 0.0 : colorB;
        }
        return (a, b);
    }

    // ===== Convolution =====
    // From Operations/Convolution/VipsGaussBlur.cs
    public static VipsImage Conv1D(VipsImage input, double[] kernel, bool vertical)
    {
        return Run(new VipsConv1D { In = input, Kernel = kernel, Vertical = vertical });
    }

    public static VipsImage GaussBlur(VipsImage input, double sigma)
    {
        if (sigma < 0.1) return input;

        int kSize = (int)(sigma * 6.0);
        if (kSize % 2 == 0) kSize++;
        if (kSize < 3) kSize = 3;

        double[] kernel = new double[kSize];
        double sum = 0;
        int offset = kSize / 2;
        for (int i = 0; i < kSize; i++)
        {
            double x = i - offset;
            kernel[i] = Math.Exp(-(x * x) / (2 * sigma * sigma));
            sum += kernel[i];
        }

        for (int i = 0; i < kSize; i++) kernel[i] /= sum;

        var horizontal = Conv1D(input, kernel, false);
        return Conv1D(horizontal, kernel, true);
    }

    // From Operations/Convolution/VipsUnsharpMask.cs
    public static VipsImage UnsharpMask(VipsImage input, double sigma = 1.0, double amount = 1.0)
    {
        return Run(new VipsUnsharpMask { In = input, Sigma = sigma, Amount = amount });
    }

    /// <summary>
    /// Gaussian sharpen — blur with σ, subtract, add back amplified detail.
    /// Mirrors ImageSharp's <c>GaussianSharpen(sigma)</c>; thin wrapper over
    /// <see cref="UnsharpMask"/> with <c>amount = 1.0</c> (the standard
    /// sharpen recipe).
    /// </summary>
    public static VipsImage GaussianSharpen(VipsImage input, double sigma = 1.0)
        => UnsharpMask(input, sigma, amount: 1.0);

    // From Operations/Convolution/VipsConv.cs
    public static VipsImage Conv(VipsImage input, double[,] mask)
    {
        return Run(new VipsConv { In = input, Mask = mask });
    }

    // From Operations/Convolution/VipsMorph.cs
    public static VipsImage Morph(VipsImage input, double[,] mask, VipsMorphMethod method)
    {
        return Run(new VipsMorph { In = input, Mask = mask, Method = method });
    }

    public static VipsImage Dilate(VipsImage input, double[,] mask) => Morph(input, mask, VipsMorphMethod.Dilate);
    public static VipsImage Erode(VipsImage input, double[,] mask) => Morph(input, mask, VipsMorphMethod.Erode);

    /// <summary>
    /// Morphological opening: erode then dilate. Removes small bright specks
    /// while preserving overall shape. Composition of existing ops.
    /// </summary>
    public static VipsImage Open(VipsImage input, double[,] mask) => Dilate(Erode(input, mask), mask);

    /// <summary>
    /// Morphological closing: dilate then erode. Fills small dark gaps inside
    /// bright shapes. Composition of existing ops.
    /// </summary>
    public static VipsImage Close(VipsImage input, double[,] mask) => Erode(Dilate(input, mask), mask);

    // From Operations/Convolution/VipsRank.cs
    /// <summary>
    /// Rank (order-statistic) filter over a <paramref name="windowWidth"/>×
    /// <paramref name="windowHeight"/> window. <paramref name="index"/> is
    /// 0-based. <see cref="Median"/> wraps this with index = window center.
    /// </summary>
    public static VipsImage Rank(VipsImage input, int windowWidth, int windowHeight, int index)
        => Run(new VipsRank { In = input, WindowWidth = windowWidth, WindowHeight = windowHeight, Index = index });

    /// <summary>Median filter — Rank with index at the window's median position.</summary>
    public static VipsImage Median(VipsImage input, int windowSize = 3)
        => Rank(input, windowSize, windowSize, (windowSize * windowSize) / 2);

    // From Operations/Convolution/VipsBokehBlur.cs
    /// <summary>
    /// Hexagonal-aperture bokeh blur. <paramref name="radius"/> in pixels
    /// (≥ 1). Produces photographic hex-shaped specular highlights, unlike
    /// Gaussian blur which always rounds them.
    /// </summary>
    public static VipsImage BokehBlur(VipsImage input, int radius)
        => VipsBokehBlur.Run(input, radius);

    // ===== Drawing =====
    // From Operations/Drawing/VipsText.cs
    public static VipsImage Text(string text, string font = "Arial", int fontSize = 72)
    {
        // Text is a generator that creates a new image
        var settings = new MagickReadSettings
        {
            Font = font,
            FontPointsize = fontSize,
            BackgroundColor = MagickColors.Transparent,
            FillColor = MagickColors.White
        };

        using var image = new MagickImage();
        image.Read($"label:{text}", settings);

        // Convert MagickImage metadata to VipsImage
        var vipsImage = new VipsImage
        {
            Width = (int)image.Width,
            Height = (int)image.Height,
            Bands = 4,
            BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            XRes = 1.0,
            YRes = 1.0
        };

        // Cache the pixels immediately for text since it's usually small
        byte[] pixels = new byte[vipsImage.Width * vipsImage.Height * 4];
        using (var pc = image.GetPixels())
        {
            var data = pc.ToByteArray(0, 0, (uint)vipsImage.Width, (uint)vipsImage.Height, "RGBA");
            if (data != null) Array.Copy(data, pixels, data.Length);
        }

        vipsImage.GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) =>
        {
            byte[] pix = (byte[])a!;
            VipsRect r = reg.Valid;
            int stride = vipsImage.Width * 4;
            for (int y = 0; y < r.Height; y++)
            {
                var dest = reg.GetAddress(r.Left, r.Top + y);
                int srcOffset = (r.Top + y) * stride + r.Left * 4;
                pix.AsSpan(srcOffset, r.Width * 4).CopyTo(dest);
            }
            return 0;
        };
        vipsImage.ClientA = pixels;

        return vipsImage;
    }

    /// <summary>
    /// Draw shaped text onto <paramref name="canvas"/> using
    /// CosmoFonts (kerning via the legacy <c>kern</c> table). Mirrors
    /// ImageSharp's <c>image.Mutate(c =&gt; c.DrawText(text, font, color, point))</c>.
    /// </summary>
    public static VipsImage DrawText(VipsImage canvas, VipsTextOptions opts, bool aa = true)
        => VipsTextOps.DrawText(canvas, opts, aa);

    /// <summary>
    /// Shape text into a fillable <see cref="VipsPath"/> without
    /// rasterising — useful for combining the text outline with
    /// transforms / boolean ops / outline expansion before drawing.
    /// </summary>
    public static VipsPath TextToPath(VipsTextOptions opts) => VipsTextOps.TextToPath(opts);

    /// <summary>
    /// Lay shaped text along a target path (single sub-path).
    /// Mirrors SVG's <c>&lt;textPath&gt;</c>. <paramref name="offset"/>
    /// shifts the text perpendicular to the path; positive moves it
    /// "below" in screen y-down.
    /// </summary>
    public static VipsPath TextOnPath(VipsTextOptions opts, VipsPath targetPath, double offset = 0)
        => VipsTextOps.TextOnPath(opts, targetPath, offset);

    /// <summary>Measure the layout-box bounds of shaped text without rasterising.</summary>
    public static VipsTextSize MeasureText(VipsTextOptions opts) => VipsTextOps.MeasureText(opts);

    /// <summary>Measure the tight glyph-bounding box (sidebearings excluded).</summary>
    public static VipsTextSize MeasureTextBounds(VipsTextOptions opts) => VipsTextOps.MeasureBounds(opts);

    /// <summary>Count the number of (wrapped + explicit) lines the text would occupy.</summary>
    public static int CountTextLines(VipsTextOptions opts) => VipsTextOps.CountLines(opts);

    // From Operations/Drawing/VipsDrawRect.cs
    public static VipsImage DrawRect(VipsImage input, int left, int top, int width, int height, byte[] ink, bool fill = false)
    {
        return Run(new VipsDrawRect { In = input, Rect = new VipsRect(left, top, width, height), Ink = ink, Fill = fill });
    }

    // From Operations/Drawing/VipsDrawLine.cs
    public static VipsImage DrawLine(VipsImage input, int x1, int y1, int x2, int y2, byte[] ink)
    {
        return Run(new VipsDrawLine { In = input, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Ink = ink });
    }

    // From Operations/Drawing/VipsComposite.cs
    /// <summary>
    /// Composite <paramref name="overlay"/> onto <paramref name="baseImage"/>
    /// at position (<paramref name="x"/>, <paramref name="y"/>). Fractional
    /// offsets apply a sub-pixel Affine shift to the overlay before placement,
    /// giving smooth positioning at any continuous coordinate. Integer-aligned
    /// calls take a fast path that skips the affine.
    /// </summary>
    public static VipsImage Composite(VipsImage baseImage, VipsImage overlay, double x, double y)
        => Composite(baseImage, overlay, x, y, VipsCompositeMode.Over);

    /// <summary>
    /// Composite <paramref name="overlay"/> onto <paramref name="baseImage"/>
    /// at (<paramref name="x"/>, <paramref name="y"/>) using a Porter-Duff
    /// <paramref name="mode"/>. <see cref="VipsCompositeMode.Over"/> is the
    /// canonical "alpha-blend src on top of dst" default; the other modes
    /// trade source / destination roles or invert the alpha geometry.
    /// </summary>
    public static VipsImage Composite(VipsImage baseImage, VipsImage overlay, double x, double y,
        VipsCompositeMode mode)
    {
        int ix = (int)Math.Floor(x);
        int iy = (int)Math.Floor(y);
        double fx = x - ix;
        double fy = y - iy;

        // Fractional offset: pre-shift the overlay by the frac so its pixel
        // centers land at sub-pixel positions in the base. Affine reads
        // source = M·(out) + (idx, idy); to shift the overlay by +(fx, fy)
        // we set idx = -fx, idy = -fy.
        if (fx != 0.0 || fy != 0.0)
            overlay = Affine(overlay, 1, 0, 0, 1, idx: -fx, idy: -fy, interpolate: VipsKernel.Linear);

        return Run(new VipsComposite { Base = baseImage, Overlay = overlay, X = ix, Y = iy, Mode = mode });
    }

    // ===== Effects =====
    // From Operations/Effects/VipsPixelate.cs
    /// <summary>
    /// Pixelate the image into <paramref name="blockSize"/>×<paramref name="blockSize"/>
    /// blocks. Each block becomes a single uniform color (the average of the
    /// pixels under it). Implementation composes the existing integer
    /// <see cref="Shrink"/> (box-average downsample) with a nearest-neighbor
    /// <see cref="Resize"/> back to roughly the original size.
    /// </summary>
    public static VipsImage Pixelate(VipsImage input, int blockSize)
    {
        if (blockSize <= 1) return input;
        var shrunk = Shrink(input, blockSize, blockSize);
        // Nearest-neighbor upscale produces the blocky look; integer scale
        // factor avoids interpolation softening the block boundaries.
        return Resize(shrunk, (double)blockSize, kernel: VipsKernel.Nearest);
    }

    // From Operations/Effects/VipsArtisticEffects.cs
    /// <summary>Oil-paint effect (Magick.NET). <paramref name="radius"/> = brushstroke size.</summary>
    public static VipsImage OilPaint(VipsImage input, double radius = 3.0, double sigma = 1.0)
        => Run(new VipsOilPaint { In = input, Radius = radius, Sigma = sigma });

    /// <summary>Charcoal-sketch effect (Magick.NET).</summary>
    public static VipsImage Charcoal(VipsImage input, double radius = 1.0, double sigma = 0.5)
        => Run(new VipsCharcoal { In = input, Radius = radius, Sigma = sigma });

    /// <summary>Pencil-sketch effect (Magick.NET).</summary>
    public static VipsImage Sketch(VipsImage input, double radius = 1.0, double sigma = 0.5, double angle = 0.0)
        => Run(new VipsSketch { In = input, Radius = radius, Sigma = sigma, Angle = angle });

    // From Operations/Effects/VipsVignette.cs
    /// <summary>Apply a vignette (corner darkening). <paramref name="strength"/> 0..1.</summary>
    public static VipsImage Vignette(VipsImage input, double strength = 0.5)
        => Run(new VipsVignette { In = input, Strength = strength });

    // From Operations/Effects/VipsPolaroid.cs
    /// <summary>
    /// Polaroid effect: white border + rotation, RGBA output sized to the
    /// rotated bounding box. <paramref name="angle"/> in degrees (negative
    /// tilts left). Wraps Magick.NET.
    /// </summary>
    public static VipsImage Polaroid(VipsImage input, double angle = -5.0)
        => Run(new VipsPolaroid { In = input, Angle = angle });

    // From Operations/Effects/VipsGlow.cs
    /// <summary>
    /// Add a soft glow halo around bright areas. <paramref name="sigma"/>
    /// controls halo width, <paramref name="strength"/> controls intensity.
    /// </summary>
    public static VipsImage Glow(VipsImage input, double sigma = 5.0, double strength = 0.3)
        => Run(new VipsGlow { In = input, Sigma = sigma, Strength = strength });

    // ===== Analysis =====
    // From Operations/Analysis/VipsHistFind.cs
    public static VipsImage HistFind(VipsImage input)
    {
        return Run(new VipsHistFind { In = input });
    }

    // From Operations/Analysis/VipsHistEqual.cs
    public static VipsImage HistCum(VipsImage input) => Run(new VipsHistCum { In = input });
    public static VipsImage HistNorm(VipsImage input) => Run(new VipsHistNorm { In = input });

    public static VipsImage HistEqual(VipsImage input)
    {
        var hist = HistFind(input);
        var cum = HistCum(hist);
        var norm = HistNorm(cum);
        return Maplut(input, norm);
    }

    // From Operations/Analysis/VipsFft.cs
    public static VipsImage FwFft(VipsImage input)
    {
        return Run(new VipsFwFft { In = input });
    }

    // From Operations/Analysis/VipsInvFft.cs
    public static VipsImage InvFft(VipsImage input) => Run(new VipsInvFft { In = input });
    public static VipsImage Spectrum(VipsImage input) => Run(new VipsSpectrum { In = input });

    // From Operations/Analysis/VipsPhasecor.cs
    /// <summary>
    /// Phase correlation: returns a Float image whose peak is at
    /// <c>(Δx, Δy)</c> — the translation that best aligns
    /// <paramref name="in1"/> with <paramref name="in2"/>. Inputs must be
    /// equal-sized single-band UChar. Useful for image registration and
    /// motion estimation; brightness/contrast invariant due to whitening.
    /// </summary>
    public static VipsImage Phasecor(VipsImage in1, VipsImage in2)
        => Run(new VipsPhasecor { In1 = in1, In2 = in2 });

    // From Operations/Analysis/VipsStats.cs
    public static VipsStatsResult Stats(VipsImage input) => VipsStats.Compute(input);
    public static double Avg(VipsImage input) => VipsStats.Compute(input).Avg[input.Bands];
    public static double Min(VipsImage input) => VipsStats.Compute(input).Min[input.Bands];
    public static double Max(VipsImage input) => VipsStats.Compute(input).Max[input.Bands];
    public static double Deviate(VipsImage input) => VipsStats.Compute(input).Deviate[input.Bands];

    // ===== Misc =====
    // From Operations/Misc/VipsMaplut.cs
    public static VipsImage Maplut(VipsImage input, VipsImage lut)
    {
        return Run(new VipsMaplut { In = input, Lut = lut });
    }

    // From Operations/Misc/VipsCast.cs
    /// <summary>
    /// Numeric band-format conversion. UChar↔Float currently supported with
    /// no auto-normalization (UChar 100 → Float 100.0). Identity casts return
    /// the input unchanged. Mirrors libvips <c>vips_cast</c>.
    /// </summary>
    public static VipsImage Cast(VipsImage input, VipsBandFormat target)
    {
        if (input.BandFormat == target) return input;
        return Run(new VipsCast { In = input, TargetFormat = target });
    }

    public static VipsImage CastFloat(VipsImage input) => Cast(input, VipsBandFormat.Float);
    public static VipsImage CastUChar(VipsImage input) => Cast(input, VipsBandFormat.UChar);

    // From Operations/Misc/VipsMath.cs
    /// <summary>
    /// Pointwise math op. Treats UChar input as <c>x = byte/255</c>; trig
    /// variants use <c>x = (byte/255)·2π</c>. Output is UChar, scaled+clamped.
    /// </summary>
    public static VipsImage MathOp(VipsImage input, VipsMathOperation op, double operand = 0)
        => Run(new VipsMath { In = input, Op = op, Operand = operand });

    public static VipsImage Abs(VipsImage input) => MathOp(input, VipsMathOperation.Abs);
    public static VipsImage Sin(VipsImage input) => MathOp(input, VipsMathOperation.Sin);
    public static VipsImage Cos(VipsImage input) => MathOp(input, VipsMathOperation.Cos);
    public static VipsImage Tan(VipsImage input) => MathOp(input, VipsMathOperation.Tan);
    public static VipsImage Log(VipsImage input) => MathOp(input, VipsMathOperation.Log);
    public static VipsImage Log10(VipsImage input) => MathOp(input, VipsMathOperation.Log10);
    public static VipsImage Exp(VipsImage input) => MathOp(input, VipsMathOperation.Exp);
    public static VipsImage Exp10(VipsImage input) => MathOp(input, VipsMathOperation.Exp10);
    public static VipsImage Sqrt(VipsImage input) => MathOp(input, VipsMathOperation.Sqrt);
    public static VipsImage Pow(VipsImage input, double exponent) => MathOp(input, VipsMathOperation.Pow, exponent);

    // From Operations/Misc/VipsBoolean.cs
    public static VipsImage BooleanConst(VipsImage input, VipsBooleanOperation op, params double[] c)
        => Run(new VipsBooleanConst { In = input, Op = op, C = c });
    public static VipsImage Boolean(VipsImage left, VipsImage right, VipsBooleanOperation op)
        => Run(new VipsBoolean2 { Left = left, Right = right, Op = op });

    public static VipsImage AndConst(VipsImage input, params double[] c) => BooleanConst(input, VipsBooleanOperation.And, c);
    public static VipsImage OrConst(VipsImage input, params double[] c) => BooleanConst(input, VipsBooleanOperation.Or, c);
    public static VipsImage XorConst(VipsImage input, params double[] c) => BooleanConst(input, VipsBooleanOperation.Xor, c);
    public static VipsImage And(VipsImage left, VipsImage right) => Boolean(left, right, VipsBooleanOperation.And);
    public static VipsImage Or(VipsImage left, VipsImage right) => Boolean(left, right, VipsBooleanOperation.Or);
    public static VipsImage Xor(VipsImage left, VipsImage right) => Boolean(left, right, VipsBooleanOperation.Xor);

    public static VipsImage RelationalConst(VipsImage input, VipsRelationalOperation op, params double[] c)
        => Run(new VipsRelationalConst { In = input, Op = op, C = c });
    public static VipsImage Relational(VipsImage left, VipsImage right, VipsRelationalOperation op)
        => Run(new VipsRelational2 { Left = left, Right = right, Op = op });

    // From Operations/Misc/VipsArithmetic2.cs — image-image arithmetic
    /// <summary>Image + image, per pixel. UChar clamps; Float unclamped.</summary>
    public static VipsImage Add(VipsImage left, VipsImage right)
        => Run(new VipsArithmetic2 { Left = left, Right = right, Op = VipsArith2Op.Add });
    /// <summary>Image - image, per pixel. UChar clamps; Float unclamped.</summary>
    public static VipsImage Subtract(VipsImage left, VipsImage right)
        => Run(new VipsArithmetic2 { Left = left, Right = right, Op = VipsArith2Op.Subtract });
    /// <summary>Image * image, per pixel. UChar treats both as fractions of 255.</summary>
    public static VipsImage Multiply(VipsImage left, VipsImage right)
        => Run(new VipsArithmetic2 { Left = left, Right = right, Op = VipsArith2Op.Multiply });
    /// <summary>Image / image, per pixel. Divisor=0 → 0.</summary>
    public static VipsImage Divide(VipsImage left, VipsImage right)
        => Run(new VipsArithmetic2 { Left = left, Right = right, Op = VipsArith2Op.Divide });
    public static VipsImage Remainder(VipsImage left, VipsImage right)
        => Run(new VipsArithmetic2 { Left = left, Right = right, Op = VipsArith2Op.Remainder });

    // From Operations/Color/VipsPremultiply.cs
    /// <summary>Multiply colour bands by alpha (alpha-correct compositing prep).</summary>
    public static VipsImage Premultiply(VipsImage input)
        => Run(new VipsPremultiply { In = input, Mode = VipsAlphaMode.Premultiply });
    /// <summary>Divide colour bands by alpha (inverse of <see cref="Premultiply"/>).</summary>
    public static VipsImage Unpremultiply(VipsImage input)
        => Run(new VipsPremultiply { In = input, Mode = VipsAlphaMode.Unpremultiply });

    // From Operations/Color/VipsFlatten.cs
    /// <summary>
    /// Composite alpha-bearing image onto an opaque background colour and
    /// drop the alpha channel. Inputs without alpha pass through.
    /// </summary>
    public static VipsImage Flatten(VipsImage input, double[]? background = null)
        => Run(new VipsFlatten { In = input, Background = background });

    /// <summary>
    /// Add an opaque alpha channel to an image without one. 1-band → 2-band,
    /// 3-band → 4-band; alpha-bearing inputs pass through unchanged.
    /// </summary>
    public static VipsImage AddAlpha(VipsImage input, double alpha = 255.0)
        => VipsAddAlpha.Apply(input, alpha);

    /// <summary>
    /// Place image onto a larger canvas with a uniform background colour.
    /// Wraps <see cref="Embed"/> with <see cref="VipsExtend.Background"/>.
    /// </summary>
    public static VipsImage Pad(VipsImage input, int width, int height,
        double[]? background = null,
        VipsCompass position = VipsCompass.Centre)
    {
        // Compute where the input should land based on the compass anchor.
        int x = position switch
        {
            VipsCompass.NorthWest or VipsCompass.West or VipsCompass.SouthWest => 0,
            VipsCompass.NorthEast or VipsCompass.East or VipsCompass.SouthEast => width - input.Width,
            _ => (width - input.Width) / 2,
        };
        int y = position switch
        {
            VipsCompass.NorthWest or VipsCompass.North or VipsCompass.NorthEast => 0,
            VipsCompass.SouthWest or VipsCompass.South or VipsCompass.SouthEast => height - input.Height,
            _ => (height - input.Height) / 2,
        };
        return Embed(input, x, y, width, height, VipsExtend.Background, background);
    }

    /// <summary>
    /// Composite an alpha-bearing image onto a uniform background colour
    /// at the same dimensions. Same as <see cref="Flatten"/> but keeps the
    /// alpha channel (set to fully opaque) so the output is shape-compatible
    /// with the input for chaining into alpha-aware ops.
    /// </summary>
    public static VipsImage BackgroundColor(VipsImage input, params double[] background)
    {
        var flat = Flatten(input, background);
        if (input.Bands == 2 || input.Bands == 4)
            return AddAlpha(flat, input.BandFormat == VipsBandFormat.Float ? 1.0 : 255.0);
        return flat;
    }

    // From Operations/Analysis/VipsHistLocal.cs
    /// <summary>
    /// Contrast Limited Adaptive Histogram Equalization (CLAHE). Per-tile
    /// equalisation with bilinear interpolation between tile-CDFs, contrast
    /// limited via histogram clipping + redistribution. UChar only.
    /// </summary>
    public static VipsImage HistLocal(VipsImage input, int tileGridSize = 8, double clipLimit = 3.0)
        => Run(new VipsHistLocal { In = input, TileGridSize = tileGridSize, ClipLimit = clipLimit });

    // From Operations/Geometric/VipsEmbed.cs
    /// <summary>
    /// Place <paramref name="input"/> at (<paramref name="x"/>, <paramref name="y"/>)
    /// inside a <paramref name="width"/>×<paramref name="height"/> canvas, filling
    /// the rest by <paramref name="extend"/>. <paramref name="background"/>
    /// is per-band fill colour for <see cref="VipsExtend.Background"/>.
    /// </summary>
    public static VipsImage Embed(VipsImage input, int x, int y, int width, int height,
        VipsExtend extend = VipsExtend.Black, double[]? background = null)
        => Run(new VipsEmbed { In = input, X = x, Y = y, OutWidth = width, OutHeight = height,
            Extend = extend, Background = background });

    // From Operations/Misc/VipsBandjoin.cs
    /// <summary>Concatenate bands of N input images. All inputs must share W/H/format.</summary>
    public static VipsImage Bandjoin(params VipsImage[] inputs)
        => Run(new VipsBandjoin { Inputs = inputs });

    // From Operations/Misc/VipsExtractBand.cs
    /// <summary>Pull <paramref name="n"/> consecutive bands starting at <paramref name="band"/>.</summary>
    public static VipsImage ExtractBand(VipsImage input, int band, int n = 1)
        => Run(new VipsExtractBand { In = input, Band = band, N = n });

    // From Operations/Misc/VipsBandbool.cs
    /// <summary>Reduce across bands with a bitwise op (AND/OR/XOR). UChar only; output is single-band.</summary>
    public static VipsImage Bandbool(VipsImage input, VipsBooleanOperation op)
        => Run(new VipsBandbool { In = input, Op = op });

    /// <summary>Average bands → single-band output of the same band format.</summary>
    public static VipsImage Bandmean(VipsImage input)
        => Run(new VipsBandmean { In = input });

    // From Operations/Misc/VipsIfthenelse.cs
    /// <summary>
    /// Per-pixel ternary. Picks from <paramref name="then"/> where condition
    /// is non-zero, from <paramref name="@else"/> otherwise. Condition must
    /// be UChar with either 1 band or matching bands of <paramref name="then"/>.
    /// </summary>
    public static VipsImage Ifthenelse(VipsImage condition, VipsImage then, VipsImage @else)
        => Run(new VipsIfthenelse { Condition = condition, Then = then, Else = @else });

    // From Operations/Geometric/VipsReplicate.cs
    /// <summary>Tile <paramref name="across"/>×<paramref name="down"/> copies of the input.</summary>
    public static VipsImage Replicate(VipsImage input, int across, int down)
        => Run(new VipsReplicate { In = input, Across = across, Down = down });

    // From Operations/Color/VipsFalsecolor.cs
    /// <summary>
    /// Map a 1-band UChar image to RGB via a built-in 256-entry "jet"
    /// colour ramp — useful for visualising depth maps, masks, and
    /// histograms.
    /// </summary>
    public static VipsImage Falsecolor(VipsImage input)
        => Run(new VipsFalsecolor { In = input });

    // From Operations/Misc/VipsBandfold.cs
    /// <summary>Reshape <c>(W, H, B)</c> → <c>(W/factor, H, B*factor)</c>; default factor folds the whole row.</summary>
    public static VipsImage Bandfold(VipsImage input, int factor = 0)
        => Run(new VipsBandfold { In = input, Factor = factor });

    /// <summary>Inverse of <see cref="Bandfold"/> — <c>(W, H, B*factor)</c> → <c>(W*factor, H, B)</c>.</summary>
    public static VipsImage Bandunfold(VipsImage input, int factor = 0)
        => Run(new VipsBandunfold { In = input, Factor = factor });

    // From Operations/Misc/VipsBandjoinConst.cs
    /// <summary>Append constant bands. Each entry of <paramref name="c"/> becomes one extra band.</summary>
    public static VipsImage BandjoinConst(VipsImage input, params double[] c)
        => Run(new VipsBandjoinConst { In = input, C = c });

    // From Operations/Geometric/VipsWrap.cs
    /// <summary>Toroidal shift. Default offset puts the image centre at the origin.</summary>
    public static VipsImage Wrap(VipsImage input, int x = int.MinValue, int y = int.MinValue)
        => Run(new VipsWrap { In = input, X = x, Y = y });

    // From Operations/Geometric/VipsZoom.cs
    /// <summary>Integer scale-up by replication — each input pel covers an <paramref name="xfac"/>×<paramref name="yfac"/> block.</summary>
    public static VipsImage Zoom(VipsImage input, int xfac, int yfac)
        => Run(new VipsZoom { In = input, XFac = xfac, YFac = yfac });

    // From Operations/Misc/VipsScale.cs
    /// <summary>Linear-stretch input to UChar 0..255. <paramref name="log"/> uses log-scale (good for FFT magnitudes).</summary>
    public static VipsImage Scale(VipsImage input, bool log = false, double exponent = 0.25)
        => Run(new VipsScale { In = input, Log = log, Exponent = exponent });

    // From Operations/Analysis/VipsHistMatch.cs
    /// <summary>Remap <paramref name="input"/> so its CDF approximates that of <paramref name="reference"/>.</summary>
    public static VipsImage HistMatch(VipsImage input, VipsImage reference)
        => Run(new VipsHistMatch { In = input, Reference = reference });

    // From Operations/Analysis/VipsHistEntropy.cs
    /// <summary>Shannon entropy per band (bits) plus aggregate at index <c>Bands</c>.</summary>
    public static double[] HistEntropy(VipsImage input)
        => VipsHistEntropy.Compute(input);

    // From Operations/Analysis/VipsPercent.cs
    /// <summary>Pixel value below which <paramref name="percent"/>% of the (aggregate) histogram lies.</summary>
    public static int Percent(VipsImage input, double percent)
        => VipsPercent.Compute(input, percent);

    // From Operations/Misc/VipsBandrank.cs
    /// <summary>
    /// Per-pixel rank-statistic across N inputs. <paramref name="index"/> = -1
    /// picks the median; 0 picks the dimmest; N-1 picks the brightest.
    /// </summary>
    public static VipsImage Bandrank(VipsImage[] inputs, int index = -1)
        => Run(new VipsBandrank { Inputs = inputs, Index = index });

    // From Operations/Misc/VipsByteswap.cs
    /// <summary>Reverse byte order of every multi-byte sample. UChar pass-through.</summary>
    public static VipsImage Byteswap(VipsImage input)
        => Run(new VipsByteswap { In = input });

    // From Operations/Geometric/VipsGrid.cs
    /// <summary>
    /// Lay a tall <paramref name="input"/> (height = N×<paramref name="tileHeight"/>) into a
    /// <paramref name="across"/>×<paramref name="down"/> grid.
    /// </summary>
    public static VipsImage Grid(VipsImage input, int tileHeight, int across, int down)
        => Run(new VipsGrid { In = input, TileHeight = tileHeight, Across = across, Down = down });

    // From Operations/Convolution/VipsSobel.cs
    /// <summary>Sobel edge-magnitude detector. UChar in, UChar out (clamped).</summary>
    public static VipsImage Sobel(VipsImage input)
        => Run(new VipsSobel { In = input });

    // From Operations/Convolution/VipsCompass.cs
    /// <summary>8-direction Kirsch edge response — max absolute response across rotated kernels.</summary>
    public static VipsImage Compass(VipsImage input)
        => Run(new VipsCompassEdge { In = input });

    // From Operations/Convolution/VipsCanny.cs
    /// <summary>Canny edge detector. Single-band UChar in → binary UChar edge map.</summary>
    public static VipsImage Canny(VipsImage input, double sigma = 1.4, int low = 20, int high = 60)
        => Run(new VipsCanny { In = input, Sigma = sigma, LowThreshold = low, HighThreshold = high });

    // From Operations/Convolution/VipsSharpen.cs
    /// <summary>Tone-aware unsharp on luminance, scaled by separate shadow/highlight gains.</summary>
    public static VipsImage Sharpen(VipsImage input, double sigma = 1.0,
        double m1 = 1.0, double m2 = 1.0, int x1 = 2)
        => Run(new VipsSharpen { In = input, Sigma = sigma, M1 = m1, M2 = m2, X1 = x1 });

    // From Operations/Convolution/VipsNearest.cs
    /// <summary>Euclidean distance to nearest non-zero pixel (clamped to UChar).</summary>
    public static VipsImage Nearest(VipsImage input)
        => Run(new VipsNearest { In = input });

    // From Operations/Convolution/VipsLabelRegions.cs
    /// <summary>4-connected component labelling. UChar in → UInt label image (1..K, 0 = background).</summary>
    public static VipsImage LabelRegions(VipsImage input)
        => Run(new VipsLabelRegions { In = input });

    // From Operations/Convolution/VipsFastcor.cs
    /// <summary>
    /// FFT-accelerated cross-correlation. Output is UInt 1-band sized
    /// <c>(W − tw + 1, H − th + 1)</c>; values are <c>Σ in·ref</c> at each
    /// position (raw, not normalised). Use for fast template matching when
    /// brightness/contrast match — otherwise prefer <see cref="Spcor"/>.
    /// </summary>
    public static VipsImage Fastcor(VipsImage input, VipsImage reference)
        => Run(new VipsFastcor { In = input, Reference = reference });

    // From Operations/Convolution/VipsSpcor.cs
    /// <summary>Normalised cross-correlation of <paramref name="reference"/> against <paramref name="input"/>; result mapped [-1, 1] → [0, 255].</summary>
    public static VipsImage Spcor(VipsImage input, VipsImage reference)
        => Run(new VipsSpcor { In = input, Reference = reference });

    // From Operations/Analysis/VipsCountlines.cs
    /// <summary>Average number of black/white transitions per row (or column).</summary>
    public static double Countlines(VipsImage input, VipsDirection direction = VipsDirection.Horizontal)
        => VipsCountlines.Compute(input, direction);

    // From Operations/Convolution/VipsStdif.cs
    /// <summary>Local-contrast renormalisation. Each pixel is scaled toward a target mean and stddev within a window.</summary>
    public static VipsImage Stdif(VipsImage input, int windowWidth = 11, int windowHeight = 11,
        double sigmaTarget = 50, double meanTarget = 128, double a = 0.5)
        => Run(new VipsStdif {
            In = input, WindowWidth = windowWidth, WindowHeight = windowHeight,
            SigmaTarget = sigmaTarget, MeanTarget = meanTarget, A = a,
        });

    // From Operations/Analysis/VipsFreqmult.cs
    /// <summary>FwFft → multiply by real Float <paramref name="mask"/> → InvFft, in one step.</summary>
    public static VipsImage Freqmult(VipsImage input, VipsImage mask)
        => Run(new VipsFreqmult { In = input, Mask = mask });

    // From Operations/Misc/VipsSwitch.cs
    /// <summary>Index of the first non-zero test image at each pixel; <paramref name="tests"/>.Length if none.</summary>
    public static VipsImage Switch(params VipsImage[] tests)
        => Run(new VipsSwitch { Tests = tests });

    // From Operations/Misc/VipsCase.cs
    /// <summary>Per-pixel select from N source images using a UChar index.</summary>
    public static VipsImage Case(VipsImage index, params VipsImage[] cases)
        => Run(new VipsCase { Index = index, Cases = cases });

    // From Operations/Color/VipsLabXYZ.cs
    /// <summary>CIE Lab (D65) → XYZ.</summary>
    public static VipsImage Lab2XYZ(VipsImage input) => Run(new VipsLab2XYZ { In = input });

    /// <summary>CIE XYZ (D65) → Lab.</summary>
    public static VipsImage XYZ2Lab(VipsImage input) => Run(new VipsXYZ2Lab { In = input });

    // From Operations/Color/VipsLabLCh.cs
    /// <summary>Lab → LCh polar form (L, chroma, hue°).</summary>
    public static VipsImage Lab2LCh(VipsImage input) => Run(new VipsLab2LCh { In = input });

    /// <summary>LCh → Lab.</summary>
    public static VipsImage LCh2Lab(VipsImage input) => Run(new VipsLCh2Lab { In = input });

    // From Operations/Color/VipsDIN99.cs
    /// <summary>
    /// CIE L*a*b* → DIN99 (DIN 6176:2001). Perceptually-uniform Lab
    /// variant where Euclidean ΔE99 ≈ ΔE2000. Useful for clustering /
    /// nearest-neighbour searches in colour space.
    /// </summary>
    public static VipsImage Lab2DIN99(VipsImage input) => Run(new VipsLab2DIN99 { In = input });

    /// <summary>DIN99 → CIE L*a*b*. Inverse of <see cref="Lab2DIN99"/>.</summary>
    public static VipsImage DIN992Lab(VipsImage input) => Run(new VipsDIN992Lab { In = input });

    // From Operations/Color/VipsdE.cs
    /// <summary>Per-pixel CIE76 ΔE between two Lab images.</summary>
    public static VipsImage DE76(VipsImage left, VipsImage right)
        => Run(new VipsdE76 { Left = left, Right = right });

    /// <summary>Per-pixel CIEDE2000 ΔE between two Lab images.</summary>
    public static VipsImage DE2000(VipsImage left, VipsImage right)
        => Run(new VipsdE2000 { Left = left, Right = right });

    /// <summary>CIEDE2000 ΔE between two Lab triplets (no image required).</summary>
    public static double DE2000(double L1, double a1, double b1, double L2, double a2, double b2)
        => VipsdE2000.ComputeDE2000(L1, a1, b1, L2, a2, b2);

    // From Operations/Color/VipsXYZYxy.cs
    /// <summary>XYZ → Yxy chromaticity coordinates (Y, x, y).</summary>
    public static VipsImage XYZ2Yxy(VipsImage input) => Run(new VipsXYZ2Yxy { In = input });

    /// <summary>Yxy chromaticity → XYZ.</summary>
    public static VipsImage Yxy2XYZ(VipsImage input) => Run(new VipsYxy2XYZ { In = input });

    // From Operations/Color/VipsLabQ.cs
    /// <summary>Float Lab → libvips' 4-byte packed LabQ encoding.</summary>
    public static VipsImage Lab2LabQ(VipsImage input) => Run(new VipsLab2LabQ { In = input });

    /// <summary>libvips 4-byte LabQ → Float Lab.</summary>
    public static VipsImage LabQ2Lab(VipsImage input) => Run(new VipsLabQ2Lab { In = input });

    // From Operations/Color/VipsLabS.cs
    /// <summary>Float Lab → 16-bit signed-short LabS (high-precision intermediate format).</summary>
    public static VipsImage Lab2LabS(VipsImage input) => Run(new VipsLab2LabS { In = input });

    /// <summary>16-bit signed-short LabS → Float Lab.</summary>
    public static VipsImage LabS2Lab(VipsImage input) => Run(new VipsLabS2Lab { In = input });

    // From Operations/Color/VipsXYZOkLab.cs
    /// <summary>XYZ (D65) → OkLab (Ottosson 2020). Reference white maps to (1, 0, 0).</summary>
    public static VipsImage XYZ2OkLab(VipsImage input) => Run(new VipsXYZ2OkLab { In = input });

    /// <summary>OkLab → XYZ (D65).</summary>
    public static VipsImage OkLab2XYZ(VipsImage input) => Run(new VipsOkLab2XYZ { In = input });

    // From Operations/Color/VipsOkLabOkLCh.cs
    /// <summary>OkLab → OkLCh polar form.</summary>
    public static VipsImage OkLab2OkLCh(VipsImage input) => Run(new VipsOkLab2OkLCh { In = input });

    /// <summary>OkLCh → OkLab.</summary>
    public static VipsImage OkLCh2OkLab(VipsImage input) => Run(new VipsOkLCh2OkLab { In = input });

    // From Operations/Color/VipsHSV.cs
    /// <summary>sRGB UChar → HSV (libvips packing: H, S, V each 0..255).</summary>
    public static VipsImage SRGB2HSV(VipsImage input) => Run(new VipsSRGB2HSV { In = input });

    /// <summary>HSV → sRGB UChar.</summary>
    public static VipsImage HSV2sRGB(VipsImage input) => Run(new VipsHSV2sRGB { In = input });

    // From Operations/Color/VipsScRGBXYZ.cs
    /// <summary>scRGB (linear, sRGB primaries, D65) → XYZ.</summary>
    public static VipsImage ScRGB2XYZ(VipsImage input) => Run(new VipsScRGB2XYZ { In = input });

    /// <summary>XYZ → scRGB (linear, sRGB primaries, D65).</summary>
    public static VipsImage XYZ2scRGB(VipsImage input) => Run(new VipsXYZ2scRGB { In = input });

    /// <summary>
    /// CICP → scRGB. Decode an HDR / SDR image tagged with CICP
    /// colour primaries + transfer characteristics into linear-light
    /// scRGB (sRGB primaries, Float 3-band, unbounded). Default is
    /// BT.2020 + PQ — the BT.2100 HDR10 baseline.
    /// </summary>
    public static VipsImage Cicp2scRGB(VipsImage input,
        VipsCicpPrimaries primaries = VipsCicpPrimaries.BT2020,
        VipsCicpTransfer transfer = VipsCicpTransfer.PQ)
        => Run(new VipsCicp2scRGB { In = input, Primaries = primaries, Transfer = transfer });

    // From Operations/Color/VipsCMYK.cs
    /// <summary>Naïve CMYK → XYZ (no ICC profile).</summary>
    public static VipsImage CMYK2XYZ(VipsImage input) => Run(new VipsCMYK2XYZ { In = input });

    /// <summary>Naïve XYZ → CMYK (no ICC profile).</summary>
    public static VipsImage XYZ2CMYK(VipsImage input) => Run(new VipsXYZ2CMYK { In = input });

    // From Operations/Color/VipsdECMC.cs
    /// <summary>Per-pixel CMC(l:c) ΔE between two Lab images.</summary>
    public static VipsImage DECMC(VipsImage left, VipsImage right, double l = 2, double c = 1)
        => Run(new VipsdECMC { Left = left, Right = right, L = l, C = c });

    /// <summary>CMC(l:c) ΔE between two Lab triplets (no image required).</summary>
    public static double DECMC(double L1, double a1, double b1, double L2, double a2, double b2,
        double l = 2, double c = 1)
        => VipsdECMC.ComputeDECMC(L1, a1, b1, L2, a2, b2, l, c);

    // From Operations/Mosaicing/VipsArrayjoin.cs
    /// <summary>Lay N inputs out into a grid (default single row).</summary>
    public static VipsImage Arrayjoin(VipsImage[] inputs, int across = 0, int shim = 0,
        double[]? background = null,
        VipsAlign hAlign = VipsAlign.Low, VipsAlign vAlign = VipsAlign.Low)
        => Run(new VipsArrayjoin {
            Inputs = inputs, Across = across, Shim = shim, Background = background,
            HAlign = hAlign, VAlign = vAlign,
        });

    // From Operations/Mosaicing/VipsJoin.cs
    /// <summary>Paste two images side-by-side (or top-to-bottom) with optional linear-blend seam.</summary>
    public static VipsImage Join(VipsImage left, VipsImage right,
        VipsDirection direction = VipsDirection.Horizontal,
        int shim = 0, VipsAlign align = VipsAlign.Low, double[]? background = null)
        => Run(new VipsJoin {
            Left = left, Right = right, Direction = direction,
            Shim = shim, Align = align, Background = background,
        });

    // From Operations/Mosaicing/VipsInsert.cs
    /// <summary>Paste <paramref name="sub"/> into <paramref name="base"/> at (x, y); optionally expand to fit.</summary>
    public static VipsImage Insert(VipsImage @base, VipsImage sub, int x, int y,
        bool expand = false, double[]? background = null)
        => Run(new VipsInsert {
            Base = @base, Sub = sub, X = x, Y = y, Expand = expand, Background = background,
        });

    // From Operations/Create/VipsBlack.cs
    /// <summary>Synthesise an all-zero image of the requested geometry.</summary>
    public static VipsImage Black(int width, int height, int bands = 1,
        VipsBandFormat format = VipsBandFormat.UChar)
        => Run(new VipsBlack { Width = width, Height = height, Bands = bands, BandFormat = format });

    // From Operations/Create/VipsXyz.cs
    /// <summary>Synthesise a 2-band UInt image where each pixel = its (x, y) coordinate.</summary>
    public static VipsImage Xyz(int width, int height, int csize = 1, int dsize = 1, int esize = 1)
        => Run(new VipsXyz { Width = width, Height = height,
            Csize = csize, Dsize = dsize, Esize = esize });

    // From Operations/Create/VipsIdentity.cs
    /// <summary>Synthesise the identity LUT (256-wide UChar by default).</summary>
    public static VipsImage Identity(int bands = 1, bool ushort_ = false, int size = 0)
        => Run(new VipsIdentity { Bands = bands, UShort = ushort_, Size = size });

    // From Operations/Create/VipsBuildLut.cs
    /// <summary>Build a piecewise-linear LUT from anchor points <c>{x, y0[, y1, ...]}</c>.</summary>
    public static VipsImage BuildLut(double[,] points)
        => Run(new VipsBuildLut { Points = points });

    // From Operations/Create/VipsGaussmat.cs
    /// <summary>Synthesise a 2D Gaussian convolution kernel as a Float matrix image.</summary>
    public static VipsImage Gaussmat(double sigma = 1.0, double minAmpl = 0.1, bool separable = false)
        => Run(new VipsGaussmat { Sigma = sigma, MinAmpl = minAmpl, Separable = separable });

    // From Operations/Create/VipsSines.cs
    /// <summary>Synthesise a 2D sinusoid; frequencies in cycles per image.</summary>
    public static VipsImage Sines(int width, int height, double hFreq = 0.5, double vFreq = 0.5)
        => Run(new VipsSines { Width = width, Height = height, HFreq = hFreq, VFreq = vFreq });

    // From Operations/Create/VipsLogmat.cs
    /// <summary>Synthesise a Laplacian-of-Gaussian kernel as a Float matrix image.</summary>
    public static VipsImage Logmat(double sigma = 1.0, double minAmpl = 0.1)
        => Run(new VipsLogmat { Sigma = sigma, MinAmpl = minAmpl });

    // From Operations/Create/VipsGaussnoise.cs
    /// <summary>Synthesise a Gaussian-noise image (Float, Box-Muller). Seed = 0 → clock.</summary>
    public static VipsImage Gaussnoise(int width, int height,
        double mean = 128, double sigma = 30, int seed = 0)
        => Run(new VipsGaussnoise { Width = width, Height = height, Mean = mean, Sigma = sigma, Seed = seed });

    // From Operations/Create/VipsPerlin.cs
    /// <summary>Synthesise 2D Perlin noise (Ottosson 2002 fade curve).</summary>
    public static VipsImage Perlin(int width, int height, int cellSize = 256, int seed = 0)
        => Run(new VipsPerlin { Width = width, Height = height, CellSize = cellSize, Seed = seed });

    // From Operations/Create/VipsWorley.cs
    /// <summary>Synthesise 2D Worley/cellular noise (F1 distance).</summary>
    public static VipsImage Worley(int width, int height, int cellSize = 256, int seed = 0)
        => Run(new VipsWorley { Width = width, Height = height, CellSize = cellSize, Seed = seed });

    // From Operations/Create/VipsSdf.cs
    /// <summary>Signed-distance field of a circle (default origin = image centre).</summary>
    public static VipsImage SdfCircle(int width, int height, double radius,
        double cx = double.NaN, double cy = double.NaN)
        => Run(new VipsSdf {
            Width = width, Height = height, Shape = VipsSdfShape.Circle,
            Radius = radius, Cx = cx, Cy = cy,
        });

    /// <summary>Signed-distance field of an axis-aligned box.</summary>
    public static VipsImage SdfBox(int width, int height, double halfWidth, double halfHeight,
        double cx = double.NaN, double cy = double.NaN)
        => Run(new VipsSdf {
            Width = width, Height = height, Shape = VipsSdfShape.Box,
            HalfWidth = halfWidth, HalfHeight = halfHeight, Cx = cx, Cy = cy,
        });

    /// <summary>Signed-distance field of a rounded box.</summary>
    public static VipsImage SdfRoundedBox(int width, int height,
        double halfWidth, double halfHeight, double cornerRadius,
        double cx = double.NaN, double cy = double.NaN)
        => Run(new VipsSdf {
            Width = width, Height = height, Shape = VipsSdfShape.RoundedBox,
            HalfWidth = halfWidth, HalfHeight = halfHeight,
            CornerRadius = cornerRadius, Cx = cx, Cy = cy,
        });

    // From Operations/Create/VipsInvertlut.cs
    /// <summary>Invert a monotonic 1D LUT. Default <c>size = 0</c> keeps the input width.</summary>
    public static VipsImage Invertlut(VipsImage input, int size = 0)
        => Run(new VipsInvertlut { In = input, Size = size });

    // From Operations/Create/VipsEye.cs
    /// <summary>Eye-test pattern — horizontal frequency chirp × vertical amplitude ramp.</summary>
    public static VipsImage Eye(int width, int height, double factor = 0.5)
        => Run(new VipsEye { Width = width, Height = height, Factor = factor });

    // From Operations/Create/VipsZone.cs
    /// <summary>Zone-plate pattern — concentric cos(r²) rings, the canonical resize-aliasing diagnostic.</summary>
    public static VipsImage Zone(int width, int height)
        => Run(new VipsZone { Width = width, Height = height });

    // From Operations/Create/VipsTonelut.cs
    /// <summary>
    /// Build a tone-curve LUT from shadow lift, midtone gamma, and highlight compression.
    /// Output is a 256-wide UChar single-band LUT for use with <c>Maplut</c>.
    /// </summary>
    public static VipsImage Tonelut(double shadows = 0, double midtones = 1.0, double highlights = 0)
        => Run(new VipsTonelut { Shadows = shadows, Midtones = midtones, Highlights = highlights });

    // From Operations/Create/VipsMaskIdeal.cs
    /// <summary>Ideal-lowpass frequency-domain mask (centred). Float; 1 inside disc, 0 outside.</summary>
    public static VipsImage MaskIdealLowpass(int width, int height, double frequencyCutoff = 0.5)
        => Run(new VipsMaskIdealLowpass {
            Width = width, Height = height, FrequencyCutoff = frequencyCutoff,
        });

    /// <summary>Ideal-highpass frequency-domain mask (centred). Float; 0 inside disc, 1 outside.</summary>
    public static VipsImage MaskIdealHighpass(int width, int height, double frequencyCutoff = 0.5)
        => Run(new VipsMaskIdealHighpass {
            Width = width, Height = height, FrequencyCutoff = frequencyCutoff,
        });

    // From Operations/Create/VipsFractsurf.cs
    /// <summary>Fractal-surface noise — sum of Perlin octaves at successive frequencies.</summary>
    public static VipsImage Fractsurf(int width, int height, int octaves = 6,
        int baseCellSize = 256, double fractalDimension = 2.5, int seed = 0)
        => Run(new VipsFractsurf {
            Width = width, Height = height, Octaves = octaves,
            BaseCellSize = baseCellSize, FractalDimension = fractalDimension, Seed = seed,
        });

    // From Operations/Create/VipsMaskGaussian.cs
    /// <summary>Gaussian-lowpass frequency mask (centred).</summary>
    public static VipsImage MaskGaussianLowpass(int width, int height, double frequencyCutoff = 0.5)
        => Run(new VipsMaskGaussian {
            Width = width, Height = height, Mode = VipsMaskMode.Lowpass,
            FrequencyCutoff = frequencyCutoff,
        });

    /// <summary>Gaussian-highpass frequency mask (centred). 1 − lowpass.</summary>
    public static VipsImage MaskGaussianHighpass(int width, int height, double frequencyCutoff = 0.5)
        => Run(new VipsMaskGaussian {
            Width = width, Height = height, Mode = VipsMaskMode.Highpass,
            FrequencyCutoff = frequencyCutoff,
        });

    /// <summary>Gaussian band-pass ring at the given centre radius / width.</summary>
    public static VipsImage MaskGaussianRing(int width, int height,
        double frequencyCutoff = 0.5, double ringWidth = 0.1)
        => Run(new VipsMaskGaussian {
            Width = width, Height = height, Mode = VipsMaskMode.Ring,
            FrequencyCutoff = frequencyCutoff, RingWidth = ringWidth,
        });

    /// <summary>
    /// Gaussian directional band-pass — sum of two symmetric Gaussian peaks
    /// at <c>(±frequencyX · width/2, ±frequencyY · height/2)</c>. Use for
    /// orientation-selective frequency filtering (motion blur removal,
    /// directional sharpening).
    /// </summary>
    public static VipsImage MaskGaussianBand(int width, int height,
        double frequencyX, double frequencyY, double ringWidth = 0.1)
        => Run(new VipsMaskGaussian {
            Width = width, Height = height, Mode = VipsMaskMode.Band,
            FrequencyX = frequencyX, FrequencyY = frequencyY, RingWidth = ringWidth,
        });

    // From Operations/Create/VipsMaskButterworth.cs
    /// <summary>Butterworth-lowpass frequency mask.</summary>
    public static VipsImage MaskButterworthLowpass(int width, int height,
        double frequencyCutoff = 0.5, int order = 2)
        => Run(new VipsMaskButterworth {
            Width = width, Height = height, Mode = VipsMaskMode.Lowpass,
            FrequencyCutoff = frequencyCutoff, Order = order,
        });

    /// <summary>Butterworth-highpass frequency mask.</summary>
    public static VipsImage MaskButterworthHighpass(int width, int height,
        double frequencyCutoff = 0.5, int order = 2)
        => Run(new VipsMaskButterworth {
            Width = width, Height = height, Mode = VipsMaskMode.Highpass,
            FrequencyCutoff = frequencyCutoff, Order = order,
        });

    /// <summary>Butterworth band-pass ring.</summary>
    public static VipsImage MaskButterworthRing(int width, int height,
        double frequencyCutoff = 0.5, double ringWidth = 0.1, int order = 2)
        => Run(new VipsMaskButterworth {
            Width = width, Height = height, Mode = VipsMaskMode.Ring,
            FrequencyCutoff = frequencyCutoff, RingWidth = ringWidth, Order = order,
        });

    /// <summary>
    /// Butterworth directional band-pass — peaks at the symmetric pair
    /// <c>(±frequencyX · width/2, ±frequencyY · height/2)</c>. Min-distance
    /// Butterworth response; <paramref name="order"/> controls the rolloff.
    /// </summary>
    public static VipsImage MaskButterworthBand(int width, int height,
        double frequencyX, double frequencyY, double ringWidth = 0.1, int order = 2)
        => Run(new VipsMaskButterworth {
            Width = width, Height = height, Mode = VipsMaskMode.Band,
            FrequencyX = frequencyX, FrequencyY = frequencyY,
            RingWidth = ringWidth, Order = order,
        });

    /// <summary>
    /// Ideal directional band-pass — two unit-disc peaks at
    /// <c>(±frequencyX · width/2, ±frequencyY · height/2)</c>. Sharp edges
    /// induce spatial-domain ringing; for practical filtering use the
    /// Gaussian or Butterworth Band variants instead.
    /// </summary>
    public static VipsImage MaskIdealBand(int width, int height,
        double frequencyX, double frequencyY, double ringWidth = 0.1, bool reject = false)
        => Run(new VipsMaskIdealBand {
            Width = width, Height = height,
            FrequencyX = frequencyX, FrequencyY = frequencyY,
            RingWidth = ringWidth, Reject = reject,
        });

    // From Operations/Create/VipsMaskFractal.cs
    /// <summary>1/fᵅ frequency mask for spectral fractal-noise synthesis.</summary>
    public static VipsImage MaskFractal(int width, int height, double fractalDimension = 2.5)
        => Run(new VipsMaskFractal {
            Width = width, Height = height, FractalDimension = fractalDimension,
        });

    // From Operations/Geometric/VipsMapim.cs
    /// <summary>Generic remap: sample <paramref name="input"/> using a Float 2-band coordinate <paramref name="index"/> image.</summary>
    public static VipsImage Mapim(VipsImage input, VipsImage index, double[]? background = null)
        => Run(new VipsMapim { In = input, Index = index, Background = background });

    // From Operations/Geometric/VipsQuadratic.cs
    /// <summary>2D quadratic-polynomial coordinate warp; <paramref name="coefficients"/> = [a0..a5, b0..b5].</summary>
    public static VipsImage Quadratic(VipsImage input, double[] coefficients,
        int outWidth = 0, int outHeight = 0)
        => Run(new VipsQuadratic {
            In = input, Coefficients = coefficients,
            OutWidth = outWidth, OutHeight = outHeight,
        });

    // From Operations/Geometric/VipsSimilarity.cs
    /// <summary>Similarity transform — uniform scale + rotate + translate.</summary>
    public static VipsImage Similarity(VipsImage input,
        double scale = 1.0, double angle = 0.0, double idx = 0.0, double idy = 0.0,
        VipsKernel interpolate = VipsKernel.Linear)
        => Run(new VipsSimilarity {
            In = input, Scale = scale, Angle = angle, Idx = idx, Idy = idy,
            Interpolate = interpolate,
        });

    // From Operations/Drawing/VipsDrawCircle.cs
    /// <summary>Draw a circle (outline or filled) into a copy of <paramref name="input"/>.</summary>
    public static VipsImage DrawCircle(VipsImage input, int cx, int cy, int radius,
        byte[] ink, bool fill = false)
        => Run(new VipsDrawCircle { In = input, Cx = cx, Cy = cy, Radius = radius,
            Ink = ink, Fill = fill });

    // From Operations/Drawing/VipsDrawFlood.cs
    /// <summary>4-connected flood-fill from (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public static VipsImage DrawFlood(VipsImage input, int x, int y, byte[] ink)
        => Run(new VipsDrawFlood { In = input, X = x, Y = y, Ink = ink });

    // From Operations/Drawing/VipsDrawImage.cs
    /// <summary>Paste <paramref name="sub"/> into <paramref name="input"/> at (x, y); output stays input-sized.</summary>
    public static VipsImage DrawImage(VipsImage input, VipsImage sub, int x, int y)
        => Run(new VipsDrawImage { In = input, Sub = sub, X = x, Y = y });

    // From Operations/Drawing/VipsDrawMask.cs
    /// <summary>Apply <paramref name="ink"/> through a single-band UChar alpha <paramref name="mask"/>.</summary>
    public static VipsImage DrawMask(VipsImage input, VipsImage mask, int x, int y, byte[] ink)
        => Run(new VipsDrawMask { In = input, Mask = mask, X = x, Y = y, Ink = ink });

    // From Operations/Drawing/VipsDrawSmudge.cs
    /// <summary>Smudge / soft-erase a rectangular region with a 3×3 local average.</summary>
    public static VipsImage DrawSmudge(VipsImage input, int x, int y, int width, int height)
        => Run(new VipsDrawSmudge { In = input, X = x, Y = y, Width = width, Height = height });

    // From Operations/Misc/VipsSum.cs
    /// <summary>Pixel-wise sum across N inputs. UChar branch clamps; cast to Float for un-clamped sum.</summary>
    public static VipsImage Sum(params VipsImage[] inputs)
        => Run(new VipsSum { Inputs = inputs });

    // From Operations/Misc/VipsImageMinMax.cs
    /// <summary>Pixel-wise minimum across N inputs.</summary>
    public static VipsImage MinImage(params VipsImage[] inputs)
        => Run(new VipsImageMin { Inputs = inputs });

    /// <summary>Pixel-wise maximum across N inputs.</summary>
    public static VipsImage MaxImage(params VipsImage[] inputs)
        => Run(new VipsImageMax { Inputs = inputs });

    // From Operations/Analysis/VipsProject.cs
    /// <summary>Project image to (column-sum 1×W image, row-sum 1×H image).</summary>
    public static (VipsImage Columns, VipsImage Rows) Project(VipsImage input)
        => VipsProject.Compute(input);

    // From Operations/Analysis/VipsFindTrim.cs
    /// <summary>Find the bounding box of non-background pixels (defaults to top-left as background).</summary>
    public static VipsRect FindTrim(VipsImage input, int threshold = 10, double[]? background = null)
        => VipsFindTrim.Compute(input, threshold, background);

    // From Operations/Misc/VipsMath.cs (extended ops)
    public static VipsImage Sign(VipsImage input) => MathOp(input, VipsMathOperation.Sign);
    public static VipsImage Floor(VipsImage input) => MathOp(input, VipsMathOperation.Floor);
    public static VipsImage Ceil(VipsImage input) => MathOp(input, VipsMathOperation.Ceil);
    public static VipsImage Rint(VipsImage input) => MathOp(input, VipsMathOperation.Rint);

    // From Operations/Analysis/VipsComplexForm.cs
    /// <summary>Build a DPComplex image from two Float (real, imag) inputs.</summary>
    public static VipsImage ComplexForm(VipsImage real, VipsImage imag)
        => Run(new VipsComplexForm { Real = real, Imag = imag });

    // From Operations/Analysis/VipsComplexGet.cs
    /// <summary>Extract a real Float component from a DPComplex image.</summary>
    public static VipsImage ComplexGet(VipsImage input, VipsComplexGetMode mode)
        => Run(new VipsComplexGet { In = input, Mode = mode });

    public static VipsImage Real(VipsImage input) => ComplexGet(input, VipsComplexGetMode.Real);
    public static VipsImage Imag(VipsImage input) => ComplexGet(input, VipsComplexGetMode.Imag);
    public static VipsImage Magnitude(VipsImage input) => ComplexGet(input, VipsComplexGetMode.Magnitude);
    public static VipsImage Phase(VipsImage input) => ComplexGet(input, VipsComplexGetMode.Phase);

    // From Operations/Analysis/VipsComplex.cs
    /// <summary>Per-pixel unary op on a DPComplex image (Polar / Rect / Conj).</summary>
    public static VipsImage Complex(VipsImage input, VipsComplexOp op)
        => Run(new VipsComplex { In = input, Op = op });

    public static VipsImage Polar(VipsImage input) => Complex(input, VipsComplexOp.Polar);
    public static VipsImage Rect(VipsImage input) => Complex(input, VipsComplexOp.Rect);
    public static VipsImage Conj(VipsImage input) => Complex(input, VipsComplexOp.Conj);

    // From Operations/Analysis/VipsComplex2.cs
    /// <summary>Per-pixel binary op on two DPComplex images (CrossPhase by default).</summary>
    public static VipsImage CrossPhase(VipsImage left, VipsImage right)
        => Run(new VipsComplex2 { Left = left, Right = right, Op = VipsComplex2Op.CrossPhase });

    // From Operations/Misc/VipsMath.cs (Atan)
    public static VipsImage Atan(VipsImage input) => MathOp(input, VipsMathOperation.Atan);

    // From Operations/Misc/VipsMath2.cs
    /// <summary>Per-pixel binary math op on two images.</summary>
    public static VipsImage Math2(VipsImage left, VipsImage right, VipsMath2Operation op)
        => Run(new VipsMath2 { Left = left, Right = right, Op = op });

    public static VipsImage Pow(VipsImage left, VipsImage right) => Math2(left, right, VipsMath2Operation.Pow);
    public static VipsImage Wop(VipsImage left, VipsImage right) => Math2(left, right, VipsMath2Operation.Wop);
    public static VipsImage Atan2(VipsImage left, VipsImage right) => Math2(left, right, VipsMath2Operation.Atan2);

    // From Operations/Misc/VipsClamp.cs
    /// <summary>Per-band clamp to [<paramref name="min"/>, <paramref name="max"/>] range.</summary>
    public static VipsImage Clamp(VipsImage input, double min = 0, double max = 255)
        => Run(new VipsClamp { In = input, Min = min, Max = max });

    /// <summary>
    /// Broadcast-scalar variant of <see cref="Linear"/> — apply
    /// <c>output = a · input + b</c> with single scalars instead of
    /// per-band arrays.
    /// </summary>
    public static VipsImage LinearConst(VipsImage input, double a, double b)
    {
        int bands = input.Bands;
        var aArr = new double[bands];
        var bArr = new double[bands];
        for (int i = 0; i < bands; i++) { aArr[i] = a; bArr[i] = b; }
        return Linear(input, aArr, bArr);
    }

    // From Operations/Analysis/VipsMeasure.cs
    /// <summary>Sample patch averages from a regular <paramref name="h"/>×<paramref name="v"/> grid.</summary>
    public static VipsImage Measure(VipsImage input, int h, int v,
        int left = 0, int top = 0, int width = 0, int height = 0)
        => VipsMeasure.Compute(input, h, v, left, top, width, height);

    // From Operations/Analysis/VipsHoughLine.cs
    /// <summary>Line Hough transform (votes in (ρ, θ) space).</summary>
    public static VipsImage HoughLine(VipsImage input,
        int width = 256, int height = 256, int threshold = 128)
        => Run(new VipsHoughLine {
            In = input, Width = width, Height = height, Threshold = threshold,
        });

    // From Operations/Analysis/VipsHoughCircle.cs
    /// <summary>Circle Hough transform for a fixed (or banded) radius.</summary>
    public static VipsImage HoughCircle(VipsImage input,
        int minRadius = 10, int maxRadius = 20, int threshold = 128)
        => Run(new VipsHoughCircle {
            In = input, MinRadius = minRadius, MaxRadius = maxRadius, Threshold = threshold,
        });

    // From Operations/Analysis/VipsHistFindIndexed.cs
    /// <summary>Per-bin reduction of <paramref name="input"/> keyed by <paramref name="index"/>.</summary>
    public static VipsImage HistFindIndexed(VipsImage input, VipsImage index,
        VipsHistIndexedReduction reduction = VipsHistIndexedReduction.Sum)
        => Run(new VipsHistFindIndexed { In = input, Index = index, Reduction = reduction });

    // From Operations/Analysis/VipsHistPlot.cs
    /// <summary>Render a 1-row histogram as a bar-chart image of the given height.</summary>
    public static VipsImage HistPlot(VipsImage input, int height = 256)
        => Run(new VipsHistPlot { In = input, Height = height });

    // From Operations/Analysis/VipsHistFindNDim.cs
    /// <summary>N-dim histogram (1, 2, or 3-band UChar). UInt accumulator output.</summary>
    public static VipsImage HistFindNDim(VipsImage input, int bins = 10)
        => Run(new VipsHistFindNDim { In = input, Bins = bins });

    // From Operations/Analysis/VipsGetpoint.cs
    /// <summary>Read a single pixel as a double[] of band values.</summary>
    public static double[] Getpoint(VipsImage input, int x, int y)
        => VipsGetpoint.Compute(input, x, y);

    // From Operations/Analysis/VipsProfile.cs
    /// <summary>Per-axis first-non-zero profile. Returns (Columns: 1×W, Rows: 1×H) UInt images.</summary>
    public static (VipsImage Columns, VipsImage Rows) Profile(VipsImage input)
        => VipsProfile.Compute(input);

    // From Operations/Create/VipsGrey.cs
    /// <summary>Horizontal grey ramp 0..1 (Float) or 0..255 (UChar).</summary>
    public static VipsImage Grey(int width, int height, bool uchar = false)
        => Run(new VipsGrey { Width = width, Height = height, UChar = uchar });

    // From Operations/Convolution/VipsConvSep.cs
    /// <summary>Separable 1D convolution — applies <paramref name="kernel"/> horizontally then vertically.</summary>
    public static VipsImage ConvSep(VipsImage input, double[] kernel)
        => Run(new VipsConvSep { In = input, Kernel = kernel });

    // From Operations/Convolution/VipsBoxBlur.cs
    /// <summary>N-pass box blur via running-sum (O(W·H) per pass regardless of radius).</summary>
    public static VipsImage BoxBlur(VipsImage input, int radius = 3, int passes = 3)
        => Run(new VipsBoxBlur { In = input, Radius = radius, Passes = passes });

    // From Operations/Convolution/VipsEdge.cs
    /// <summary>Generic edge-detector dispatcher (Sobel / Compass / Canny).</summary>
    public static VipsImage Edge(VipsImage input, VipsEdgeMethod method = VipsEdgeMethod.Sobel,
        double cannySigma = 1.4, int cannyLow = 20, int cannyHigh = 60)
        => VipsEdge.Apply(input, method, cannySigma, cannyLow, cannyHigh);

    // From Operations/Iofuncs/VipsCache.cs
    /// <summary>Materialise the input once; downstream consumers reuse the cached pixels.</summary>
    public static VipsImage Cache(VipsImage input)
        => Run(new VipsCacheOp { In = input });

    // From Operations/Iofuncs/VipsSequential.cs
    /// <summary>Force sequential top-to-bottom evaluation. Wrap before a streaming saver.</summary>
    public static VipsImage Sequential(VipsImage input)
        => Run(new VipsSequential { In = input });

    // From Operations/Iofuncs/VipsCopy.cs
    /// <summary>
    /// Stream the input through unchanged but with optional metadata
    /// rewrites (interpretation / band-format / band-count / x/y-res / coding).
    /// Pixel bytes pass through verbatim; format/band rewrites reinterpret.
    /// </summary>
    public static VipsImage Copy(VipsImage input,
        VipsInterpretation? interpretation = null,
        VipsBandFormat? bandFormat = null,
        int? bands = null,
        double? xRes = null,
        double? yRes = null,
        VipsCoding? coding = null)
        => Run(new VipsCopy {
            In = input,
            Interpretation = interpretation, BandFormat = bandFormat, Bands = bands,
            XRes = xRes, YRes = yRes, Coding = coding,
        });

    // From Operations/Color/VipsOpacity.cs
    /// <summary>Multiply the alpha channel by <paramref name="amount"/> (0..1).</summary>
    public static VipsImage Opacity(VipsImage input, double amount)
        => Run(new VipsOpacity { In = input, Amount = amount });

    // From Operations/Color/VipsThreshold.cs
    /// <summary>Per-band binary threshold: ≥ <paramref name="value"/> → 255, else 0.</summary>
    public static VipsImage Threshold(VipsImage input, double value = 128)
        => Run(new VipsThreshold { In = input, Value = value });

    /// <summary>Alias for <c>Saturate(0)</c>; ImageSharp `BlackWhite()` parity.</summary>
    public static VipsImage BlackWhite(VipsImage input) => Saturate(input, 0);

    // From Operations/Drawing/VipsClear.cs
    /// <summary>Fill the entire image with <paramref name="color"/>.</summary>
    public static VipsImage Clear(VipsImage input, params double[] color)
        => Run(new VipsClear { In = input, Color = color });

    // From Operations/Color/VipsColorMatrix.cs
    /// <summary>4×5 colour-matrix transform on RGBA. ImageSharp `Filter(ColorMatrix)` parity.</summary>
    public static VipsImage ColorMatrix(VipsImage input, double[,] matrix)
        => Run(new VipsColorMatrix { In = input, Matrix = matrix });

    /// <summary>
    /// Skew (shear) the image by the given X / Y degrees. Composes
    /// the shear matrix and dispatches to <see cref="Affine"/>.
    /// </summary>
    public static VipsImage Skew(VipsImage input, double degreesX, double degreesY,
        VipsKernel interpolate = VipsKernel.Linear)
    {
        double tanX = Math.Tan(degreesX * Math.PI / 180.0);
        double tanY = Math.Tan(degreesY * Math.PI / 180.0);
        // Affine reads source coords from output, so invert: the inverse of
        // [[1, tanX], [tanY, 1]] is [[1, -tanX], [-tanY, 1]] / (1 - tanX*tanY).
        double det = 1 - tanX * tanY;
        if (Math.Abs(det) < 1e-12) return input; // degenerate
        double a = 1.0 / det, bb = -tanX / det;
        double c = -tanY / det, d = 1.0 / det;
        return Affine(input, a, bb, c, d, 0, 0, interpolate);
    }

    // From Operations/Color/VipsStylisedFilters.cs
    /// <summary>Kodachrome film-stock-style colour transform.</summary>
    public static VipsImage Kodachrome(VipsImage input) => VipsKodachrome.Apply(input);

    /// <summary>Lomograph-style saturated colour transform.</summary>
    public static VipsImage Lomograph(VipsImage input) => VipsLomograph.Apply(input);

    /// <summary>
    /// Simulate colour-vision deficiency for accessibility preview.
    /// Brettel-Vienot-Mollon (1997) dichromacy / anomaly matrices.
    /// </summary>
    public static VipsImage ColorBlindness(VipsImage input, VipsColorBlindnessMode mode)
        => VipsColorBlindness.Apply(input, mode);

    // From Operations/Convolution/VipsEdgeKernel.cs
    /// <summary>Edge magnitude via a fixed kernel (Roberts / Prewitt / Laplacian).</summary>
    public static VipsImage EdgeKernel(VipsImage input, VipsEdgeKernel kernel)
        => Run(new VipsEdgeKernelOp { In = input, Kernel = kernel });

    public static VipsImage Roberts(VipsImage input) => EdgeKernel(input, VipsEdgeKernel.Roberts);
    public static VipsImage Prewitt(VipsImage input) => EdgeKernel(input, VipsEdgeKernel.Prewitt);
    public static VipsImage Laplacian(VipsImage input) => EdgeKernel(input, VipsEdgeKernel.Laplacian);

    // From Operations/Color/VipsDither.cs
    /// <summary>
    /// Quantise to <paramref name="levels"/> per band using the chosen
    /// dither method (default Floyd-Steinberg, 2 levels = 1-bit-per-band).
    /// </summary>
    public static VipsImage Dither(VipsImage input,
        VipsDitherMethod method = VipsDitherMethod.FloydSteinberg, int levels = 2)
        => Run(new VipsDither { In = input, Method = method, Levels = levels });

    /// <summary>Specialised 1-bit dither (alias for <c>Dither(method, levels=2)</c>).</summary>
    public static VipsImage BinaryDither(VipsImage input,
        VipsDitherMethod method = VipsDitherMethod.FloydSteinberg)
        => Dither(input, method, levels: 2);

    /// <summary>Invert a binary image — alias for <see cref="Invert"/>.</summary>
    public static VipsImage BinaryInvert(VipsImage input) => Invert(input);

    /// <summary>
    /// Adaptive histogram equalisation — ImageSharp-named alias for
    /// libvips' <see cref="HistLocal"/> (CLAHE).
    /// </summary>
    public static VipsImage AdaptiveHistogramEqualization(VipsImage input,
        int tileGridSize = 8, double clipLimit = 3.0)
        => HistLocal(input, tileGridSize, clipLimit);

    // From Loaders/VipsIdentify.cs
    /// <summary>
    /// Sniff <paramref name="stream"/> and return the detected
    /// <see cref="CosmoImage.Loaders.VipsImageFormat"/> + a
    /// header-only <see cref="VipsImage"/> when supported. Mirrors
    /// ImageSharp's <c>Image.IdentifyAsync</c>.
    /// </summary>
    public static System.Threading.Tasks.ValueTask<CosmoImage.Loaders.VipsIdentifyResult>
        IdentifyAsync(System.IO.Stream stream, System.Threading.CancellationToken ct = default)
        => CosmoImage.Loaders.VipsIdentify.IdentifyAsync(stream, ct);

    /// <summary>
    /// Sniff <paramref name="stream"/>, dispatch to the right loader,
    /// and return the loaded <see cref="VipsImage"/>. Mirrors
    /// ImageSharp's <c>Image.LoadAsync</c>.
    /// </summary>
    public static System.Threading.Tasks.ValueTask<VipsImage?>
        LoadAsync(System.IO.Stream stream, System.Threading.CancellationToken ct = default)
        => CosmoImage.Loaders.VipsIdentify.LoadAsync(stream, ct);

    /// <summary>
    /// Load with an explicit <see cref="VipsConfiguration"/>. Useful for
    /// scoped custom format registrations that don't pollute the global
    /// <see cref="VipsConfiguration.Default"/>.
    /// </summary>
    public static System.Threading.Tasks.ValueTask<VipsImage?>
        LoadAsync(System.IO.Stream stream, VipsConfiguration configuration,
            System.Threading.CancellationToken ct = default)
        => CosmoImage.Loaders.VipsIdentify.LoadAsync(stream, configuration, ct);

    // From Operations/Color/VipsPixelOperations.cs
    /// <summary>Convert to single-band UChar via BT.601 luminance.</summary>
    public static VipsImage ToL8(VipsImage input)
        => Run(new VipsPixelOperations { In = input, TargetBands = 1 });

    /// <summary>Convert to 2-band UChar (luminance + alpha; opaque if input has none).</summary>
    public static VipsImage ToLa16(VipsImage input)
        => Run(new VipsPixelOperations { In = input, TargetBands = 2 });

    /// <summary>Convert to 3-band UChar RGB. 1-band replicates; 4-band drops alpha.</summary>
    public static VipsImage ToRgb24(VipsImage input)
        => Run(new VipsPixelOperations { In = input, TargetBands = 3 });

    /// <summary>Convert to 4-band UChar RGBA. 1/2/3-band synthesises opaque alpha.</summary>
    public static VipsImage ToRgba32(VipsImage input)
        => Run(new VipsPixelOperations { In = input, TargetBands = 4 });

    /// <summary>Swap R and B channels (RGB↔BGR or RGBA↔BGRA). Pass-through for 1- / 2-band.</summary>
    public static VipsImage SwapRb(VipsImage input)
    {
        if (input == null) return null!;
        if (input.Bands == 3)
            return Run(new VipsPixelOperations {
                In = input, TargetBands = 3, Permutation = new[] { 2, 1, 0 },
            });
        if (input.Bands == 4)
            return Run(new VipsPixelOperations {
                In = input, TargetBands = 4, Permutation = new[] { 2, 1, 0, 3 },
            });
        return input;
    }

    /// <summary>Rotate RGBA → ARGB (move alpha from band 3 to band 0).</summary>
    public static VipsImage ToArgb(VipsImage input)
    {
        if (input == null || input.Bands != 4) return input!;
        return Run(new VipsPixelOperations {
            In = input, TargetBands = 4, Permutation = new[] { 3, 0, 1, 2 },
        });
    }

    // From Operations/Drawing/VipsBlend.cs
    /// <summary>
    /// Composite <paramref name="overlay"/> onto <paramref name="base"/> at
    /// (<paramref name="x"/>, <paramref name="y"/>) using the chosen blend
    /// <paramref name="mode"/> and <paramref name="opacity"/>. Mirrors
    /// ImageSharp's <c>DrawImage(source, location, opacity, blendMode)</c>.
    /// </summary>
    public static VipsImage DrawImage(VipsImage @base, VipsImage overlay, int x, int y,
        VipsBlendMode mode, double opacity = 1.0)
        => Run(new VipsBlend {
            Base = @base, Overlay = overlay, X = x, Y = y, Mode = mode, Opacity = opacity,
        });

    /// <summary>
    /// Composite <paramref name="overlay"/> onto <paramref name="base"/> at the
    /// origin with the chosen PorterDuff blend mode. Renamed from the
    /// over-blend `Composite` to disambiguate at the call site.
    /// </summary>
    public static VipsImage CompositeBlend(VipsImage @base, VipsImage overlay,
        VipsBlendMode mode, double opacity = 1.0)
        => Run(new VipsBlend {
            Base = @base, Overlay = overlay, X = 0, Y = 0, Mode = mode, Opacity = opacity,
        });

    // From Operations/Drawing/VipsFillPath.cs
    /// <summary>
    /// Fill a vector <paramref name="path"/> with <paramref name="brush"/>.
    /// Mirrors ImageSharp's <c>Fill(brush, path)</c>.
    /// </summary>
    public static VipsImage FillPath(VipsImage input, VipsPath path, IVipsBrush brush,
        bool aa = true, VipsRect? clipRect = null)
        => Run(new VipsFillPath {
            In = input, Path = path, Brush = brush, Antialiased = aa, ClipRect = clipRect,
        });

    /// <summary>Convenience: fill an axis-aligned rectangle.</summary>
    public static VipsImage Fill(VipsImage input, IVipsBrush brush,
        double x, double y, double w, double h, bool aa = true)
        => FillPath(input, VipsPath.Rectangle(x, y, w, h), brush, aa);

    /// <summary>
    /// Fill an arbitrary region with a solid colour. Mirrors
    /// ImageSharp's <c>Fill(color, region)</c> — equivalent to
    /// <see cref="FillPath"/> with a <see cref="VipsSolidBrush"/>.
    /// </summary>
    public static VipsImage Fill(VipsImage input, byte[] color, VipsPath region, bool aa = true)
        => FillPath(input, region, new VipsSolidBrush(color), aa);

    /// <summary>Convenience: fill a circle at (cx, cy) with the given radius.</summary>
    public static VipsImage FillCircle(VipsImage input, IVipsBrush brush,
        double cx, double cy, double radius, bool aa = true)
        => FillPath(input, VipsPath.Circle(cx, cy, radius), brush, aa);

    /// <summary>Convenience: fill a polygon described by a sequence of vertices.</summary>
    public static VipsImage FillPolygon(VipsImage input, IVipsBrush brush,
        params (double x, double y)[] points)
        => FillPath(input, VipsPath.Polygon(points), brush);

    /// <summary>
    /// Stroke a vector <paramref name="path"/> with <paramref name="pen"/>.
    /// Mirrors ImageSharp's <c>Draw(pen, path)</c>.
    /// </summary>
    public static VipsImage StrokePath(VipsImage input, VipsPath path, VipsPen pen,
        bool aa = true, VipsRect? clipRect = null)
        => VipsStrokePath.Stroke(input, path, pen, aa, clipRect);

    /// <summary>Stroke an axis-aligned rectangle outline.</summary>
    public static VipsImage StrokeRectangle(VipsImage input, VipsPen pen,
        double x, double y, double w, double h, bool aa = true)
        => StrokePath(input, VipsPath.Rectangle(x, y, w, h), pen, aa);

    /// <summary>Stroke a circle outline.</summary>
    public static VipsImage StrokeCircle(VipsImage input, VipsPen pen,
        double cx, double cy, double radius, bool aa = true)
        => StrokePath(input, VipsPath.Circle(cx, cy, radius), pen, aa);

    /// <summary>Stroke a polygon outline.</summary>
    public static VipsImage StrokePolygon(VipsImage input, VipsPen pen,
        params (double x, double y)[] points)
        => StrokePath(input, VipsPath.Polygon(points), pen);

    /// <summary>Stroke a single line segment from (x1, y1) to (x2, y2).</summary>
    public static VipsImage StrokeLine(VipsImage input, VipsPen pen,
        double x1, double y1, double x2, double y2,
        bool aa = true, VipsRect? clipRect = null)
    {
        var path = new VipsPath().MoveTo(x1, y1).LineTo(x2, y2);
        return StrokePath(input, path, pen, aa, clipRect);
    }

    public static VipsImage EqualConst(VipsImage input, params double[] c) => RelationalConst(input, VipsRelationalOperation.Equal, c);
    public static VipsImage NotEqualConst(VipsImage input, params double[] c) => RelationalConst(input, VipsRelationalOperation.NotEqual, c);
    public static VipsImage LessConst(VipsImage input, params double[] c) => RelationalConst(input, VipsRelationalOperation.Less, c);
    public static VipsImage LessEqConst(VipsImage input, params double[] c) => RelationalConst(input, VipsRelationalOperation.LessEq, c);
    public static VipsImage MoreConst(VipsImage input, params double[] c) => RelationalConst(input, VipsRelationalOperation.More, c);
    public static VipsImage MoreEqConst(VipsImage input, params double[] c) => RelationalConst(input, VipsRelationalOperation.MoreEq, c);

    /// <summary>
    /// Block-scoped fluent wrapper. <c>image.Mutate(im => im.Resize(0.5).Sepia())</c>
    /// is equivalent to <c>image.Resize(0.5).Sepia()</c>. Pure ergonomics —
    /// the underlying pipeline is still immutable. ImageSharp users land softer.
    /// </summary>
    public static VipsImage Mutate(VipsImage input, Func<VipsImage, VipsImage> action)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));
        return action(input);
    }

    // From Operations/Misc/VipsQuantize.cs
    /// <summary>
    /// Reduce the image to at most <paramref name="colors"/> distinct colors,
    /// optionally with Floyd-Steinberg dithering. Output stays the same band
    /// format. For palette-PNG export, see <see cref="SavePngAsync"/>'s
    /// <c>palette</c> parameter, which combines quantization + indexed write
    /// in one step.
    /// </summary>
    public static VipsImage Quantize(VipsImage input, int colors = 256, bool dither = true)
        => Run(new VipsQuantize { In = input, Colors = colors, Dither = dither });

    /// <summary>
    /// Quantize via a pluggable <see cref="IVipsQuantizer"/>. Mirrors
    /// ImageSharp's <c>Quantize(IQuantizer)</c>. Use this overload to
    /// inject a custom quantization algorithm; the simpler
    /// <see cref="Quantize(VipsImage, int, bool)"/> overload routes
    /// through <see cref="VipsOctreeQuantizer"/> (pure managed).
    /// </summary>
    public static VipsImage Quantize(VipsImage input, IVipsQuantizer quantizer)
    {
        if (quantizer == null) throw new ArgumentNullException(nameof(quantizer));
        return quantizer.Apply(input);
    }

    // ===== Savers =====
    // From Savers/VipsApngSaver.cs
    /// <summary>
    /// Save as APNG (animated PNG). Multi-frame is triggered by the same
    /// metadata convention as GIF/WebP: <c>n-pages</c> + <c>page-height</c>
    /// (+ optional <c>animation-delays</c>). Single-frame inputs produce a
    /// regular PNG inside an APNG container.
    /// </summary>
    public static Task SaveApngAsync(VipsImage image, PipeWriter writer)
        => VipsApngSaver.SaveAsync(image, writer);

    // From Savers/VipsHeifSaver.cs
    public static Task SaveHeifAsync(VipsImage image, PipeWriter writer, int quality = 75, bool lossless = false)
        => VipsHeifSaver.SaveHeifAsync(image, writer, quality, lossless);

    public static Task SaveAvifAsync(VipsImage image, PipeWriter writer, int quality = 75, bool lossless = false)
        => VipsHeifSaver.SaveAvifAsync(image, writer, quality, lossless);

    // From Savers/VipsGifSaver.cs
    public static Task SaveGifAsync(VipsImage image, PipeWriter writer)
        => VipsGifSaver.SaveAsync(image, writer);

    // From Savers/VipsTgaSaver.cs
    public static Task SaveTgaAsync(VipsImage image, PipeWriter writer)
        => VipsTgaSaver.SaveAsync(image, writer);

    // From Savers/VipsQoiSaver.cs
    public static Task SaveQoiAsync(VipsImage image, PipeWriter writer)
        => VipsQoiSaver.SaveAsync(image, writer);

    // From Savers/VipsPnmSaver.cs
    /// <summary>
    /// Save as Netpbm. <paramref name="variant"/> = Auto picks PBM/PGM/PPM/PAM
    /// from band count; PAM is used for alpha-bearing inputs so the alpha
    /// channel survives the round-trip.
    /// </summary>
    public static Task SavePnmAsync(VipsImage image, PipeWriter writer, VipsPnmVariant variant = VipsPnmVariant.Auto)
        => VipsPnmSaver.SaveAsync(image, writer, variant);

    // From Savers/VipsDzSaver.cs
    /// <summary>
    /// Save as a Deep Zoom Image (DZI) pyramid. Output is a directory tree
    /// rooted at <paramref name="basePath"/> — <c>{basePath}.dzi</c> for the
    /// XML descriptor and <c>{basePath}_files/</c> for per-level tile
    /// subdirectories. Compatible with OpenSeadragon and other viewers
    /// implementing the Microsoft DZI 2008 schema.
    /// </summary>
    public static Task SaveDeepZoomAsync(
        VipsImage image,
        string basePath,
        int tileSize = 256,
        int overlap = 1,
        VipsDzTileFormat format = VipsDzTileFormat.Jpeg,
        int jpegQuality = 85)
        => VipsDzSaver.SaveAsync(image, basePath, tileSize, overlap, format, jpegQuality);

    // From Savers/VipsHdrSaver.cs
    /// <summary>
    /// Save as Radiance HDR (<c>.hdr</c>). 3-band Float input recommended;
    /// UChar input is auto-cast to Float. RLE-compressed scanlines.
    /// </summary>
    public static Task SaveHdrAsync(VipsImage image, PipeWriter writer)
        => VipsHdrSaver.SaveAsync(image, writer);

    // From Savers/VipsFitsSaver.cs
    /// <summary>
    /// Save as FITS (Flexible Image Transport System). UChar → BITPIX 8;
    /// Float and other formats → BITPIX -32. Multi-band input writes a
    /// planar layout (NAXIS = 3, NAXIS3 = bands). Cards from
    /// <c>Metadata["fits:*"]</c> round-trip into the header.
    /// </summary>
    public static Task SaveFitsAsync(VipsImage image, PipeWriter writer)
        => VipsFitsSaver.SaveAsync(image, writer);

    // From Savers/VipsBmpSaver.cs
    /// <summary>
    /// Save as BMP. 24bpp BGR (3-band input) or 32bpp BGRA (4-band input);
    /// 1-band grayscale is replicated to 24bpp. BITMAPINFOHEADER, BI_RGB,
    /// bottom-up rows — the layout every BMP reader handles.
    /// </summary>
    public static Task SaveBmpAsync(VipsImage image, PipeWriter writer)
        => VipsBmpSaver.SaveAsync(image, writer);

    // From Savers/VipsNiftiSaver.cs
    /// <summary>
    /// Save as single-file NIfTI-1 (<c>.nii</c>). UChar → datatype 2 (uint8);
    /// Float → datatype 16 (float32). 1-band saves as 2D, multi-band as 3D
    /// with planes mapped to the third dimension.
    /// </summary>
    public static Task SaveNiftiAsync(VipsImage image, PipeWriter writer)
        => VipsNiftiSaver.SaveAsync(image, writer);
}
