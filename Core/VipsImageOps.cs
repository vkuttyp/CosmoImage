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

        return Run(new VipsComposite { Base = baseImage, Overlay = overlay, X = ix, Y = iy });
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
}
