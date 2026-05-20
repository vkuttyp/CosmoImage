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

> **⚠️ Doc-vs-reality note (Magick.NET removal):** This matrix predates
> the production-side removal of `Magick.NET`. Any cell below that
> reads "via Magick", "Magick-backed", "Magick.NET-Q8", or claims
> Magick as a current dependency describes the **prior state**, not the
> current implementation. As of the removal:
>
> - **Pure-managed now:** SVG raster, WebP VP8L lossless encoder,
>   `IccTransform`, quantizers (`VipsOctreeQuantizer` /
>   `VipsPaletteQuantizer` / `VipsFloydSteinbergQuantizer`), artistic
>   effects (`OilPaint` / `Charcoal` / `Sketch` / `Polaroid`).
> - **Dropped:** HEIF / AVIF entirely (no pure-managed HEVC/AV1 codec
>   yet — was previously an ImageSharp *advantage*; now a parity gap),
>   WebP VP8 lossy.
>
> Individual line items below have **not** been retro-edited; they will
> be updated as the team revisits each subsystem. See `README.md`
> Dependencies section + `CONTRIBUTING.md` for the current policy.

---

## Architecture

| Capability | ImageSharp | CosmoImage |
| :--- | :--- | :--- |
| Pipeline model | Eager, per-op `Parallel.For` | Lazy demand-driven, sink-driven threadpool (libvips-style) |
| Strong pixel typing | `Image<TPixel>` generic everywhere | `TypedImage<TPixel>` access layer; ops still operate on untyped `VipsImage` |
| Op-chain idiom | `image.Mutate(ctx => ctx.Resize(...).Sepia())` block | `image.Mutate(im => im.Resize(...).Sepia())` block (mirrors ImageSharp); also fluent `image.Resize(...).Sepia()` |
| `MemoryAllocator` | Pluggable everywhere; `ArrayPoolMemoryAllocator` default | `IVipsAllocator` plumbed through transient buffers (`VipsRegion`, `OrderedStripSink`); long-lived buffers (`PixelsLazy`, `MemorySink.Pixels`) bypass the pool |
| `Configuration` registry | Global + per-image `Configuration` — registers decoders/encoders/allocator | ✅ `VipsConfiguration` (rounds 89-93) — global `Default` + per-instance constructor (with optional `seedBuiltIns` parameter); registry of decoders + encoders via `IVipsImageFormat`; `Allocator` property (round 93) for `IVipsAllocator` registration that flows into every loaded image's transient-buffer pool. `LoadAsync(stream, configuration)` overloads on `VipsIdentify` and `VipsImageOps` for scoped registrations |
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
| `RgbaVector` | 4 | 32-bit float per channel | ✅ `RgbaVector` typed struct (round 97) — `[StructLayout(Sequential, Pack=1)]`, 16-byte contiguous; reinterpretable from byte buffers via `MemoryMarshal.Cast`. Plus `RgbVector` (3-band), `LFloat` (1-band), `LaVector` (2-band) for the float-per-channel family |
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
| **JPEG** | Pure managed; full + EXIF/ICC/XMP, baseline + progressive, arithmetic | ✅ pure-C# decoder via JpegLibrary; full metadata round-trip; JFIF YCbCr / Adobe APP14 RGB / YCCK / CMYK colour-space conversion (round 138 — fixed a long-standing double-level-shift bug where the decoder was emitting raw YCbCr labelled as RGB) |
| **PNG** | Pure managed; full + APNG (animated), interlace | ✅ pure-managed `PurePngDecoder` — 8 / 16-bit color types 0/2/3/4/6 with `tRNS` expansion; Adam7 interlace; endianness-aware uint16 conversion. Pure-managed `PureApngDecoder` — animated PNG read with full `dispose_op` / `blend_op` composition AND IDAT-as-fallback layout (round 151). StbImageSharp dependency only for 1/2/4-bit depths |
| **BMP** | Pure managed; full | ✅ pure-C# decoder — 1/4/8 bpp paletted (BI_RGB), 16 bpp RGB555, 24 bpp BGR, 32 bpp BGRA, BI_RLE8, BI_RLE4 (round 134), BI_BITFIELDS (round 134); V4/V5 colour-space variants pass through |
| **TGA** | Pure managed | ✅ pure-C# decoder — full real-world variant matrix: types 1/9 (uncompressed/RLE paletted with 15/16/24/32-bit colour map), types 2/10 (uncompressed/RLE truecolor at depth 15/16/24/32), types 3/11 (uncompressed/RLE grayscale) (round 144) |
| **WebP** | Pure managed; full lossy + lossless + animated | 🟡 pure-managed VP8L lossless decoder — full bitstream, all four transforms (predictor / cross-color / subtract-green / color-indexing), color cache, LZ77, meta-Huffman. VP8X-wrapped VP8L also pure for files that carry ICC/EXIF/XMP metadata. VP8 lossy + animated still via Magick |
| **TIFF** | Pure managed; LZW / Deflate / PackBits / JPEG-in-TIFF, multi-page | ✅ pure-managed `PureTiffDecoder` — uncompressed + LZW + Deflate (zlib + raw deflate fallback, round 154) + PackBits + JPEG-in-TIFF (compression=7, round 139); predictor=2 (horizontal differencing) + predictor=3 (FP byte-shuffle); multi-page IFD chain; tiled layout; tiled+planar=2 combo (round 147); FillOrder=2 accepted; SampleFormat 1 (UChar/UShort/UInt), 2 (Char/Short/Int — round 152), 3 (Float); CMYK photometric; YCbCr photometric=6 for JPEG-in-TIFF; BigTIFF (8-byte offsets); plus OME-TIFF metadata + Ptif pyramid output |
| **GIF** | Pure managed; animated, LZW | 🟡 pure-managed `PureGifDecoder` — full GIF87a / GIF89a still decode with LZW (variable bit-width, dynamic dictionary, KwKwK case), Graphics Control Extension (delay / transparency / disposal NONE/BACKGROUND/PREVIOUS), interlaced frames, global + local colour tables. Animated frames go through Magick |
| **PBM/PGM/PPM/PAM** | Pure managed | ✅ pure-C# full Netpbm matrix (round 145) — PBM (P1/P4), PGM (P2/P5), PPM (P3/P6), PAM (P7) at 8 and 16 bits per sample. Drops the previous Magick fallback for PAM and 16-bit |
| **QOI** | Pure managed; full QOI v1.0 | ✅ pure-C# (full QOI v1.0) |
| **HEIF / AVIF** | ❌ (paid 3rd-party `Microsoft.Maui.Graphics.HeifSharp` or similar; not in core ImageSharp) | ✅ via Magick.NET — we have *advantage* here including animated AVIF/HEIC sequence load |
| **PDF render** | ❌ | ✅ via Docnet — multi-page rendering |
| **SVG raster** | ❌ (would need separate `SixLabors.ImageSharp.Drawing`) | ✅ via Magick |
| **Radiance HDR** | ❌ | ✅ pure-C# — new-style RLE + old-style RLE (round 146); all four Y-first axis orderings (round 153); X-first 90° rotations rejected |
| **FITS** | ❌ | ✅ pure-C# |
| **NIfTI-1** | ❌ | ✅ pure-C# (single-file + paired) |
| **Matlab `.mat`** | ❌ | ✅ pure-C# (v5 numeric arrays) |
| **CSV / Matrix** | ❌ | ✅ pure-C# |
| **JPEG XL** | ❌ | ❌ |
| **JPEG 2000** | ❌ | ❌ |
| **OpenEXR** | ❌ | 🟢 pure-managed `PureExrDecoder` — single-part scanline + tiled (ONE_LEVEL/MIPMAP/RIPMAP, level 0 exposed); multi-part (first-image-part); compressors NO_COMPRESSION / RLE / ZIPS / ZIP / PIZ / PXR24 / B44 / B44A / DWAA-DWAB-partial; HALF / FLOAT / UINT pixel types; arbitrary 1-4 channel sets (RGB[A] / Y / Z / U / V / arbitrary). DCT primitives + integration self-validated; full DWA-RGB-CSC + libimf-DC-encoding outstanding |
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
| `Resize(size, sampler, options)` with Pad/Crop/BoxPad/Max/Min/Stretch modes + anchor | ✅ `VipsImageOps.Resize(input, VipsResizeOptions)` (round 81) — full mode (Stretch/Crop/Pad/BoxPad/Max/Min) + 9-position anchor (`VipsCompass`) + per-band PadColor + Kernel choice. Plus existing `Resize(scale)` and `Thumbnail(w, h, crop)` |
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
| `DetectEdges(filter)` (Sobel/Roberts/Prewitt/Kayyali/Kirsch/Laplacian variants) | ✅ `Edge(method)` dispatcher: Sobel, Compass (= Kirsch), Canny, Roberts, Prewitt, Laplacian, Kayyali (round 95) |

### Convolution / Blur

| ImageSharp op | CosmoImage |
| :--- | :--- |
| `BoxBlur(radius)` | ✅ `BoxBlur(radius, passes)` running-sum (round 49) |
| `GaussianBlur(sigma)` | ✅ `GaussBlur` |
| `GaussianSharpen(sigma)` | ✅ `GaussianSharpen(sigma)` (round 95) — thin wrapper over `UnsharpMask(sigma, amount=1.0)` |
| `DetectEdges(EdgeDetectorKernel)` (8+ kernels) | 🟡 same as above — Sobel/Compass/Canny/Roberts/Prewitt/Laplacian via the `Edge(method)` dispatcher |

### Quantization / Dithering

| ImageSharp op | CosmoImage |
| :--- | :--- |
| `Quantize(IQuantizer)` — Octree / Wu / Werner / Webby / Palette | ✅ `Quantize(image, IVipsQuantizer)` pluggable interface (round 96) + four built-in implementations: `MagickQuantizer` (Wu / median-cut + Floyd-Steinberg via Magick.NET, round 96), `VipsOctreeQuantizer` (pure-managed Gervautz-Purgathofer, round 99), `VipsPaletteQuantizer` (nearest-neighbour mapping to a fixed palette, round 100; built-in `WebSafe` for the 216-colour 6×6×6 RGB cube), and `VipsFloydSteinbergQuantizer` (round 101 — decorator that adds error-diffused dithering on top of any inner quantizer; pure-managed). Werner / Webby palettes are user-supplied via `VipsPaletteQuantizer(custom)` |
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
| `Fill(color, region)` | ✅ `Fill(canvas, color, region)` (round 95) — region as a `VipsPath`; thin wrapper over `FillPath` with a `VipsSolidBrush`. Plus the existing rectangle convenience and `DrawRect(fill:true)` |
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
| Path operations: outline expansion, offset, simplify | ✅ | ✅ `VipsPath.Outline(width, cap, join, miterLimit)` (round 71 — closed stroke band) + `VipsPath.Simplify(tolerance)` (round 71 — Douglas-Peucker) + `VipsPath.Offset(distance)` (round 95 — single-side parallel offset with miter-bisector corners). Bezier curves flatten first |
| Text rendering with full glyph shaping (via `SixLabors.Fonts`) | ✅ HarfBuzz-equivalent shaping, ligatures, kerning, RTL/LTR/BiDi | ✅ rounds 72–77 — `VipsTextOps.DrawText` / `TextToPath` backed by `SixLabors.Fonts` (kerning, ligatures, full OpenType shaping). Multi-line layout (`\n`, `WrappingLength`, `LineSpacing`, `HAlign`, `WordBreak`, `Justify`); decorations (Underline / Strikeout / Overline as flags); writing mode (`LayoutMode` Horizontal* / Vertical*); reading direction (`TextDirection` LeftToRight / RightToLeft / Auto with Unicode BiDi resolution); opt-in `OpenTypeFeatures` (4-char tags — `smcp`, `ss01`, `frac`, `dlig`, etc.). Legacy `VipsImageOps.Text` (Magick.NET label render) still available for one-shot label generation |
| Text on path | ✅ | ✅ `VipsTextOps.TextOnPath(opts, targetPath, offset)` (round 74) — shapes text at origin, flattens glyph outlines to polylines, then warps each point onto the target via arc-length parameterisation. Tangent-aware so glyphs rotate to follow path direction. `offset` shifts perpendicular (positive = below path). Single sub-path target only |
| Text wrapping / measuring | ✅ | ✅ wrapping via `VipsTextOptions.WrappingLength` + word break + alignment (round 73). Measuring via `VipsTextOps.MeasureText` (layout box) / `MeasureBounds` (tight glyph bbox) / `CountLines` (round 78) |

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
| `Color`, `Rgb`, `LinearRgb` | 🟡 typed pixel structs `Rgb24` / `Rgba32` / `RgbVector` / `RgbaVector` (rounds 55, 97) cover the byte / float per-channel layouts; sRGB↔linear conversion via `Linearize` / `Delinearize` ops + `VipsColorConvert` (round 79). No dedicated `LinearRgb`-flavoured struct (the layout is identical to `RgbVector` — gamma is a pipeline concept here, not a per-pixel type) |
| `Hsl`, `Hsv` | ✅ via `VipsColorHsl` / `VipsColorHsv` value types + `VipsColorConvert.RgbToHsl` etc. (round 79) |
| `CieLab`, `CieLch`, `CieLchuv`, `CieLuv` | ✅ all four — `VipsColorLab` / `VipsColorLch` (round 79) + `VipsColorLuv` / `VipsColorLchuv` (round 80, CIE 1976 UCS) via `VipsColorConvert` |
| `CieXyz`, `CieXyy` | ✅ `VipsColorXyz` (D65, normalised Y=1) (round 79) + `VipsColorXyy` chromaticity form (round 80) |
| `Cmyk` | ✅ `VipsColorCmyk` value type + RGB↔CMYK conversion via `VipsColorConvert` (round 79) |
| `HunterLab` | ✅ `VipsColorHunterLab` (D65 reference white; `Ka` and `Kb` scaled per the modern CIE references) via `VipsColorConvert.XyzToHunterLab` / `HunterLabToXyz` (round 82) |
| `LmsBradford`, `LmsCAT02`, `LmsCAT97s` | ✅ all three matrices — `VipsColorLms` (round 80) via `XyzToLms(xyz, method)` / `LmsToXyz(lms, method)` taking `VipsLmsAdaptation.Bradford` (default) / `Cat02` / `Cat97s` (round 82). Inverses derived numerically via 3×3 inverter for accuracy |
| `YCbCr` | ✅ `VipsColorYCbCr` value type + `RgbToYCbCr` / `YCbCrToRgb` via `VipsColorConvert` (round 82) — BT.601 / JPEG full-range; chroma uses 0.5 = neutral offset |
| Chromatic adaptation | ✅ `VipsColorConvert.ChromaticAdapt(xyz, fromWP, toWP)` (round 80) — Bradford von Kries transform with LMS pivot |
| White-point (D65, D50, etc.) | ✅ `VipsWhitePoint` enum + `WhitePointXyz` accessor — D65 / D50 / D55 / D75 / A / E (round 80) |
| `ColorConverter.Convert<TFrom, TTo>(...)` | ✅ `VipsColorConvert.Convert<TFrom, TTo>` (round 79) — generic dispatcher routing through RGB / XYZ. Direct pairwise methods also available (RgbToHsl, RgbToHsv, RgbToCmyk, RgbToXyz, XyzToLab, LabToLch). Identity case skips conversion |
| `ColorMatrix` (4×4 with alpha channel) | ✅ `VipsImageOps.ColorMatrix(input, matrix)` — 4×5 matrix transform (4×4 mix + 1 translation column) on RGBA pixels, mirroring ImageSharp's `Filter(ColorMatrix)`. Per-image op alongside the per-value `VipsColorConvert` family |

Color-space coverage is now at parity-or-better — value types for every
ImageSharp colour space, full conversion graph through `VipsColorConvert`,
chromatic adaptation, and a real ICC CMM (Matrix/TRC fast path + full
LUT-based profile support) that exceeds ImageSharp's RGB-matrix
approximation.

---

## Metadata

| Capability | ImageSharp | CosmoImage |
| :--- | :--- | :--- |
| Raw EXIF/XMP/ICC byte-blob round-trip | ✅ | ✅ |
| Typed EXIF tag access (`ExifProfile.GetValue<T>(ExifTag)`) | ✅ — full tag dictionary | ✅ `VipsExifProfile` (round 83) — TIFF-format parser + serializer with typed `GetValue<T>` / `SetValue<T>`; IFD0 + Exif sub-IFD + GPS sub-IFD (round 87, separate `VipsGpsTag` enum + `GetGpsValue` / `SetGpsValue` API), both byte orders, common tags. Decimal-degree `GetLocation()` / `SetLocation()` helpers convert between user-friendly degrees and the on-wire DMS rationals. Bridge methods `VipsImage.GetExifProfile()` / `SetExifProfile()` |
| EXIF profile editing | ✅ | ✅ via `VipsExifProfile.SetValue<T>` + `Remove(tag)` + `SetExifProfile` round-trip (round 83) |
| IPTC profile (read + write) | ✅ | ✅ `VipsIptcProfile` (round 84) — typed parser + serializer for IIM Application-Record streams; supports repeatable tags (Keywords, Byline, etc.) as `IReadOnlyList<string>`; UTF-8; standard 2-byte and extended 4-byte length forms; entries from non-Application records are silently skipped. Bridge methods `VipsImage.GetIptcProfile()` / `SetIptcProfile()`. Format-specific extraction (JPEG APP13 / 8BIM unwrap) deferred — users attach the IIM bytes directly via the bridge for now |
| ICC profile structure parsing | ✅ — `IccProfile` with header + tag table | ✅ `VipsIccProfile` (rounds 85, 88) — typed header + tag-table indexed by 4-char signature with per-tag-type decoders. Round 88 added: `text` / `desc` / `mluc` for text tags (`Description` / `Copyright` properties auto-pick mluc on v4, desc/text on v2); `XYZ` decoding (`WhitePoint` / `BlackPoint` / `RedPrimary` / `GreenPrimary` / `BluePrimary` for `wtpt` / `bkpt` / `rXYZ` / `gXYZ` / `bXYZ`); `curv` decoding via `GetTagCurveGamma` / `GetTagCurveTable` (handles identity / single-gamma u8Fixed8 / N-entry LUT). LUT-AtoB / parametric curves / named-color tables / chromaticity tag still missing (rare on consumer profiles) |
| ICC profile applied at sink (proper CMM) | 🟡 — uses RGB-matrix approximation | ✅ pure-managed CMM (rounds 114, 121-126): Matrix/TRC fast path with precomputed forward + inverse LUTs (round 114, sRGB / AdobeRGB / Display-P3 / etc.) + LUT-based profiles via lut16Type ('mft2'), lut8Type ('mft1'), lutAtoBType ('mAB '), lutBtoAType ('mBA ') (rounds 121-122, 124) + n-linear CLUT covering Gray / Lab / RGB / CMYK device sides + mixed src/dst band counts (RGB↔CMYK transforms via round 123) + Lab↔XYZ PCS conversion (round 125, standard CIE formulas with D50 white point) + rendering intent selection (round 126: Perceptual / RelativeColorimetric / Saturation tag slots with fallback). Legacy Magick path retained for profiles outside the modeled set (mpet multiProcessElementsType, black-point compensation). |
| XMP DOM | ✅ via `SixLabors.Fonts` extension | ✅ `VipsXmpProfile` (round 86) — XDocument-backed DOM with typed accessors for the four standard XMP value shapes (simple, `rdf:Bag`, `rdf:Seq`, language-alternative `rdf:Alt` with `xml:lang`). Standard namespace constants (Dc / Xmp / Exif / Tiff / Photoshop / Iptc4XmpCore). Direct `Document` access for advanced use. xpacket markers stripped on parse, re-emitted on serialize. Bridge methods `VipsImage.GetXmpProfile()` / `SetXmpProfile()` |
| Format-specific metadata (PNG text chunks, JPEG comments, GIF comments) | ✅ structured | ✅ typed extensions on `VipsImage` (round 98): `GetPngText` / `SetPngText` / `RemovePngText` / `GetAllPngText` / `SetAllPngText` for PNG `tEXt` / `iTXt` keywords (with `PngTextKeywords` constants for the spec-standard names — Title / Author / Description / Copyright / CreationTime / Software / Disclaimer / Warning / Source / Comment); `GetJpegComment` / `SetJpegComment` / `RemoveJpegComment` for the JPEG COM marker; `GetGifComment` / `SetGifComment` / `RemoveGifComment` for the GIF Comment Extension. Storage namespaced on `Metadata` dict (`png:text:*`, `jpeg:comment`, `gif:comment`); loader/saver pickup is per-format work for future rounds |
| `VipsFields` typed accessors | n/a (their typed-profile API serves the same purpose) | ✅ `GetInt/Double/DoubleArray/Blob` + well-known shortcuts |
| FITS / NIfTI header round-trip | ❌ | ✅ via `Metadata["fits:*"]` / `["nifti:*"]` |
| OME-TIFF parsing | ❌ | ✅ `VipsOmeTiff` typed accessors |

Typed profile access is now substantially complete: `VipsExifProfile`
(incl. GPS sub-IFD), `VipsIptcProfile`, `VipsIccProfile` (with
parametric-curve / mft1 / mft2 / mAB / mBA tag-type decoders),
`VipsXmpProfile` (XDocument DOM with simple / Bag / Seq / Alt-lang
accessors), plus typed accessors for PNG `tEXt` / `iTXt`, JPEG COM,
and GIF Comment Extension. Editing workflows (rotate EXIF orientation,
set a DateTime tag, edit XMP keywords) work without reaching for
Magick. Format-specific extraction inside container formats (JPEG
APP13 8BIM-wrapped IPTC) deferred — users attach the IIM bytes
directly via the bridge.

---

## Streaming, async, MemoryAllocator

| Capability | ImageSharp | CosmoImage |
| :--- | :--- | :--- |
| Stream-based load (no full-buffer hop) | ✅ — every decoder | 🟡 `LoadStreamingAsync` opt-in on every Stream-capable format; PNG/PDF still byte-buffer (decoder limit) |
| Stream-based save | ✅ | ✅ — every saver is `PipeWriter`-based |
| `MemoryAllocator` configurable | ✅ — every buffer goes through it | 🟡 `IVipsAllocator` plumbed through transient buffers (`VipsRegion`, `OrderedStripSink`) and opt-in for `MemorySink` long-lived buffers (round 94 — pass an `IVipsAllocator` to the constructor; sink is `IDisposable` for the pool-return). Default behaviour unchanged (`BareAllocator` = `new byte[]`). `PixelsLazy` factories still use plain `new byte[]` — converting them needs a per-call ownership story |
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
| Custom format plugin registration | ✅ — register decoder/encoder pair via `Configuration` | ✅ `VipsConfiguration.Default.Register(IVipsImageFormat)` (round 89: decoder side) + `IVipsImageFormat.SaveAsync` + `VipsConfiguration.Default.SaveAsync(image, stream, formatName)` for save-by-name dispatch (round 90: encoder side) + `IVipsImageFormat.FileExtensions` + `FindByExtension` + `SaveByExtensionAsync` for ergonomic extension-based save (round 108). All 13 encodable built-ins wired with their conventional extensions: `.png` / `.jpg` / `.jpeg` / `.webp` / `.gif` / `.bmp` / `.tif` / `.tiff` / `.qoi` / `.heif` / `.heic` / `.avif` / `.hdr` / `.fits` / `.nii` / `.tga` / `.pnm` / `.ppm` / `.pgm` / `.pbm`. Decoder-only formats (PDF, SVG, JXL, JP2K) declare extensions but throw on save |

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
- **Pure-managed TIFF, including BigTIFF, tiles, predictor=2/3, multi-page,
  and 32-bit float samples** — covers the geospatial / scientific-imaging
  cases ImageSharp doesn't reach. JPEG-in-TIFF is the only fallback to
  Magick.
- **Pure-managed WebP VP8L lossless** decoder including the four
  spatial transforms, color cache, and meta-Huffman partitioning.
- **Real pure-managed ICC CMM** — Matrix/TRC fast path + LUT-based
  profiles (mft1 / mft2 / mAB / mBA) + n-linear CLUT for CMYK + Lab↔XYZ
  PCS conversion + rendering intents. ImageSharp uses an RGB-matrix
  approximation; CosmoImage does the real CIE math.
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
| Codecs (modern web formats) | 🟢 PNG / APNG (incl. IDAT-as-fallback) / JPEG (with proper YCbCr→RGB) / GIF-still / TIFF (incl. JPEG-in-TIFF, signed ints, raw deflate, FillOrder=2, tiled+planar=2) / BMP (incl. RLE4, BI_BITFIELDS) / TGA (full variant matrix incl. paletted + 15/16-bit) / QOI / PNM (incl. PAM + 16-bit) / WebP-lossless all pure-managed; WebP-lossy + GIF-animated still via Magick |
| Codecs (scientific / niche) | 🟢 we exceed ImageSharp here (HDR with old-RLE + axis orderings / FITS / NIfTI / MAT / CSV; PDF render via Docnet); OpenEXR substantially complete (rounds 127–164: PIZ, PXR24, B44/B44A, DWA-UNKNOWN+RLE+DCT-self-validated, multi-part, MIPMAP/RIPMAP, HALF/FLOAT/UINT, generic 1-4 channels) |
| Processing extensions (color/effects/geometric/etc.) | 🟡 ~40 of ~50 ops, a few via Magick |
| Drawing & vector graphics | ✅ rounds 61–70 shipped path builder (move / line / cubic / quadratic / arc / close) + shape factories + all 6 brushes (solid / linear / radial / path / image / pattern) + FillPath + StrokePath + AA + complete VipsPen (caps / joins / miter limit / dashes) + affine path transforms + rectangular clipping + path-vs-path booleans (intersect / union / subtract via Greiner-Hormann) |
| Color spaces | ✅ all ImageSharp colour types (Hsl / Hsv / Lab / Lch / Luv / Lchuv / Xyz / Xyy / Cmyk / HunterLab / LMS / YCbCr) + chromatic adaptation + full ICC CMM (Matrix/TRC + LUT-based + Lab↔XYZ PCS + rendering intents, rounds 79-82, 114, 121-126) |
| Metadata typed access | ✅ `VipsExifProfile` (incl. GPS) / `VipsIptcProfile` / `VipsIccProfile` / `VipsXmpProfile` + format-specific (PNG text chunks, JPEG COM, GIF Comment) — rounds 83-88, 98 |
| `MemoryAllocator` integration | 🟡 transient buffers + `MemorySink` long-lived; decoded `PixelsLazy` buffers still `new byte[]` |
| Streaming load | 🟢 opt-in on every Stream-capable format |
| Image.IdentifyAsync (single entry point) | ✅ `IdentifyAsync(stream)` + `LoadAsync(stream)` (round 54) |
| Configuration registry | ✅ `VipsConfiguration.Default.Register(IVipsImageFormat)` for decoders + encoders + extension-based save dispatch (rounds 89-93, 108) |

The headline conclusion: CosmoImage covers the **mainline web-image
pipeline** (load, transform, save with proper colour-managed Float
intermediates) at parity-or-better with ImageSharp, and exceeds it
on scientific / VFX formats. The pixel-format zoo, vector-drawing
surface, typed-metadata access, and ICC color management are done.
After rounds 133–164, OpenEXR is now substantially complete pure
(PIZ, B44/B44A, PXR24, DWA partial-but-self-validated, multi-part,
MIPMAP/RIPMAP, HALF/FLOAT/UINT, generic channels) — bringing it from
"partial" to "near-full" coverage. Remaining gaps are mostly
**codec-completion work** — WebP VP8 lossy + animated, the last bits
of OpenEXR DWA (RGB-CSC + libimf-DC-encoding), and niche formats
(JPEG XL, JPEG 2000, DICOM, OpenSlide, dcraw). The architectural gap
(eager `Image<TPixel>` vs lazy demand-driven) stays permanent by
design; the typed-generic-op surface is closable but follows a
broader direction tracked in `TODO_PARITY.md`.

---

*Last updated: 2026-05-05. Compares CosmoImage's current state at
1413 passing tests (through round 164) against the ImageSharp 3.x
API surface.*
