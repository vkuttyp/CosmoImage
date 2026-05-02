# Parity Matrix (CosmoImage vs upstream libvips)

Snapshot of where this library stands against the **upstream
libvips** C reference (~300 ops across 12 subsystems, 450 .c files).
Earlier versions of this matrix understated the gap by an order of
magnitude — this rewrite mirrors libvips' actual subsystem layout
(`arithmetic/`, `colour/`, `conversion/`, `convolution/`, `create/`,
`draw/`, `foreign/`, `freqfilt/`, `histogram/`, `iofuncs/`,
`morphology/`, `mosaicing/`, `resample/`).

Status legend: ✅ full · 🟢 production-ready · 🟡 partial · ❌ missing

---

## Architecture (`iofuncs/`)

The libvips engine itself — demand-driven regions, sink-driven
threadpool, source/target abstractions. ~41 source files; we have
ports of the core ones plus a few CosmoImage-specific extensions
(typed pixel access, ArrayPool integration).

| Capability | Status | Notes |
| :--- | :--- | :--- |
| Demand-driven lazy regions (`region.c`, `generate.c`) | ✅ | `VipsRegion.Prepare` + `GenerateFn` |
| Sink-driven threadpool (`threadpool.c`, `sink.c`, `sinkmemory.c`) | ✅ | `VipsSink` + bounded `Channel<VipsRect>` + N workers |
| Per-worker `seq` (`vips_start_one`, `vips_start_many`) | ✅ | `VipsSeq.StartOne` / `StartMany` |
| Demand hints (`SmallTile`/`FatStrip`/`ThinStrip`/`Any`) | ✅ | `VipsDemandStyle` + `SetPipeline` min-propagation |
| Memory-image dtype (`SETBUF`/`MMAPIN`) | ✅ | `VipsImage.PixelsLazy`; `Prepare` aliases the buffer |
| Source abstraction (`source.c`, `connection.c`, `sbuf.c`) | 🟡 | `IVipsSource` + `PipeVipsSource` + `VipsSourceStream` adapter; libvips has fancier features (custom callbacks, mmap, signals) we don't expose |
| Target abstraction (`target.c`, `targetcustom.c`) | ❌ | We only have `PipeWriter`-based saver entry points; no symmetric `IVipsTarget` interface |
| Operation cache (`cache.c`) | 🟡 | Simple count-based; libvips has LRU + resource-aware eviction |
| Op profiling / gating (`gate.c`) | ❌ | Built-in profiler for finding slow stages |
| Op-tree reordering (`reorder.c`) | ❌ | Memory-locality-aware op ordering |
| Disc-backed sink (`sinkdisc.c`) | ❌ | Tiled output for huge images that don't fit in memory |
| Live preview sink (`sinkscreen.c`) | ❌ | Background recompute for GUI viewports — niche, used by libvips' own GUI |
| SIMD scaffolding (`vector.cpp`) | 🟡 | A few hot paths use `Vector<T>` (Linear / Invert / Glow); libvips has a runtime IR that compiles SIMD per op |
| `vips_image_get_*` typed accessors | ✅ | `VipsFields` (round 2) + typed pixel access via `TypedImage<TPixel>` (round 4) |

---

## `arithmetic/` (~50 files in libvips)

Pointwise arithmetic, statistics, hough transform, measurement.

| libvips op | Status | Our equivalent |
| :--- | :---: | :--- |
| `abs`, `sin`, `cos`, `tan`, `atan`, `log`, `log10`, `exp`, `exp10`, `sqrt`, `sign`, `round`/`floor`/`ceil`/`rint` (math, math2, sign, round, unary) | 🟡 | `Math` suite covers abs/sin/cos/tan/log/log10/exp/exp10/sqrt/pow. Missing: atan/atan2, sign, floor/ceil/rint variants |
| `pow`, `wop` (math2 — y = x^a) | ✅ | `Pow(image, exp)` |
| `add`, `subtract`, `multiply`, `divide`, `remainder` (binary) | ✅ | `Add`/`Subtract`/`Multiply`/`Divide`/`Remainder` — UChar branch clamps and treats multiply as fraction-of-255; Float branch unclamped |
| `linear` (a·x + b per band) | ✅ | `Linear` with SIMD same-coefficient path |
| `invert` | ✅ | SIMD pointwise; libvips Float convention (`-x`) on Float input |
| `boolean`, `boolean_const` (and/or/xor/lshift/rshift) | ✅ | `Boolean2`, `BooleanConst` |
| `relational`, `relational_const` (eq/ne/lt/le/gt/ge) | ✅ | `Relational2`, `RelationalConst` |
| `complex`, `complex2`, `complexform`, `complexget` | ❌ | No complex-number ops on DPComplex images |
| `min`, `max`, `sum` (reductions) | ✅ via `Stats` | Per-band + aggregate min/max/sum/avg/deviate in one pass |
| `avg`, `deviate`, `stats` | ✅ | `Stats(image)` returns full result; `Avg`/`Min`/`Max`/`Deviate` shortcuts |
| `maxpair`, `minpair` (per-pixel max/min of two images) | ❌ | |
| `getpoint` (extract single pixel as values) | ❌ | Equivalent: `image.GetPixel<TPixel>(x, y)` |
| `find_trim` (auto-find non-background bbox) | ❌ | |
| `measure` (extract patch averages from grid) | ❌ | Color-chart calibration helper |
| `profile` (column/row first/last non-zero) | ❌ | |
| `project` (sum-along-axis, both axes) | ❌ | |
| `hist_find`, `hist_find_indexed`, `hist_find_ndim` | 🟡 | `HistFind` (per-band UChar). N-dim variants missing. |
| `hough_circle`, `hough_line` | ❌ | Feature detection — niche |
| `clamp` | ❌ | Per-band clamp to range |

---

## `conversion/` (~40 files in libvips)

Format and layout conversions — band manipulation, embedding,
flattening, premultiplication. **Major gap area.**

| libvips op | Status | Our equivalent |
| :--- | :---: | :--- |
| `cast` (band-format conversion) | ✅ | `Cast`/`CastFloat`/`CastUChar` (UChar↔Float only) |
| `copy` | 🟡 | Implicit via op pipeline; no explicit `Copy(image)` |
| `extract_area` / `extract_band` | ✅ | `ExtractArea(left, top, w, h)` and `ExtractBand(band, n=1)` |
| `embed` (place into larger canvas with extension mode) | ✅ | `Embed` with Black/White/Copy/Repeat/Mirror/Background modes; per-band background colour |
| `gravity` (positional embed) | ✅ | `Pad(width, height, background, position)` with `VipsCompass` (Centre/N/E/S/W/NE/SE/SW/NW); `BackgroundColor(...)` flattens transparent pixels onto a fill colour while keeping alpha |
| `flip` | ✅ | |
| `rot` (orthogonal) / `rot45` | 🟡 | `Rotate(VipsAngle)` ✅; rot45 missing |
| `autorot` (EXIF-based) | ✅ | `AutoOrient` |
| `composite`, `composite2` | 🟡 | `Composite` (over-blend only); libvips has 19 PorterDuff modes |
| `recomb` | ✅ | |
| `gamma` | ✅ | |
| `flatten` (alpha-flatten against background) | ✅ | `Flatten(r, g, b)` — composes RGBA/GA over an opaque background, drops alpha. UChar + Float branches |
| `premultiply` / `unpremultiply` | ✅ | `Premultiply` / `Unpremultiply` — UChar normalizes alpha by 255; Float treats alpha as nominal [0,1]. Pass-through on band counts without alpha |
| `addalpha` | ✅ | `AddAlpha(alpha=255)` — synthesise constant alpha plane and bandjoin. Pass-through if input already has alpha |
| `bandbool` (and/or/xor across bands) | ✅ | `Bandbool(op)` reduces input bands with AND/OR/XOR → single-band UChar |
| `bandfold` / `bandunfold` (W↔W*bands rearrange) | ❌ | |
| `bandjoin` | ✅ | `Bandjoin(other, …)` — concatenate bands across N images. `bandjoin_const` (fold constants in) still missing |
| `bandmean` (average all bands) | ✅ | `Bandmean()` — UChar (with rounding) and Float branches |
| `bandrank` (rank-statistic across bands) | ❌ | |
| `byteswap` | ❌ | |
| `cache` (operation result cache) | 🟡 | Internal `VipsCache` only |
| `falsecolour` | ✅ | `Falsecolor()` — built-in jet colour ramp; 1-band UChar → RGB |
| `grid` (lay tiles into grid) | ❌ | |
| `ifthenelse` (per-pixel ternary) | ✅ | `Ifthenelse(then, else)` — UChar condition broadcasts (1-band) or selects per-band (N-band). UChar + Float then/else |
| `insert` (paste image at point) | 🟡 | `Composite` covers the common case |
| `join` (join two images side-by-side) | ❌ | |
| `arrayjoin` (join N images in a grid) | ❌ | |
| `msb` (most-significant-byte extraction) | ❌ | |
| `replicate` (tile to bigger size) | ✅ | `Replicate(across, down)` — scanline-slab copy across tile seams |
| `scale` (linear stretch to 0..255) | ❌ | Different from `Resize` — value-range scaling |
| `sequential` (force sequential read order) | ❌ | |
| `subsample` | 🟡 | `Shrink` covers integer subsample |
| `switch` (case-style multi-image select) | ❌ | |
| `tilecache` (region cache) | ❌ | |
| `transpose3d` | ❌ | |
| `wrap` (toroidal shift) | ❌ | |
| `zoom` (integer scale-up by replication) | ❌ | |
| `smartcrop` | ✅ | `EntropyCrop` |

---

## `colour/` (~40 files in libvips)

The colour-management graph. **Severe gap.** libvips supports a full
colour graph between sRGB / scRGB / Lab / LabQ / LabS / LCh / UCS /
Oklab / Oklch / XYZ / Yxy / HSV / CMYK / CICP / uhdr / rad
colourspaces, plus dE76/dE00/dECMC colour-difference metrics.
We have only a few corners of it.

| libvips op | Status | Our equivalent |
| :--- | :---: | :--- |
| `colourspace` (graph dispatcher) | 🟡 | `Colourspace` covers a subset of target spaces |
| `sRGB2scRGB` / `scRGB2sRGB` (IEC 61966-2-1 transfer) | ✅ | `Linearize` / `Delinearize` (Float and UChar paths) |
| `scRGB2BW` | 🟡 | `Greyscale` (RGB-space, not scRGB) |
| `XYZ2Lab`, `Lab2XYZ`, `XYZ2Yxy`, `Yxy2XYZ` | ❌ | |
| `Lab2LCh`, `LCh2Lab`, `LCh2UCS`, `UCS2LCh` | ❌ | |
| `Lab2LabQ` / `LabQ2Lab` (8-bit packed Lab) | ❌ | |
| `Lab2LabS` / `LabS2Lab` (16-bit Lab) | ❌ | |
| `LabQ2sRGB`, `LabQ2LabS`, `LabS2LabQ` | ❌ | |
| `XYZ2Oklab`, `Oklab2XYZ`, `Oklab2Oklch`, `Oklch2Oklab` | ❌ | OkLab perceptual space |
| `sRGB2HSV` / `HSV2sRGB` | 🟡 | Internal use only inside `Lightness` |
| `XYZ2CMYK` / `CMYK2XYZ` | ❌ | Print colourspace |
| `XYZ2scRGB` / `scRGB2XYZ` | ❌ | |
| `CICP2scRGB` (ITU-R BT.2100 / Rec.2020 + transfer) | ❌ | HDR / wide-gamut |
| `uhdr2scRGB` (Ultra HDR JPEG gainmap → scRGB) | ❌ | Modern HDR-photo path |
| `float2rad` / `rad2float` (Radiance RGBE ↔ Float) | 🟡 | Built into `VipsHdrLoader`/`Saver` directly, not a standalone op |
| `dE76`, `dE00`, `dECMC` | ❌ | Colour-difference metrics |
| `icc_transform` | 🟡 | `IccTransform` via Magick.NET (one-shot, not pipeline-aware) |
| `profile_load` (load named ICC profile) | ❌ | |
| Custom "color" ops (matrix-driven RGB) | ✅ | `Saturate`, `Sepia`, `Hue`, `Brightness`, `Contrast`, `Lightness` |

---

## `convolution/` (~14 files in libvips)

| libvips op | Status | Our equivalent |
| :--- | :---: | :--- |
| `conv` (general 2D mask) | ✅ | `Conv` with Float branch |
| `convf` (float-only fast path) | ✅ via dispatch | Single Conv with Float dispatch |
| `convi` (int-only fast path) | ✅ via dispatch | Same |
| `convsep` (separable mask) | ✅ | `Conv1D` (X+Y composed by `GaussBlur`) |
| `conva` (approximate) | ❌ | `vips_conva` — approximate large-kernel via box-pass |
| `convasep` (approximate separable) | ❌ | |
| `gaussblur` | ✅ | Two-pass `Conv1D` |
| `sharpen` | 🟡 | `UnsharpMask` covers sigma+amount; libvips' `sharpen` does Lab-space with thresholds |
| `canny` | ❌ | Canny edge detector |
| `compass` (compass-pattern edge) | ❌ | |
| `correlation`, `fastcor`, `spcor` | ❌ | Template matching / cross-correlation |
| `edge` | ❌ | Generic edge detector wrapper |

---

## `create/` (~30 files in libvips)

Generators. **Whole subsystem missing** apart from `text`.

| libvips op | Status |
| :--- | :---: |
| `black` (constant 0 image) | ❌ |
| `xyz` (per-pixel x/y coordinate image — useful for mapim) | ❌ |
| `eye`, `grey`, `zone` (test-pattern generators) | ❌ |
| `gaussmat`, `logmat`, `gaussnoise` (filter mask generators) | ❌ |
| `mask_butterworth`, `mask_butterworth_band`, `mask_butterworth_ring` | ❌ |
| `mask_gaussian`, `mask_gaussian_band`, `mask_gaussian_ring` | ❌ |
| `mask_ideal`, `mask_ideal_band`, `mask_ideal_ring` | ❌ |
| `mask_fractal` | ❌ |
| `fractsurf` (fractal surface) | ❌ |
| `perlin`, `worley`, `sines` | ❌ | Procedural texture |
| `point` (sample image at point) | ❌ |
| `sdf` (signed distance field generator) | ❌ |
| `tonelut`, `buildlut`, `invertlut` (LUT builders) | ❌ |
| `identity` (identity LUT) | ❌ |
| `text` (glyph rendering) | 🟡 `Text` (rudimentary, no proper shaping) |

---

## `draw/` (~7 ops in libvips)

In-place pixel drawing onto a memory-backed image.

| libvips op | Status |
| :--- | :---: |
| `draw_line` | ✅ `DrawLine` (Xiaolin Wu antialiased) |
| `draw_rect` | ✅ `DrawRect` (outline + fill) |
| `draw_circle` | ❌ |
| `draw_flood` (flood fill) | ❌ |
| `draw_image` (paste image at point) | 🟡 covered by `Composite` |
| `draw_mask` (draw with alpha mask) | ❌ |
| `draw_smudge` | ❌ |

---

## `foreign/` (~70 files in libvips — loaders + savers)

| Format | libvips coverage | Our coverage |
| :--- | :--- | :--- |
| **JPEG** | full + EXIF/XMP/ICC + progressive + arith | ✅ pure-C# decoder + multi-segment ICC |
| **PNG** | libpng / libspng | ✅ via StbImageSharp; XMP via iTXt added |
| **TIFF** | libtiff (huge variant matrix) | 🟡 via Magick; multi-page + Ptif pyramid + OME-XML metadata |
| **WebP** | libwebp animated | 🟡 via Magick |
| **HEIF / AVIF** | libheif single + sequence | 🟡 via Magick; animated load works, save single-frame only |
| **GIF** | nsgif animated | 🟡 via Magick |
| **PDF** | poppler / pdfium | 🟡 via Docnet |
| **SVG** | librsvg | 🟡 via Magick |
| **BMP** | libvips + magick fallback | 🟡 pure-C# fast path (24/32 bpp BI_RGB) + Magick fallback |
| **TGA** | magick fallback | 🟡 pure-C# fast path (types 2/3/10/11) + Magick fallback |
| **QOI** | direct | ✅ pure-C# (QOI v1.0 spec) |
| **PPM family** (PBM/PGM/PPM/PFM) | direct | 🟡 pure-C# P1-P6; PAM via Magick |
| **CSV / Matrix / Matlab** | csv, matrix, mat (v5 + v7.3) | 🟡 CSV ✅; Matrix ✅; Matlab v5 read ✅; v7.3 (HDF5) ❌; Matlab write ❌ |
| **Radiance HDR** | direct | ✅ pure-C# |
| **FITS** | cfitsio | ✅ pure-C# (2D/3D, BSCALE/BZERO, single HDU) |
| **NIfTI** | niftiio | ✅ single-file `.nii` + paired `.hdr/.img`; 4D fMRI deferred |
| **Analyze 7.5** | direct | ❌ NIfTI-1 covers most use; pure Analyze rare |
| **JPEG XL** | libjxl | ❌ header stub only |
| **JPEG 2000** | libjp2k | ❌ header stub only |
| **OpenEXR** | OpenEXR library | ❌ |
| **OpenSlide** | openslide (whole-slide microscopy: SVS / NDPI / MRXS / VMS / VMU / SCN / MIRAX / TIFF) | ❌ |
| **dcraw** | dcraw camera-raw | ❌ |
| **uhdr** (Ultra HDR JPEG with gainmap) | libuhdr | ❌ |
| **DICOM** | via Magick | ❌ |

**Output-only / multi-resolution:**
| Format | libvips | Ours |
| :--- | :--- | :--- |
| **Pyramidal TIFF** (Ptif) | ✅ | ✅ via `SaveTiffAsync(pyramid: true)` |
| **dzsave** (Deep Zoom — DZI / Zoomify / IIIF / Google) | ✅ all 4 layouts | 🟡 DZI only |
| **APNG** (animated PNG) | via libspng | ✅ pure-C# saver |

---

## `freqfilt/` (~6 files in libvips)

Frequency-domain filtering.

| libvips op | Status |
| :--- | :---: |
| `fwfft` | ✅ `FwFft` |
| `invfft` | ✅ `InvFft` |
| `spectrum` (log-magnitude visualisation) | ✅ `Spectrum` (FFT-shifted) |
| `freqmult` (frequency-domain multiply with mask) | ❌ |
| `phasecor` (phase correlation — image registration) | ❌ |

---

## `histogram/` (~13 files in libvips)

| libvips op | Status |
| :--- | :---: |
| `hist_find` | ✅ |
| `hist_cum`, `hist_norm`, `hist_equal` | ✅ |
| `maplut` | ✅ |
| `hist_entropy` | ❌ |
| `hist_local` (CLAHE — contrast-limited adaptive histogram equalization) | ✅ | `HistLocal(tileGridSize=8, clipLimit=3.0)` — Pizer/Zuiderveld 1994. Per-tile clipped+redistributed CDF, bilinear blend across 4 surrounding tile-CDFs at each pixel. UChar only. Per-band (no Lab conversion) |
| `hist_match` (histogram matching against reference) | ❌ |
| `hist_plot` (visualise hist as image) | ❌ |
| `hist_ismonotonic` | ❌ |
| `case` (per-pixel select from band of LUTs) | ❌ |
| `percent` (find threshold for given percentage) | ❌ |
| `stdif` (statistical differencing — local-contrast enhancement) | ❌ |

---

## `morphology/` (~5 files in libvips)

| libvips op | Status |
| :--- | :---: |
| `morph` (dilate/erode dispatcher) | ✅ `Morph` with Float branch |
| `rank` | ✅ `Rank`/`Median` |
| `nearest` (distance to nearest non-zero pixel) | ❌ |
| `countlines` (count black-white transitions per scanline) | ❌ |
| `labelregions` (connected-component labeling) | ❌ |

---

## `mosaicing/` (~22 files in libvips)

**Whole subsystem missing.** Image stitching, panorama assembly,
luminosity balancing.

| libvips op | Status |
| :--- | :---: |
| `lrmerge` / `lrmosaic` (left-right) | ❌ |
| `tbmerge` / `tbmosaic` (top-bottom) | ❌ |
| `mosaic`, `mosaic1`, `remosaic` | ❌ |
| `match` (control-point match) | ❌ |
| `global_balance` | ❌ |
| `matrixinvert`, `matrixmultiply` | ❌ |
| `merge`, `chkpair`, `im_avgdxdy`, `im_clinear`, `im_improve`, `im_initialize`, `im_lrcalcon`, `im_tbcalcon` (legacy helpers) | ❌ |

---

## `resample/` (~19 files in libvips)

| libvips op | Status |
| :--- | :---: |
| `resize` (kernel-based, separable) | ✅ |
| `affine` | ✅ |
| `shrink` (integer box-average) | ✅ |
| `shrinkh` / `shrinkv` (axis-specific) | 🟡 covered by `Resize1D` |
| `reduce` / `reduceh` / `reducev` (non-integer downsample) | ✅ via `Resize` |
| `thumbnail` | ✅ (composes resize + autoorient + crop) |
| `similarity` (uniform scale + rotate + translate) | 🟡 covered by `Affine` |
| `mapim` (nonlinear remap via index image) | ❌ |
| `quadratic` (quadratic transform — used in lens correction) | ❌ |
| `transform` | 🟡 covered by `Affine` |
| `interpolate` (custom interpolator registration) | ❌ |
| Built-in kernels: nearest / linear / cubic / mitchell / lanczos2 / lanczos3 | ✅ + lanczos5 / hermite / bicubic-sharper / bicubic-smoother |
| `lbb` / `nohalo` / `vsqbs` (advanced edge-preserving interpolators) | ❌ |

---

## CosmoImage extensions (not in upstream libvips)

| Item | Notes |
| :--- | :--- |
| `TypedImage<TPixel>` | C# typed-pixel wrapper (`L8`/`La16`/`Rgb24`/`Rgba32`); `RowSpan(y)` reinterprets via `MemoryMarshal.Cast` |
| `Mutate(action)` | ImageSharp-style block-scoped fluent API |
| Float-throughout mainline | Linearize → Resize → Composite → Glow → Vignette → Delinearize end-to-end in Float |
| `IVipsAllocator` (`ArrayPool<byte>`) | Plumbed through `VipsRegion` and `OrderedStripSink` |
| Effects (`Vignette`, `Glow`, `Pixelate`, `OilPaint`, `Charcoal`, `Sketch`, `Polaroid`, `BokehBlur`) | Effects-pipeline ops on top of the libvips primitives |
| `EntropyCrop` | Greedy entropy-driven smartcrop variant |
| `Quantize` (Wu/median-cut) | Magick-backed quantizer |
| Pure-managed dependency stack | No libpng/libjpeg/libtiff native deps; alternative stack via JpegLibrary, StbImageSharp, Magick.NET-Q8, Docnet, MathNet |

---

## Status summary

By libvips subsystem, where ✅ = full or near-full, 🟡 = partial,
❌ = missing.

| Subsystem | Files in libvips | Our coverage |
| :--- | :---: | :---: |
| `iofuncs/` (engine) | 41 | 🟡 — core ported; profiling / reorder / disc-sink / target abstraction missing |
| `arithmetic/` | ~50 | 🟡 — math/boolean/relational/stats/hist_find/linear/invert; missing add/sub/mul/div, complex, hough, find_trim, measure |
| `conversion/` | ~40 | 🟡 — cast/extract/flip/rot/composite/recomb/gamma/autorot/smartcrop; ~25 ops missing (band ops, embed, flatten, premultiply, ifthenelse, switch, …) |
| `colour/` | ~40 | 🟡 — sRGB↔linear + RGB-matrix ops + ICC; ~30 colourspace converters missing (Lab, LabQ, LabS, LCh, UCS, Oklab, XYZ, Yxy, HSV, CMYK, scRGB, CICP, uhdr) plus dE metrics |
| `convolution/` | 14 | 🟡 — conv/convsep/gaussblur/sharpen; missing canny, compass, correlation family, conva variants |
| `morphology/` | 5 | 🟡 — morph/rank shipped; nearest/countlines/labelregions missing |
| `histogram/` | 13 | 🟡 — find/cum/equal/norm/maplut; ~8 missing (CLAHE, hist_match, percent, stdif, hist_entropy, …) |
| `freqfilt/` | 6 | 🟡 — fwfft/invfft/spectrum; freqmult and phasecor missing |
| `resample/` | 19 | 🟡 — resize/affine/shrink/thumbnail/reduce; mapim, quadratic, similarity, edge-preserving interpolators (nohalo/lbb/vsqbs) missing |
| `mosaicing/` | 22 | ❌ — entire subsystem missing (panorama / stitching / global balance) |
| `create/` | 30 | ❌ — only `text` covered; all generators (mask_*, perlin, worley, sdf, sines, identity LUT, …) missing |
| `draw/` | 7 | 🟡 — line/rect; circle/flood/mask/smudge missing |
| `foreign/` | ~70 | 🟡 — modern web formats covered (mostly via Magick); scientific (HDR/FITS/NIfTI/Matlab v5) covered pure-C#; gaps: OpenEXR, JPEG XL/2K full decode, OpenSlide, dcraw, uhdr, DICOM, Analyze, Matlab v7.3 |

---

*Last updated: 2026-05-02. 251 tests pass. Source files under `Core/`,
`Loaders/`, `Savers/`, `Operations/{Geometric,Color,Effects,Convolution,
Drawing,Analysis,Misc}/`. Upstream libvips counts from
`~/Downloads/libvips-master`.*
