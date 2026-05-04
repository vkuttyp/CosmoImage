# ImageSharp parity (CosmoImage vs SixLabors.ImageSharp)

The README frames CosmoImage as "the architecture of libvips and the
**surface area of ImageSharp**". `PARITY_MATRIX.md` covers the libvips
side (the architecture parent). This document is the honest accounting
against the surface-area parent — SixLabors.ImageSharp.

CosmoImage and ImageSharp differ at the most fundamental level: ImageSharp
is **eager and strongly-typed** (every pixel format is a `struct`,
every op signature is generic in `TPixel`); CosmoImage is **lazy and
demand-driven** (typed access bolted on as a separate layer via
`TypedImage<TPixel>`). Some gaps below follow from that architectural
choice and won't ever close — others are just work we haven't done.

Status legend: ✅ full · 🟢 production-ready · 🟡 partial · ❌ missing.

---

## Architecture

| Capability | ImageSharp | CosmoImage |
| :--- | :--- | :--- |
| Pipeline model | Eager, per-op `Parallel.For` | Lazy demand-driven, sink-driven threadpool (libvips-style) |
| Strong pixel typing | `Image<TPixel>` generic everywhere | `TypedImage<TPixel>` access layer; ops still operate on untyped `VipsImage` |
| Op-chain idiom | `image.Mutate(ctx => ctx.Resize(...).Sepia())` block | `image.Mutate(im => im.Resize(...).Sepia())` block (mirrors ImageSharp); also fluent `image.Resize(...).Sepia()` |
| `MemoryAllocator` | Pluggable everywhere; `ArrayPoolMemoryAllocator` default | `IVipsAllocator` plumbed through transient buffers (`VipsRegion`, `OrderedStripSink`); long-lived buffers (`PixelsLazy`, `MemorySink.Pixels`) bypass the pool |
| `Configuration` registry | Global + per-image `Configuration` — registers decoders/encoders/allocator | ❌ no equivalent — decoders/encoders are static classes |
| Auto-format detect (`Image.IdentifyAsync`) | Single entry point detects format + reads header | ✅ `VipsImageOps.IdentifyAsync(stream)` returns `{Format, Header}`; sniffs across 19 formats (round 54) |
| Streaming load | Native — every decoder consumes `Stream` incrementally | 🟡 opt-in `LoadStreamingAsync` on every Stream-capable format; PNG/PDF still byte-buffer |
| Async API | Every entry point has `Async` variant | ✅ all loaders / savers / sinks are async |
| Cross-platform | Pure managed, no native deps | 🟡 Magick.NET-Q8 still required for several formats (WebP/HEIF/AVIF/TIFF/SVG/GIF) |

---

## Pixel formats

ImageSharp ships ~25 pixel structs covering the matrix of:
{8/16/Float per-channel} × {1/2/3/4 channels} × {RGB/BGR/ARGB ordering}
× {packed-bit variants}.

| ImageSharp pixel | Bands | Format | Status |
| :--- | :---: | :---: | :---: |
| `A8` | 1 | 8-bit alpha | ✅ `A8` (round 58) |
| `L8` | 1 | 8-bit grayscale | ✅ `L8` |
| `L16` | 1 | 16-bit grayscale | ✅ `L16` (round 57) |
| `La16` | 2 | 8-bit grayscale + alpha | ✅ `La16` |
| `La32` | 2 | 16-bit grayscale + alpha | ✅ `La32` (round 57) |
| `Rgb24` | 3 | 8-bit RGB | ✅ `Rgb24` |
| `Bgr24` | 3 | 8-bit BGR | ✅ `Bgr24` (round 57) |
| `Rgb48` | 3 | 16-bit RGB | ✅ `Rgb48` (round 57) |
| `Rgba32` | 4 | 8-bit RGBA | ✅ `Rgba32` |
| `Bgra32` | 4 | 8-bit BGRA | ✅ `Bgra32` (round 57) |
| `Argb32` | 4 | 8-bit ARGB | ✅ `Argb32` (round 57) |
| `Rgba64` | 4 | 16-bit RGBA | ✅ `Rgba64` (round 57) |
| `Bgr565` | 1 packed | 16-bit packed RGB (5/6/5) | ✅ `Bgr565` (round 58) — bit-replication R/G/B accessors |
| `Bgra4444` | 1 packed | 16-bit packed ARGB (4/4/4/4) | ✅ `Bgra4444` (round 58) |
| `Bgra5551` | 1 packed | 16-bit packed ARGB (5/5/5/1) | ✅ `Bgra5551` (round 58) — binary alpha (≥128 = opaque) |
| `Rgba1010102` | 1 packed | 32-bit packed (10/10/10/2) | ✅ `Rgba1010102` (round 59) — UInt-stored, 10-bit RGB / 2-bit A bit-replication accessors |
| `Rg32` | 2 | 16-bit per channel (R, G) | ✅ `Rg32` (round 58) |
| `HalfSingle` | 1 | 16-bit float | ✅ `HalfSingle` (round 60) — uses `System.Half` storage |
| `HalfVector2` | 2 | 16-bit float ×2 | ✅ `HalfVector2` (round 60) |
| `HalfVector4` | 4 | 16-bit float ×4 | ✅ `HalfVector4` (round 60) |
| `RgbaVector` | 4 | 32-bit float per channel | 🟡 covered functionally by `BandFormat=Float` + 4 bands, but no typed struct |
| `Byte4`, `Short2`, `Short4`, `NormalizedByte2/4`, `NormalizedShort2/4` | 2/4 | various integer | ✅ all 7 added round 59. `Byte4` = 4-band UChar tuple, `Short2/4` = 2/4-band Short, `NormalizedByte*` reinterpret raw byte as `sbyte` for `[-1, 1]` access, `NormalizedShort*` use Short with /32767 normalisation |
| `PixelOperations<TPixel>` (bulk format conversion) | — | — | 🟡 named conversions: `ToL8` / `ToLa16` / `ToRgb24` / `ToRgba32` / `SwapRb` / `ToArgb` (round 55). Generic `From<TFromPixel>` still missing — needs the typed-pixel surface to mature first |

CosmoImage gap: **0 of ~25 pixel formats missing.** Round 60 closed
out the zoo by adding `BandFormat.Half = 10` (2 bytes per band,
threaded through `VipsEnumsExtensions.SizeOf`) and the three
`Half`-precision IPixel structs backed by `System.Half`.

---

## Codecs

| Format | ImageSharp | CosmoImage |
| :--- | :--- | :--- |
| **JPEG** | Pure managed; full + EXIF/ICC/XMP, baseline + progressive, arithmetic | ✅ pure-C# decoder via JpegLibrary; full metadata round-trip |
| **PNG** | Pure managed; full + APNG (animated), interlace | 🟡 via StbImageSharp (byte[] only); we ship APNG saver but not animated PNG read |
| **BMP** | Pure managed; full | 🟡 pure-C# fast path (24/32 bpp BI_RGB); paletted/RLE via Magick |
| **TGA** | Pure managed | 🟡 pure-C# fast path (types 2/3/10/11) |
| **WebP** | Pure managed; full lossy + lossless + animated | 🟡 via Magick.NET (animated load works) |
| **TIFF** | Pure managed; LZW / Deflate / PackBits / JPEG-in-TIFF, multi-page | 🟡 via Magick; multi-page + Ptif pyramid + OME-XML metadata |
| **GIF** | Pure managed; animated, LZW | 🟡 via Magick |
| **PBM/PGM/PPM** | Pure managed (P1-P6) | ✅ pure-C# (P1-P6); PAM via Magick |
| **QOI** | Pure managed; full QOI v1.0 | ✅ pure-C# (full QOI v1.0) |
| **HEIF / AVIF** | ❌ (paid 3rd-party `Microsoft.Maui.Graphics.HeifSharp` or similar; not in core ImageSharp) | ✅ via Magick.NET — we have *advantage* here including animated AVIF/HEIC sequence load |
| **PDF render** | ❌ | ✅ via Docnet — multi-page rendering |
| **SVG raster** | ❌ (would need separate `SixLabors.ImageSharp.Drawing`) | ✅ via Magick |
| **Radiance HDR** | ❌ | ✅ pure-C# |
| **FITS** | ❌ | ✅ pure-C# |
| **NIfTI-1** | ❌ | ✅ pure-C# (single-file + paired) |
| **Matlab `.mat`** | ❌ | ✅ pure-C# (v5 numeric arrays) |
| **CSV / Matrix** | ❌ | ✅ pure-C# |
| **JPEG XL** | ❌ | ❌ |
| **JPEG 2000** | ❌ | ❌ |
| **OpenEXR** | ❌ | ❌ |
| **OpenSlide / DICOM / dcraw** | ❌ | ❌ |
| **Output: pyramidal TIFF (Ptif)** | ❌ | ✅ |
| **Output: dzsave (Deep Zoom DZI)** | ❌ | ✅ |

CosmoImage covers the same modern web-format set ImageSharp does (with
Magick.NET as the implementation in some cases vs ImageSharp's pure
managed). We *exceed* ImageSharp on HEIF/AVIF, PDF, SVG, and the
scientific-format set; ImageSharp is purer-managed for the formats it
supports.

---

## Processing extensions

The bread-and-butter `IImageProcessingContext` op surface. ImageSharp
has ~50 named processors; we have ~40 ops. Coverage is patchy — some
of theirs we don't have, a few of ours they don't.

### Color / Adjustment

| ImageSharp op | CosmoImage |
| :--- | :--- |
| `Brightness(amount)` | ✅ `Brightness` |
| `Contrast(amount)` | ✅ `Contrast` |
| `Saturate(amount)` | ✅ `Saturate` |
| `Hue(degrees)` | ✅ `Hue` |
| `Lightness(amount)` | ✅ `Lightness` (HSL L-axis) |
| `Invert()` | ✅ |
| `Grayscale()` | ✅ `Greyscale` (alias `Grayscale`) |
| `Sepia(amount)` | ✅ |
| `Kodachrome()` | ✅ stylised film-stock matrix via `Recomb` |
| `Lomograph()` | ✅ saturated cross-process matrix via `Recomb` |
| `Polaroid(amount)` | ✅ via Magick.NET wrapper |
| `BlackWhite()` | ✅ named `BlackWhite()` shortcut over `Saturate(0)` |
| `Filter(ColorMatrix)` (4×4 matrix incl. alpha mix) | ✅ `ColorMatrix(double[4,5])` — 4 mix rows + translation column, RGBA UChar+Float branches |
| `Opacity(amount)` | ✅ multiplies alpha by amount (0..1); pass-through for non-alpha images |
| `ColorBlindness(mode)` (Deuteranopia / Protanopia / Tritanopia / etc.) | ✅ all 8 Brettel-Vienot-Mollon (1997) matrices |

### Effects

| ImageSharp op | CosmoImage |
| :--- | :--- |
| `Pixelate(size)` | ✅ |
| `Vignette(...)` | ✅ |
| `Glow(...)` | ✅ |
| `OilPaint(levels, brushSize)` | ✅ via Magick.NET |
| `BokehBlur(radius, components, gamma)` | 🟡 we have `BokehBlur(radius)` (hexagonal); ImageSharp's parametric multi-component is richer |

### Geometric / Transform

| ImageSharp op | CosmoImage |
| :--- | :--- |
| `Resize(size, sampler, options)` with Pad/Crop/BoxPad/Max/Min/Stretch modes + anchor | 🟡 `Resize(scale)` + `Thumbnail(w, h, crop)`; full mode/anchor matrix not exposed |
| `Resize(size, sampler)` | ✅ — we have 10 kernels (Nearest, Linear, Cubic, Mitchell, Lanczos2/3/5, Hermite, BicubicSharper/Smoother) |
| `Rotate(degrees, sampler)` | ✅ `Rotate` |
| `Skew(degreesX, degreesY)` | ✅ named `Skew(dx, dy)` over `Affine` |
| `Crop(rect)` | ✅ `Crop` / `ExtractArea` |
| `EntropyCrop(threshold)` | ✅ |
| `Pad(width, height, color)` | ✅ `Pad(width, height, background, position)` with VipsCompass anchor |
| `BackgroundColor(color)` | ✅ `BackgroundColor(...)` flattens transparent pixels onto fill colour |
| `AutoOrient()` | ✅ |
| `Flip(FlipMode)` | ✅ |
| `Transform(matrix, sampler)` | 🟡 covered by `Affine` |
| `DetectEdges(filter)` (Sobel/Roberts/Prewitt/Kayyali/Kirsch/Laplacian variants) | 🟡 `Edge(method)` dispatcher: Sobel, Compass (= Kirsch), Canny, Roberts, Prewitt, Laplacian. Kayyali still missing |

### Convolution / Blur

| ImageSharp op | CosmoImage |
| :--- | :--- |
| `BoxBlur(radius)` | ✅ `BoxBlur(radius, passes)` running-sum (round 49) |
| `GaussianBlur(sigma)` | ✅ `GaussBlur` |
| `GaussianSharpen(sigma)` | 🟡 covered by `UnsharpMask` |
| `DetectEdges(EdgeDetectorKernel)` (8+ kernels) | 🟡 same as above — Sobel/Compass/Canny/Roberts/Prewitt/Laplacian via the `Edge(method)` dispatcher |

### Quantization / Dithering

| ImageSharp op | CosmoImage |
| :--- | :--- |
| `Quantize(IQuantizer)` — Octree / Wu / Werner / Webby / Palette | 🟡 `Quantize(colors, dither)` via Magick (Wu/median-cut); no quantizer interface |
| `Dither(IDither, threshold)` — Floyd-Steinberg / Stevenson-Arce / Burkes / Bayer / Ordered | ✅ `Dither(method, levels)` — FS / Atkinson / Burkes / Stevenson-Arce / Sierra error-diffusion + Bayer4×4 / Bayer8×8 ordered |
| `BinaryThreshold(threshold)` | ✅ `Threshold(value)` from round 51 |
| `BinaryDither(...)` | ✅ `BinaryDither(method)` — alias for `Dither(method, levels=2)` |
| `BinaryInvert()` | ✅ alias for `Invert` |

### Histogram / Tone

| ImageSharp op | CosmoImage |
| :--- | :--- |
| `HistogramEqualization(LuminanceLevels)` | ✅ `HistEqual` |
| `AdaptiveHistogramEqualization(...)` (CLAHE) | ✅ `AdaptiveHistogramEqualization(tileGridSize, clipLimit)` — alias for libvips-named `HistLocal` |
| `Threshold(amount)` | ✅ `Threshold(value)` per-band binary; UChar + Float |
| `Gamma(gamma)` | ✅ `Gamma` |

### Compositing / Drawing-on-image

| ImageSharp op | CosmoImage |
| :--- | :--- |
| `DrawImage(source, location, opacity, blendMode)` (full PorterDuff: Normal, Multiply, Add, Subtract, Screen, Darken, Lighten, Overlay, HardLight, …) | ✅ `DrawImage(base, overlay, x, y, mode, opacity)` and `CompositeBlend(base, overlay, mode, opacity)` — 13 modes (Normal/Multiply/Screen/Overlay/Darken/Lighten/HardLight/SoftLight/Difference/Exclusion/Add/Subtract/ColorDodge) |
| `Fill(color, region)` | 🟡 covered by `DrawRect(... fill: true)` for rect; no general region fill |
| `Clear(color)` | ✅ `Clear(input, color…)` fills the entire canvas |

CosmoImage extras not in ImageSharp:
- `Charcoal`, `Sketch` artistic effects
- `BokehBlur` (we have hexagonal aperture; ImageSharp has a more general parametric form)

---

## Drawing & vector graphics (`SixLabors.ImageSharp.Drawing`)

ImageSharp's separate Drawing package is a full 2D vector pipeline.
This is a **major** gap area for CosmoImage — we have only line / rect /
text. ImageSharp has all of:

| Capability | ImageSharp | CosmoImage |
| :--- | :--- | :--- |
| Path construction (move-to / line-to / cubic / quadratic Bezier / arc / close) | ✅ `IPathBuilder`, `Path`, `PathBuilder` | ✅ `VipsPath` builder (round 61) — `MoveTo` / `LineTo` / `CubicTo` / `QuadraticTo` / `Close` + `ArcTo` (round 69, SVG-style elliptical arc converted to cubics at construction). Curves flatten via recursive subdivision (0.25-px tolerance) at fill time |
| Polygon / Ellipse / Circle / Rectangle / Star / RegularPolygon as path objects | ✅ | ✅ `VipsPath.Rectangle` / `Polygon` / `Ellipse` / `Circle` / `RegularPolygon` / `Star` factory methods (round 61) |
| Line rendering (Xiaolin Wu / Bresenham / sub-pixel) | ✅ via path-based renderer | ✅ `StrokeLine(pen, x1, y1, x2, y2)` (round 62) on top of path-based renderer; legacy `DrawLine` (Xiaolin Wu) still available |
| Rectangle (fill + outline) | ✅ as Path | ✅ `Fill(brush, x, y, w, h)` and `StrokeRectangle(pen, x, y, w, h)` (rounds 61–62) |
| Circle / Ellipse | ✅ | ✅ `FillCircle` / `StrokeCircle` (rounds 61–62); ellipse via `VipsPath.Ellipse` factory |
| Polygon / Polyline | ✅ | ✅ `FillPolygon` / `StrokePolygon` (rounds 61–62) |
| Arc / Bezier curves | ✅ | ✅ cubic + quadratic Bezier in `VipsPath` (round 61) + SVG-style elliptical arc via `ArcTo(rx, ry, xRot, largeArc, sweep, x, y)` (round 69) |
| `SolidPen`, dashed pens, `Pen` width, line joins (miter / round / bevel), end caps | ✅ | ✅ `VipsPen` solid + width + all 3 joins (bevel / miter / round) + all 3 caps (butt / square / round) + miter limit + dashed pens with arc-length cycle and `DashOffset` phase (rounds 62, 64, 65) |
| Brushes: `SolidBrush`, `LinearGradientBrush`, `RadialGradientBrush`, `PathGradientBrush`, `ImageBrush`, `PatternBrush` | ✅ | ✅ `VipsSolidBrush` / `VipsLinearGradientBrush` / `VipsRadialGradientBrush` (round 61) + `VipsImageBrush` / `VipsPatternBrush` (round 67) + `VipsPathGradientBrush` (round 70 — N-vertex polygon gradient via centroid fan-triangulation + barycentric blend; optional explicit centre colour, defaults to per-band average of vertex colours). All 6 ImageSharp brushes covered |
| Clipping regions (intersect / union / difference) | ✅ | ✅ rectangular `clipRect` parameter on FillPath / StrokePath / StrokeLine etc. (round 66) AND full path-vs-path booleans via `VipsPath.Intersect` / `Union` / `Subtract` (round 68 — Greiner-Hormann polygon clipping; curves flattened first; non-degenerate inputs only) |
| Affine path transforms | ✅ | ✅ `VipsPath.Transform(a, b, c, d, tx, ty)` + `Translate` / `Scale` / `Rotate` / `RotateAround` (round 66). Returns a new path; transforms endpoints AND Bezier control points |
| Tessellation (path → triangles) | ✅ | 🟡 `VipsPath.Tessellate()` (round 76) — ear-clipping triangulation, returns flat `(x, y)` triplets ready for GPU vertex buffers. Bezier curves flatten first; each closed sub-path tessellates independently (no automatic hole subtraction — run `Subtract` first for shapes like glyph 'O') |
| Path operations: outline expansion, offset, simplify | ✅ | 🟡 `VipsPath.Outline(width, cap, join, miterLimit)` (round 71 — exposes the stroke outline as a fillable path) + `VipsPath.Simplify(tolerance)` (round 71 — Douglas-Peucker; flattens curves first, returns polyline path). Independent path-offset (offset by N without producing a closed band) not yet exposed |
| Text rendering with full glyph shaping (via `SixLabors.Fonts`) | ✅ HarfBuzz-equivalent shaping, ligatures, kerning, RTL/LTR/BiDi | ✅ rounds 72–77 — `VipsTextOps.DrawText` / `TextToPath` backed by `SixLabors.Fonts` (kerning, ligatures, full OpenType shaping). Multi-line layout (`\n`, `WrappingLength`, `LineSpacing`, `HAlign`, `WordBreak`, `Justify`); decorations (Underline / Strikeout / Overline as flags); writing mode (`LayoutMode` Horizontal* / Vertical*); reading direction (`TextDirection` LeftToRight / RightToLeft / Auto with Unicode BiDi resolution); opt-in `OpenTypeFeatures` (4-char tags — `smcp`, `ss01`, `frac`, `dlig`, etc.). Legacy `VipsImageOps.Text` (Magick.NET label render) still available for one-shot label generation |
| Text on path | ✅ | ✅ `VipsTextOps.TextOnPath(opts, targetPath, offset)` (round 74) — shapes text at origin, flattens glyph outlines to polylines, then warps each point onto the target via arc-length parameterisation. Tangent-aware so glyphs rotate to follow path direction. `offset` shifts perpendicular (positive = below path). Single sub-path target only |
| Text wrapping / measuring | ✅ | ❌ |

Closing this gap means importing or porting an entire 2D vector
pipeline. **It's by far the largest CosmoImage gap vs ImageSharp** and
is probably permanent — drawing is its own discipline (Skia / Cairo /
ImageSharp.Drawing), not a feature we'd want to bolt on.

---

## Color spaces

ImageSharp has a colour-conversion graph rivalling libvips, with
typed structs for each space:

| ImageSharp colour space | Status |
| :--- | :---: |
| `Color`, `Rgb`, `LinearRgb` | 🟡 — we do sRGB↔linear via `Linearize`/`Delinearize` |
| `Hsl`, `Hsv` | 🟡 — internal use only inside `Lightness` |
| `CieLab`, `CieLch`, `CieLchuv`, `CieLuv` | ❌ |
| `CieXyz`, `CieXyy` | ❌ |
| `Cmyk` | ❌ |
| `HunterLab` | ❌ |
| `LmsBradford`, `LmsCAT02`, `LmsCAT97s` | ❌ |
| `YCbCr` | ❌ — internal in JPEG decode only |
| Chromatic adaptation | ❌ |
| White-point (D65, D50, etc.) | ❌ |
| `ColorConverter.Convert<TFrom, TTo>(...)` | ❌ |
| `ColorMatrix` (4×4 with alpha channel) | 🟡 we have `Recomb` (3×3 RGB) |

Same gap as the libvips colour matrix. Both libvips and ImageSharp
treat the colourspace graph as a first-class citizen; CosmoImage
doesn't yet.

---

## Metadata

| Capability | ImageSharp | CosmoImage |
| :--- | :--- | :--- |
| Raw EXIF/XMP/ICC byte-blob round-trip | ✅ | ✅ |
| Typed EXIF tag access (`ExifProfile.GetValue<T>(ExifTag)`) | ✅ — full tag dictionary | ❌ — raw bytes only |
| EXIF profile editing | ✅ | ❌ |
| IPTC profile (read + write) | ✅ | ❌ |
| ICC profile structure parsing | ✅ — `IccProfile` with header + tag table | ❌ — raw bytes only |
| ICC profile applied at sink (proper CMM) | 🟡 — uses RGB-matrix approximation | 🟡 — uses Magick.NET as one-shot |
| XMP DOM | ✅ via `SixLabors.Fonts` extension | ❌ — raw bytes only |
| Format-specific metadata (PNG text chunks, JPEG comments, GIF comments) | ✅ structured | 🟡 raw key/value via `Metadata` dict + `MetadataBlobs` |
| `VipsFields` typed accessors | n/a (their typed-profile API serves the same purpose) | ✅ `GetInt/Double/DoubleArray/Blob` + well-known shortcuts |
| FITS / NIfTI header round-trip | ❌ | ✅ via `Metadata["fits:*"]` / `["nifti:*"]` |
| OME-TIFF parsing | ❌ | ✅ `VipsOmeTiff` typed accessors |

CosmoImage carries the metadata bytes losslessly across format
boundaries (a JPEG → AVIF round-trip preserves all three blob types)
but doesn't parse them into typed objects the way ImageSharp does.
For a typical web-image workflow (preserve EXIF, strip on resize,
inject XMP) the byte-blob model is enough; for editing-style metadata
(rotate the EXIF orientation, set a DateTime tag, etc.) the typed
profile API ImageSharp ships is a meaningful gap.

---

## Streaming, async, MemoryAllocator

| Capability | ImageSharp | CosmoImage |
| :--- | :--- | :--- |
| Stream-based load (no full-buffer hop) | ✅ — every decoder | 🟡 `LoadStreamingAsync` opt-in on every Stream-capable format; PNG/PDF still byte-buffer (decoder limit) |
| Stream-based save | ✅ | ✅ — every saver is `PipeWriter`-based |
| `MemoryAllocator` configurable | ✅ — every buffer goes through it | 🟡 `IVipsAllocator` plumbed through transient buffers (`VipsRegion`, `OrderedStripSink`); long-lived buffers (`PixelsLazy`, `MemorySink.Pixels`) bypass |
| `ArrayPool` integration | ✅ — `ArrayPoolMemoryAllocator` default | ✅ — `ArrayPoolAllocator.Shared` default |
| Per-image allocator override | ✅ via `Configuration` | ✅ via `VipsImage.Allocator` |
| Pool-rented decoded pixel buffers | ✅ | ❌ — decoded buffers are `new byte[]` |
| `Image.IdentifyAsync` (header-only without decode) | ✅ — single entry point | ✅ `IdentifyAsync(stream)` (round 54) — sniffs format, returns header where the loader supports it |

---

## Format-detect / IO surface

| Capability | ImageSharp | CosmoImage |
| :--- | :--- | :--- |
| Auto-detect format from magic bytes | ✅ `Image.DetectFormatAsync` | ✅ `IdentifyAsync` returns `Format` enum across all 19 formats (round 54) |
| Magic-byte registry | ✅ via `Configuration` | ❌ — implicit, format-by-format |
| Custom format plugin registration | ✅ — register decoder/encoder pair via `Configuration` | ❌ — would need to add a static loader + saver pair |

**Closed (round 54)**: `VipsImageOps.IdentifyAsync(stream)` and
`VipsImageOps.LoadAsync(stream)` sniff every known format's magic
bytes and dispatch automatically. Sniff order favours distinctive
formats (PNG / JPEG / WebP / HEIF first) so magic-less formats (TGA,
PNM) only match as last resort. JXL / JP2K return `Format` correctly
but `LoadAsync` throws `NotSupportedException` since we ship only
header-only readers for them.

---

## Things CosmoImage has that ImageSharp doesn't

Worth itemising — not all the gap goes one way.

- **Lazy demand-driven pipeline.** ImageSharp materializes per-op;
  we don't until the sink consumes. Means a chain like
  `Linearize → Resize → Composite → Delinearize → SaveJpeg` runs
  through one threadpool sweep producing the JPEG bytes, never
  allocating an intermediate full-image buffer.
- **Sink-driven multi-stage parallelism.** One threadpool drains the
  whole chain; ImageSharp parallelizes per-op.
- **Full Float-throughout.** Mainline pipeline (Linearize → Resize →
  Composite → Glow → Vignette → Delinearize) runs end-to-end in
  Float. ImageSharp's processors are TPixel-typed but most operate
  on the source pixel format directly (UChar in / UChar out for
  `Rgba32`); high-precision linear-light blending requires a manual
  cast to `RgbaVector`.
- **HEIF / AVIF native (incl. animated sequences).** Via Magick.NET.
  ImageSharp doesn't ship HEIF support in core.
- **Multi-page PDF render.** Via Docnet. ImageSharp doesn't render PDF.
- **Scientific format support.** HDR, FITS, NIfTI (single + paired),
  Matlab `.mat` v5, CSV / Matrix loaders. ImageSharp doesn't
  ship any of these.
- **dzsave Deep Zoom output.** Tile-pyramid + DZI XML for
  OpenSeadragon. ImageSharp doesn't have this.
- **OME-TIFF metadata** with typed `VipsOmeTiff` accessors for
  microscopy / pathology workflows.
- **Permissive licensing only** — CosmoImage was specifically built
  to escape the SixLabors split-license. ImageSharp is dual-licensed
  (Apache 2.0 for non-commercial, paid for commercial use over
  certain revenue thresholds since 3.0).

---

## Summary

Coarse-grained CosmoImage coverage of ImageSharp's surface:

| Layer | Coverage |
| :--- | :--- |
| Core architecture (lazy vs eager — different by design) | n/a — different model |
| Pixel formats (struct types) | ✅ 25 of ~25 (round 60 added the `Half` family — `BandFormat.Half = 10` enum value plus `HalfSingle` / `HalfVector2` / `HalfVector4` IPixel structs) |
| Codecs (modern web formats) | 🟢 most covered, often via Magick |
| Codecs (scientific / niche) | 🟢 we exceed ImageSharp here |
| Processing extensions (color/effects/geometric/etc.) | 🟡 ~40 of ~50 ops, many via Magick |
| Drawing & vector graphics | ✅ rounds 61–70 shipped path builder (move / line / cubic / quadratic / arc / close) + shape factories + all 6 brushes (solid / linear / radial / path / image / pattern) + FillPath + StrokePath + AA + complete VipsPen (caps / joins / miter limit / dashes) + affine path transforms + rectangular clipping + path-vs-path booleans (intersect / union / subtract via Greiner-Hormann) |
| Color spaces | 🟡 only sRGB↔linear + RGB-matrix ops |
| Metadata typed access | ❌ raw bytes only |
| `MemoryAllocator` integration | 🟡 transient buffers only |
| Streaming load | 🟢 opt-in on every Stream-capable format |
| Image.IdentifyAsync (single entry point) | ✅ `IdentifyAsync(stream)` + `LoadAsync(stream)` (round 54) |
| Configuration registry | ❌ |

The headline conclusion: CosmoImage covers the **mainline web-image
pipeline** (load, transform, save with proper colour-managed Float
intermediates) at parity-or-better with ImageSharp, including formats
ImageSharp doesn't ship at all. We're behind on the **typed-pixel
ecosystem** (~21 missing pixel structs, no generic op surface) and
the **vector-graphics drawing layer** (entirely missing). The drawing
gap is probably permanent — that's its own discipline; the typed-pixel
gap is closable but follows the broader `Image<TPixel>` generic-op-
surface direction tracked in `TODO_PARITY.md`.

---

*Last updated: 2026-05-02. Compares CosmoImage's current state at
233 passing tests against the ImageSharp 3.x API surface.*
