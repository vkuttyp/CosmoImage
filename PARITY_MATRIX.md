# Parity Matrix (CosmoImage)

Snapshot of where this library stands vs **libvips** (the C reference it ports
from) and **ImageSharp** + **SkiaSharp** (the .NET libraries it competes with).

Status legend: ✅ full · 🟢 production-ready · 🟡 partial · ❌ missing

---

## Architecture (vs libvips IO model)

| Capability | Status | Notes |
| :--- | :--- | :--- |
| Demand-driven lazy regions | ✅ | `VipsRegion.Prepare` + `GenerateFn`; chained ops compute only what the sink consumes |
| Sink-driven threadpool | ✅ | `VipsSink` + bounded `Channel<VipsRect>` + N workers; matches `vips_threadpool_run` |
| Per-worker `seq` (`StartFn`/`StopFn`) | ✅ | `VipsSeq.StartOne` / `StartMany` mirror `vips_start_one` / `vips_start_many` |
| Demand hints with min-propagation | ✅ | `VipsDemandStyle` + `SetPipeline`; SmallTile/FatStrip/ThinStrip/Any |
| Memory-image dtype | ✅ | `VipsImage.PixelsLazy`; `Prepare` aliases the buffer (zero-copy reads, libvips SETBUF/MMAPIN equivalent) |
| `MemorySink` for tile-shape-aware materialization | ✅ | Honors DemandHint; SmallTile pipelines get 128×128 tiles |
| Bounds-asserting `GetAddress` | ✅ | `Debug.Assert` instead of silent clamping (matches `VIPS_REGION_ADDR`) |
| Operation cache | 🟡 | Simple count-based; libvips has LRU + resource-aware. Functional but not production-tuned |
| SIMD on hot paths | 🟡 | `VipsLinear`, `VipsInvert`, `VipsGlow` use `Vector<T>` pointwise; not pervasive |

---

## Loaders

| Format | Header | Pixel decode | Animated/multi-page | Metadata extraction |
| :--- | :---: | :---: | :---: | :--- |
| JPEG | ✅ | ✅ | n/a | EXIF + XMP raw blobs, orientation int |
| PNG | ✅ | ✅ | n/a | EXIF (eXIf), ICC (iCCP, deflated), XMP (iTXt) |
| WebP | ✅ | ✅ | ✅ animated | EXIF/XMP/ICC via Magick |
| TIFF | ✅ | ✅ | ✅ multi-page | EXIF/XMP/ICC + orientation |
| BMP | ✅ | ✅ | n/a | — |
| GIF | ✅ | ✅ | ✅ animated (per-frame delays) | EXIF/XMP/ICC + Comment via Magick |
| HEIF / AVIF | ✅ | ✅ | ❌ sequences | EXIF/XMP/ICC via Magick |
| PDF | ✅ | ✅ | ✅ multi-page render | `pdf-n-pages` count |
| SVG | ✅ | ✅ rasterize | n/a | — |
| JPEG XL | 🟡 header stub | ❌ | n/a | — (decoder unavailable in managed) |
| JPEG 2000 | 🟡 header only | ❌ | n/a | — |
| OpenEXR / Radiance HDR / FITS / NIfTI | ❌ | ❌ | n/a | — |
| CSV / Matrix / Matlab | ❌ | ❌ | n/a | — |
| TGA | ✅ | ✅ | n/a | EXIF/XMP/ICC via Magick |
| QOI | ✅ | ✅ | n/a | — |
| PBM / PGM / PPM / PAM | ✅ | ✅ | n/a | — |
| CSV / Matrix (numeric text) | ✅ | ✅ | n/a | — |

---

## Savers

| Format | Single-frame | Animated/multi-page | Metadata write |
| :--- | :---: | :---: | :--- |
| JPEG | ✅ | n/a | EXIF + XMP via APP1; ICC via multi-segment APP2 |
| PNG | ✅ (full + palette PNG-8) | n/a | eXIf, iCCP (deflated), iTXt for XMP |
| WebP | ✅ | ✅ animated | EXIF/XMP/ICC via Magick |
| TIFF | ✅ | ✅ multi-page; pyramidal (Ptif) on `pyramid:true` | EXIF/XMP/ICC native |
| HEIF / AVIF | ✅ | ❌ sequences | EXIF/XMP/ICC via Magick |
| GIF | ✅ | ✅ animated | profiles + Comment on first frame |
| APNG | ✅ | ✅ animated | profiles + Comment on first frame |
| TGA / QOI / PBM-PAM | ✅ | n/a | EXIF/XMP/ICC via Magick (where supported) |
| Pyramidal TIFF (Ptif) | ✅ via `SaveTiffAsync(pyramid:true)` | — | — |
| OME-TIFF / dzsave (Deep Zoom) | ❌ | — | — |

---

## Operations (by category)

### Geometric

| Op | Status | Notes |
| :--- | :---: | :--- |
| Resize (kernel-based, separable) | ✅ | 10 kernels: Nearest, Linear, Cubic, Mitchell, Lanczos2/3/5, Hermite, BicubicSharper/Smoother |
| Affine (kernel-based) | ✅ | Same kernel suite; arbitrary 2x2 + offset; `OutWidth`/`OutHeight` |
| Rotate (orthogonal) | ✅ | D0/D90/D180/D270, no resampling |
| Rotate (arbitrary angle) | ✅ | Wrapper over Affine, computes bounding box |
| Flip | ✅ | Horizontal/vertical |
| Shrink (integer box-average) | ✅ | Used as fast-path before Resize |
| ExtractArea / Crop | ✅ | |
| EntropyCrop / SmartCrop | ✅ | Greedy entropy-driven trim |
| Thumbnail | ✅ | Composes Resize + AutoOrient + Crop |
| AutoOrient (EXIF orientation) | ✅ | Flip+Rotate combo + EXIF blob orientation patch |
| Reduce (non-integer downscale) | ✅ via Resize | libvips' `vips_reduce` integrated into Resize |

### Color / pointwise

| Op | Status | Notes |
| :--- | :---: | :--- |
| Invert | ✅ | SIMD pointwise |
| Linear (a·x + b per band) | ✅ | SIMD when same-coefficient |
| Gamma | ✅ | LUT-based |
| Brightness | ✅ | Linear with alpha-preserve |
| Contrast | ✅ | Linear around midpoint with alpha-preserve |
| Lightness (HSL L-axis) | ✅ | Per-pixel HSL conversion |
| Saturate | ✅ | 3×3 luma-mix matrix via Recomb |
| Hue rotation | ✅ | RGB rotation matrix around (1,1,1) gray axis |
| Greyscale | ✅ | Saturate(0); preserves band count |
| Sepia | ✅ | Standard 3×3 sepia matrix |
| Recomb (NxN band matrix) | ✅ | Alpha pass-through |
| Maplut | ✅ | LUT image input |
| Linearize / Delinearize | ✅ | IEC 61966-2-1 piecewise sRGB transfer |
| Quantize (Wu/median-cut) | ✅ | Magick.NET-backed |
| Math suite (abs, sin, cos, tan, log, log10, exp, exp10, sqrt, pow) | ✅ | LUT-based pointwise on UChar |
| Boolean / Relational suite (and, or, xor, lshift, rshift / eq, ne, lt, le, gt, ge) | ✅ | Const-vs-image and image-vs-image variants |
| Stats (avg, min, max, deviate) | ✅ | Per-band + aggregate via materializing scan |

### Convolution / morphology

| Op | Status | Notes |
| :--- | :---: | :--- |
| Conv (2D mask) | ✅ | |
| Conv1D | ✅ | Building block for separable kernels |
| GaussBlur | ✅ | Two-pass separable Conv1D |
| UnsharpMask | ✅ | SIMD pointwise on input + GaussBlur |
| Dilate / Erode | ✅ | |
| Morph (general) | ✅ | |
| Open / Close | ✅ | Compositions of Erode/Dilate |
| Rank / Median | ✅ | Quickselect over k×k window |

### Composition / drawing

| Op | Status | Notes |
| :--- | :---: | :--- |
| Composite (alpha-over) | ✅ | Sub-pixel positioning via Affine pre-shift |
| DrawLine | ✅ | Xiaolin Wu antialiased; axis-aligned fast path full-coverage |
| DrawRect (outline + fill) | ✅ | |
| Text (glyph rendering) | 🟡 | Rudimentary — no proper shaping/kerning |
| Paths / polygons / gradients / brushes | ❌ | Out of scope (Skia territory) |

### Effects

| Op | Status | Notes |
| :--- | :---: | :--- |
| Vignette | ✅ | Quadratic radial darkening |
| Pixelate | ✅ | Shrink + Nearest upscale |
| Glow | ✅ | Bloom (input + sigma·blur) |
| OilPaint / Charcoal / Sketch / Polaroid | ✅ | Magick.NET wrappers |
| BokehBlur | ✅ | Hexagonal-aperture kernel via `Conv` |

### Analysis / frequency

| Op | Status | Notes |
| :--- | :---: | :--- |
| HistFind | ✅ | Per-band 256-bin histogram |
| HistCum / HistNorm / HistEqual | ✅ | |
| Forward FFT (`FwFft`) | ✅ | MathNet, row+column 1D passes |
| Inverse FFT (`InvFft`) | ✅ | Magnitude back to UChar |
| Spectrum (log magnitude) | ✅ | FFT-shifted, normalized per band |

---

## Metadata round-trip across formats

| Format | EXIF | XMP | ICC |
| :--- | :---: | :---: | :---: |
| JPEG | ✅ | ✅ | ✅ multi-segment APP2 |
| PNG | ✅ eXIf | ✅ iTXt | ✅ iCCP |
| WebP | ✅ | ✅ | ✅ |
| TIFF | ✅ | ✅ | ✅ |
| HEIF / AVIF | ✅ | ✅ | ✅ |
| GIF / APNG | ✅ | ✅ | ✅ |

Cross-format conversion (e.g. JPEG → AVIF) preserves all three blob types.
GIF and APNG also carry a free-form `Metadata["comment"]` round-trip via
Magick.NET's Comment attribute.

---

## vs ImageSharp specifically

Items where ImageSharp differs and we don't currently match:

| Item | Status | Comment |
| :--- | :---: | :--- |
| `Image<TPixel>` strongly-typed pixel access | ❌ | We use `byte[] Pixels` + `BandFormat` enum; pixel safety is runtime, not compile-time |
| `Mutate(action)` block-scoped op chaining | ✅ | `image.Mutate(im => im.Resize(0.5).Sepia())` — wraps the fluent extensions |
| Float-format pixel ops throughout | ❌ | Most ops are UChar-only; would require every op to gain a Float code path |
| `MemoryAllocator` (caller-supplied buffer pool) | 🟡 | `IVipsAllocator` plumbed through `VipsRegion` and `OrderedStripSink`; default `ArrayPoolAllocator` wraps `ArrayPool<byte>.Shared`. Long-lived buffers (`PixelsLazy`, `MemorySink.Pixels`) intentionally bypass the pool — pool ownership across an image lifetime is harder to reason about |
| TGA / QOI / PBM formats | ❌ | Niche; rarely needed |

Items where we match or exceed ImageSharp:

| Item | Status | Comment |
| :--- | :---: | :--- |
| Lazy demand-driven pipeline | ✅ | ImageSharp materializes per-op; we don't until the sink consumes |
| Multi-stage parallelism | ✅ | One threadpool drains the whole chain (sink-side); ImageSharp parallelizes per-op |
| HEIF/AVIF native (no extension package needed) | ✅ | Magick.NET ships the codecs |
| Permissive licensing only | ✅ | No Six Labors split-license dependency |
| Multi-page PDF render | ✅ | Docnet-backed |
| Cross-format metadata blob round-trip | ✅ | EXIF/XMP/ICC traverse format boundaries |

---

## Architectural lifts deliberately deferred

| Item | Effort | Why deferred |
| :--- | :--- | :--- |
| Float-precision ops throughout | Months | Every op (Resize/Conv/Linear/Affine/etc.) needs a parallel Float code path; current Linearize/Delinearize covers the 90% case via UChar LUT |
| Proper CMM-based ICC color management | Significant | Needs a LittleCMS native binding; current `IccTransform` uses Magick.NET as a one-shot transform |
| `MemoryAllocator` for lazy materializers | Moderate | Transient buffers (`VipsRegion`, `OrderedStripSink`) now pool through `IVipsAllocator`. Extending to `MemorySink.Pixels` and loader `PixelsLazy` requires explicit ownership/disposal semantics on `VipsImage` — design discussion before code |
| `Image<TPixel>` strong typing | Significant | Would touch every public op signature; doesn't translate cleanly to lazy-pipeline model where ops produce new images |
| Streaming (truly-non-buffered) loaders | Significant | All codecs decode into memory; ImageSharp does the same — parity item not gap |
| TIFF pyramidal / dzsave (deep-zoom output) | Moderate | Would need its own multi-resolution writer wrapping libtiff |

---

*Last updated: 2026-05-02. Numbers in this matrix track the source tree
under `Core/`, `Loaders/`, `Savers/`, and `Operations/{Geometric,Color,
Effects,Convolution,Drawing,Analysis,Misc}/`. 84 tests pass.*
