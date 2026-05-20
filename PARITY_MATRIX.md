# Parity Matrix (CosmoImage vs upstream libvips)

Snapshot of where this library stands against the **upstream
libvips** C reference (~300 ops across 12 subsystems, 450 .c files).
Earlier versions of this matrix understated the gap by an order of
magnitude — this rewrite mirrors libvips' actual subsystem layout
(`arithmetic/`, `colour/`, `conversion/`, `convolution/`, `create/`,
`draw/`, `foreign/`, `freqfilt/`, `histogram/`, `iofuncs/`,
`morphology/`, `mosaicing/`, `resample/`).

Status legend: ✅ full · 🟢 production-ready · 🟡 partial · ❌ missing

> **⚠️ Doc-vs-reality note (Magick.NET removal):** This matrix predates
> the production-side removal of `Magick.NET`. Any cell below that reads
> "via Magick", "Magick-backed", or "Magick.NET-Q8" describes the **prior
> state**, not the current implementation. As of the removal:
>
> - **Pure-managed now (Magick path replaced):** SVG (full renderer with
>   shapes / paths / transforms / gradients incl. `xlink:href` chains /
>   clipPath / masks / text via CosmoFonts / filter chain), WebP VP8L
>   lossless encoder (palette + ColorTransform + LZ77 + boundary
>   package-merge Huffman), `IccTransform` (matrix/TRC + mAB/mBA + CMYK
>   + Lab + BPC + rendering intent + n-ink ≤ 8), quantizers
>   (`VipsOctreeQuantizer` / `VipsPaletteQuantizer` /
>   `VipsFloydSteinbergQuantizer`), artistic effects (`OilPaint` /
>   `Charcoal` / `Sketch` / `Polaroid`).
> - **Dropped (no replacement):** HEIF / AVIF (loaders return null,
>   savers throw — no pure-managed HEVC/AV1 codec), WebP VP8 lossy
>   load/save, DICOM.
>
> Individual line items below have **not** been retro-edited; they will
> be updated as the team revisits each subsystem. See `README.md`
> Dependencies section + `CONTRIBUTING.md` for the current policy.

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
| `abs`, `sin`, `cos`, `tan`, `atan`, `log`, `log10`, `exp`, `exp10`, `sqrt`, `sign`, `round`/`floor`/`ceil`/`rint` (math, math2, sign, round, unary) | ✅ | Full `Math` suite plus `Atan` (round 46) |
| `pow`, `wop`, `atan2` (math2 — binary math on two images) | ✅ | `Math2`/`Pow`/`Wop`/`Atan2` (round 46) — pixel-wise binary math; UChar branch clamps |
| `add`, `subtract`, `multiply`, `divide`, `remainder` (binary) | ✅ | `Add`/`Subtract`/`Multiply`/`Divide`/`Remainder` — UChar branch clamps and treats multiply as fraction-of-255; Float branch unclamped |
| `linear` (a·x + b per band) | ✅ | `Linear` with SIMD same-coefficient path |
| `invert` | ✅ | SIMD pointwise; libvips Float convention (`-x`) on Float input |
| `boolean`, `boolean_const` (and/or/xor/lshift/rshift) | ✅ | `Boolean2`, `BooleanConst` |
| `relational`, `relational_const` (eq/ne/lt/le/gt/ge) | ✅ | `Relational2`, `RelationalConst` |
| `complex`, `complex2`, `complexform`, `complexget` | ✅ | `Complex` (Polar/Rect/Conj), `Complex2`/`CrossPhase`, `ComplexForm`(re, im) → DPComplex, `ComplexGet` (Real/Imag/Magnitude/Phase) |
| `min`, `max`, `sum` (reductions) | ✅ via `Stats` | Per-band + aggregate min/max/sum/avg/deviate in one pass |
| `avg`, `deviate`, `stats` | ✅ | `Stats(image)` returns full result; `Avg`/`Min`/`Max`/`Deviate` shortcuts |
| `maxpair`, `minpair` (per-pixel max/min of two images) | ✅ | `MinImage(inputs…)` / `MaxImage(inputs…)` accept N inputs; `Sum(inputs…)` for additive composition |
| `getpoint` (extract single pixel as values) | ✅ | `Getpoint(input, x, y)` returns `double[]` of band values (UChar/UShort/Short/UInt/Int/Float/DPComplex) |
| `find_trim` (auto-find non-background bbox) | ✅ | `FindTrim(input, threshold, background)` — top-left pixel = default background; returns `VipsRect(0, 0, 0, 0)` when uniform |
| `measure` (extract patch averages from grid) | ✅ | `Measure(input, h, v, left, top, w, h)` — Float matrix of patch means; samples middle 80% of each cell to dodge edge bleed |
| `profile` (column/row first/last non-zero) | ✅ | `Profile(input)` returns (Columns: 1×W, Rows: 1×H) UInt images of first-non-zero coordinates per axis |
| `project` (sum-along-axis, both axes) | ✅ | `Project(input)` returns (Columns: 1×W Float, Rows: 1×H Float) per-axis sums |
| `hist_find`, `hist_find_indexed`, `hist_find_ndim` | ✅ | `HistFind`, `HistFindIndexed(input, index, reduction)` (Sum/Mean/Min/Max), `HistFindNDim(input, bins)` for 1/2/3-band UChar |
| `hough_circle`, `hough_line` | ✅ | `HoughLine(width, height, threshold)` and `HoughCircle(minRadius, maxRadius, threshold)` — UInt accumulator output |
| `clamp` | ✅ | `Clamp(input, min, max)` — UChar (byte-clamped) and Float (numeric-clamped) branches |

---

## `conversion/` (~40 files in libvips)

Format and layout conversions — band manipulation, embedding,
flattening, premultiplication. **Major gap area.**

| libvips op | Status | Our equivalent |
| :--- | :---: | :--- |
| `cast` (band-format conversion) | ✅ | `Cast`/`CastFloat`/`CastUChar` (UChar↔Float only) |
| `copy` | ✅ | `Copy(interpretation, bandFormat, bands, xRes, yRes, coding)` — pixel pass-through with metadata rewrites; band-format / band-count rewrites reinterpret bytes (pel size must match) |
| `extract_area` / `extract_band` | ✅ | `ExtractArea(left, top, w, h)` and `ExtractBand(band, n=1)` |
| `embed` (place into larger canvas with extension mode) | ✅ | `Embed` with Black/White/Copy/Repeat/Mirror/Background modes; per-band background colour |
| `gravity` (positional embed) | ✅ | `Pad(width, height, background, position)` with `VipsCompass` (Centre/N/E/S/W/NE/SE/SW/NW); `BackgroundColor(...)` flattens transparent pixels onto a fill colour while keeping alpha |
| `flip` | ✅ | |
| `rot` (orthogonal) / `rot45` | ✅ | `Rotate(VipsAngle)`; `Rot45(VipsAngle45)` (round 171) for 45°-increment rotation around the centre of a square odd-sided image. |
| `autorot` (EXIF-based) | ✅ | `AutoOrient` |
| `composite`, `composite2` | 🟢 | `Composite` with full Porter-Duff family (round 170): Clear / Source / Dest / Over / DestOver / In / DestIn / Out / DestOut / Atop / DestAtop / Xor / Add. Colour-modulation modes (Multiply / Screen / …) on `VipsBlend`. |
| `recomb` | ✅ | |
| `gamma` | ✅ | |
| `flatten` (alpha-flatten against background) | ✅ | `Flatten(r, g, b)` — composes RGBA/GA over an opaque background, drops alpha. UChar + Float branches |
| `premultiply` / `unpremultiply` | ✅ | `Premultiply` / `Unpremultiply` — UChar normalizes alpha by 255; Float treats alpha as nominal [0,1]. Pass-through on band counts without alpha |
| `addalpha` | ✅ | `AddAlpha(alpha=255)` — synthesise constant alpha plane and bandjoin. Pass-through if input already has alpha |
| `bandbool` (and/or/xor across bands) | ✅ | `Bandbool(op)` reduces input bands with AND/OR/XOR → single-band UChar |
| `bandfold` / `bandunfold` (W↔W*bands rearrange) | ✅ | `Bandfold(factor)` / `Bandunfold(factor)` — pure metadata reshape, default factor folds the whole row |
| `bandjoin` / `bandjoin_const` | ✅ | `Bandjoin(other, …)` and `BandjoinConst(c …)` — append constant bands without a synthetic source |
| `bandmean` (average all bands) | ✅ | `Bandmean()` — UChar (with rounding) and Float branches |
| `bandrank` (rank-statistic across bands) | ✅ | `Bandrank(inputs, index=-1)` — N inputs → per-pixel rank-statistic. Default median; UChar (insertion-sort) + Float branches |
| `byteswap` | ✅ | `Byteswap()` — reverses every multi-byte sample. UChar pass-through |
| `cache` (operation result cache) | ✅ | `Cache(input)` materialises once for DAG fan-out; internal op cache also runs |
| `falsecolour` | ✅ | `Falsecolor()` — built-in jet colour ramp; 1-band UChar → RGB |
| `grid` (lay tiles into grid) | ✅ | `Grid(tileHeight, across, down)` — tall N×tile stack → 2D grid. Trailing cells zero-filled |
| `ifthenelse` (per-pixel ternary) | ✅ | `Ifthenelse(then, else)` — UChar condition broadcasts (1-band) or selects per-band (N-band). UChar + Float then/else |
| `insert` (paste image at point) | ✅ | `Insert(sub, x, y, expand=false, background)` — expand=true grows output to the union bounding box |
| `join` (join two images side-by-side) | ✅ | `Join(other, direction, shim, align, background)` — optional linear-blend seam over `shim` pixels |
| `arrayjoin` (join N images in a grid) | ✅ | `Arrayjoin(inputs, across, shim, background, hAlign, vAlign)` — per-row max-height / per-column max-width geometry |
| `msb` (most-significant-byte extraction) | ❌ | |
| `replicate` (tile to bigger size) | ✅ | `Replicate(across, down)` — scanline-slab copy across tile seams |
| `scale` (linear stretch to 0..255) | ✅ | `Scale(log=false, exponent=0.25)` — linear or log-scale stretch to UChar; aggregate min/max via `VipsStats` |
| `sequential` (force sequential read order) | ✅ | `Sequential(input)` — sets `DemandHint = FatStrip` for top-to-bottom streaming saves |
| `subsample` | 🟡 | `Shrink` covers integer subsample |
| `switch` (case-style multi-image select) | ✅ | `Switch(tests…)` — index of first non-zero test image, N if none |
| `tilecache` (region cache) | ❌ | |
| `transpose3d` | ❌ | |
| `wrap` (toroidal shift) | ✅ | `Wrap(x, y)` — default offset centres the image; scanline-slab copy across the seam |
| `zoom` (integer scale-up by replication) | ✅ | `Zoom(xfac, yfac)` — nearest-neighbour pel→block enlarge |
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
| `XYZ2Lab`, `Lab2XYZ`, `XYZ2Yxy`, `Yxy2XYZ` | ✅ | `Lab2XYZ` / `XYZ2Lab` (D65) and `XYZ2Yxy` / `Yxy2XYZ` chromaticity coordinates |
| `Lab2LCh`, `LCh2Lab`, `LCh2UCS`, `UCS2LCh` | 🟡 | `Lab2LCh` ✅ and `LCh2Lab` ✅; `UCS` pair still missing |
| `Lab2LabQ` / `LabQ2Lab` (8-bit packed Lab) | ✅ | libvips' 4-byte LabQ layout: 10-bit L + 11-bit signed a + 11-bit signed b + extension byte |
| `Lab2LabS` / `LabS2Lab` (16-bit Lab) | ✅ | 3-band Short LabS — high-precision intermediate (L · 327.67, a/b · 256) |
| `LabQ2sRGB`, `LabQ2LabS`, `LabS2LabQ` | ❌ | |
| `XYZ2Oklab`, `Oklab2XYZ`, `Oklab2Oklch`, `Oklch2Oklab` | ✅ | `XYZ2OkLab` / `OkLab2XYZ` / `OkLab2OkLCh` / `OkLCh2OkLab` (Ottosson 2020 — D65 white maps to (1, 0, 0)) |
| `sRGB2HSV` / `HSV2sRGB` | ✅ | `SRGB2HSV` / `HSV2sRGB` — libvips' UChar packing (H ∈ [0, 255] for 0–360°) |
| `XYZ2CMYK` / `CMYK2XYZ` | ✅ | Naïve no-profile transform via sRGB-from-K. ICC profile path remains via `IccTransform` |
| `XYZ2scRGB` / `scRGB2XYZ` | ✅ | Standard sRGB-primary 3×3 matrix (D65); accepts negative / >1 values for HDR/wide-gamut |
| `CICP2scRGB` (ITU-R BT.2100 / Rec.2020 + transfer) | ❌ | HDR / wide-gamut |
| `uhdr2scRGB` (Ultra HDR JPEG gainmap → scRGB) | ❌ | Modern HDR-photo path |
| `float2rad` / `rad2float` (Radiance RGBE ↔ Float) | 🟡 | Built into `VipsHdrLoader`/`Saver` directly, not a standalone op |
| `dE76`, `dE00`, `dECMC` | ✅ | `DE76(other)` / `DE2000(other)` / `DECMC(other, l, c)` — all image + per-triplet APIs; CMC weights default to (2, 1) acceptability |
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
| `convsep` (separable mask) | ✅ | `Conv1D` (single-axis) and `ConvSep(kernel)` (composed two-axis) |
| `conva` / `convasep` (approximate large-kernel) | 🟡 | `BoxBlur(radius, passes)` covers the practical large-sigma case via running-sum box passes (3+ passes ≈ Gaussian); arbitrary-mask `conva` line-segment approximation still missing |
| `gaussblur` | ✅ | Two-pass `Conv1D` |
| `sharpen` | ✅ | `Sharpen(sigma, m1, m2, x1)` — luminance-band unsharp with separate shadow/highlight gains and dead-band threshold |
| `sobel` | ✅ | `Sobel()` — 3×3 Gx/Gy magnitude (UChar in/out) |
| `canny` | ✅ | `Canny(sigma, low, high)` — full pipeline: blur, Sobel, NMS, double-threshold, hysteresis |
| `compass` (compass-pattern edge) | ✅ | `Compass()` — 8 Kirsch rotations, max absolute response |
| `correlation`, `fastcor`, `spcor` | ✅ `spcor`, ❌ `fastcor` | `Spcor(reference)` — Pearson NCC (UChar 1-band), result mapped [-1, 1] → [0, 255]; FFT-accelerated `fastcor` still missing |
| `edge` | ✅ | `Edge(input, method)` dispatcher (Sobel / Compass / Canny) |

---

## `create/` (~30 files in libvips)

Generators. **Whole subsystem missing** apart from `text`.

| libvips op | Status |
| :--- | :---: |
| `black` (constant 0 image) | ✅ | `Black(width, height, bands, format)` |
| `xyz` (per-pixel x/y coordinate image — useful for mapim) | ✅ | `Xyz(width, height, csize, dsize, esize)` — UInt 2-band default; extra dims roll into bands |
| `eye`, `grey`, `zone` (test-pattern generators) | ✅ | `Eye`, `Zone`, `Grey(width, height, uchar=false)` |
| `gaussmat`, `logmat`, `gaussnoise` (filter mask generators) | ✅ | `Gaussmat(sigma, minAmpl, separable)`, `Logmat(sigma, minAmpl)`, `Gaussnoise(width, height, mean, sigma, seed)` (Box-Muller) |
| `mask_butterworth`, `mask_butterworth_band`, `mask_butterworth_ring` | 🟡 | `MaskButterworthLowpass` / `MaskButterworthHighpass` / `MaskButterworthRing` ✅; `_band` (directional) still missing |
| `mask_gaussian`, `mask_gaussian_band`, `mask_gaussian_ring` | 🟡 | `MaskGaussianLowpass` / `MaskGaussianHighpass` / `MaskGaussianRing` ✅; `_band` (directional) still missing |
| `mask_ideal`, `mask_ideal_band`, `mask_ideal_ring` | 🟡 | `MaskIdealLowpass` / `MaskIdealHighpass` ✅; band / ring variants still missing |
| `mask_fractal` | ✅ | `MaskFractal(width, height, fractalDimension)` — 1/fᵅ centred mask; pair with `Gaussnoise` + `Freqmult` for spectrally-shaped noise |
| `fractsurf` (fractal surface) | ✅ | `Fractsurf(width, height, octaves, baseCellSize, fractalDimension, seed)` — sum of Perlin octaves at successive frequencies |
| `perlin`, `worley`, `sines` | ✅ | `Sines`, `Perlin(width, height, cellSize, seed)` (Perlin 2002 fade curve), `Worley` (F1 distance, deterministic per-cell hash) |
| `point` (sample image at point) | ❌ |
| `sdf` (signed distance field generator) | ✅ | `SdfCircle` / `SdfBox` / `SdfRoundedBox` — Float distance field; threshold at 0 for crisp shapes, smoothstep around 0 for AA edges |
| `tonelut`, `buildlut`, `invertlut` (LUT builders) | ✅ | `BuildLut(points)` piecewise-linear; `Invertlut` monotonic-LUT inverse; `Tonelut(shadows, midtones, highlights)` photographer-friendly tone curve |
| `identity` (identity LUT) | ✅ | `Identity(bands, ushort_, size)` |
| `text` (glyph rendering) | 🟡 `Text` (rudimentary, no proper shaping) |

---

## `draw/` (~7 ops in libvips)

In-place pixel drawing onto a memory-backed image.

| libvips op | Status |
| :--- | :---: |
| `draw_line` | ✅ `DrawLine` (Xiaolin Wu antialiased) |
| `draw_rect` | ✅ `DrawRect` (outline + fill) |
| `draw_circle` | ✅ | `DrawCircle(input, cx, cy, radius, ink, fill)` — Bresenham outline or span-fill |
| `draw_flood` (flood fill) | ✅ | `DrawFlood(input, x, y, ink)` — 4-connected scanline flood (Smith 1979) |
| `draw_image` (paste image at point) | ✅ | `DrawImage(input, sub, x, y)` — output stays input-sized; clips at edges |
| `draw_mask` (draw with alpha mask) | ✅ | `DrawMask(input, mask, x, y, ink)` — UChar single-band alpha; per-pixel blend |
| `draw_smudge` | ✅ | `DrawSmudge(input, x, y, w, h)` — 3×3 local-average soft erase |

---

## `foreign/` (~70 files in libvips — loaders + savers)

| Format | libvips coverage | Our coverage |
| :--- | :--- | :--- |
| **JPEG** | full + EXIF/XMP/ICC + progressive + arith | ✅ pure-C# decoder + multi-segment ICC + JFIF YCbCr→RGB / Adobe APP14 RGB / YCCK / CMYK colourspace handling |
| **PNG** | libpng / libspng | ✅ pure-C# `PurePngDecoder` (8/16-bit, color types 0/2/3/4/6, Adam7 interlace, tRNS); StbImageSharp fallback for malformed streams |
| **TIFF** | libtiff (huge variant matrix) | ✅ pure-managed `PureTiffDecoder` — uncompressed + LZW + Deflate (zlib + raw fallback) + PackBits + JPEG-in-TIFF (compression=7); predictor=2/3; multi-page IFD chain; tiled layout; tiled+planar=2 combo; BigTIFF (8-byte offsets); FillOrder=2 accepted; SampleFormat 1/2/3 (UChar/UShort/UInt/Char/Short/Int/Float); CMYK photometric; YCbCr photometric (=6) for JPEG-in-TIFF |
| **WebP** | libwebp animated | 🟡 pure VP8L lossless decoder (round 113) — full bitstream, all four transforms (predictor / cross-color / subtract-green / color-indexing), color cache, LZ77, meta-Huffman; VP8X-wrapped VP8L. VP8 lossy + animated still via Magick |
| **HEIF / AVIF** | libheif single + sequence | 🟡 via Magick; animated load works, save single-frame only |
| **GIF** | nsgif animated | 🟡 pure-C# `PureGifDecoder` for stills; animated still via Magick |
| **PDF** | poppler / pdfium | 🟡 via Docnet |
| **SVG** | librsvg | 🟡 via Magick |
| **BMP** | libvips + magick fallback | ✅ pure-C# decoder — 1/4/8 bpp paletted (BI_RGB), 16 bpp RGB555 / BI_BITFIELDS, 24/32 bpp BGR(A), BI_RLE8, BI_RLE4 (Round 134); V4/V5 colour-space variants pass through (extra header fields ignored) |
| **TGA** | magick fallback | ✅ pure-C# decoder — types 1/9 (uncompressed/RLE paletted with 15/16/24/32-bit colour map), types 2/10 (uncompressed/RLE truecolor at depth 15/16/24/32), types 3/11 (uncompressed/RLE grayscale) (Round 144) |
| **QOI** | direct | ✅ pure-C# (QOI v1.0 spec) |
| **PPM family** (PBM/PGM/PPM/PAM) | direct | ✅ pure-C# full Netpbm matrix — PBM (P1/P4), PGM (P2/P5), PPM (P3/P6), PAM (P7) at 8 and 16 bits per sample (Round 145) |
| **CSV / Matrix / Matlab** | csv, matrix, mat (v5 + v7.3) | 🟡 CSV ✅; Matrix ✅; Matlab v5 read ✅; v7.3 (HDF5) ❌; Matlab write ❌ |
| **Radiance HDR** | direct | ✅ pure-C# — new-style RLE + old-style RLE (Round 146); all four Y-first axis orderings (Round 153); X-first 90° rotations rejected |
| **FITS** | cfitsio | ✅ pure-C# (2D/3D, BSCALE/BZERO, single HDU) |
| **NIfTI** | niftiio | ✅ single-file `.nii` + paired `.hdr/.img`; 4D fMRI deferred |
| **APNG** | via libspng | ✅ pure-C# decoder + saver — supports both fcTL-before-IDAT and IDAT-as-fallback layouts (Round 151) |
| **OpenEXR** | OpenEXR library | 🟢 pure-C# `PureExrDecoder` — single-part scanline + tiled (ONE_LEVEL / MIPMAP / RIPMAP, level 0 exposed); multi-part (first-image-part); compressors NO_COMPRESSION / RLE / ZIPS / ZIP / PIZ / PXR24 / B44 / B44A / DWAA-DWAB-partial; HALF / FLOAT / UINT pixel types; arbitrary 1-4 channel sets (RGB[A] / Y / Z / U / V / arbitrary). DWA RGB-CSC + libimf-DC-encoding outstanding |
| **Analyze 7.5** | direct | ❌ NIfTI-1 covers most use; pure Analyze rare |
| **JPEG XL** | libjxl | ❌ header stub only |
| **JPEG 2000** | libjp2k | ❌ header stub only |
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
| `freqmult` (frequency-domain multiply with mask) | ✅ | `Freqmult(mask)` — FwFft → real-mask multiply → InvFft preserving Float output |
| `phasecor` (phase correlation — image registration) | ❌ |

---

## `histogram/` (~13 files in libvips)

| libvips op | Status |
| :--- | :---: |
| `hist_find` | ✅ |
| `hist_cum`, `hist_norm`, `hist_equal` | ✅ |
| `maplut` | ✅ |
| `hist_entropy` | ✅ | `HistEntropy()` returns per-band Shannon entropy plus aggregate (bits) |
| `hist_local` (CLAHE — contrast-limited adaptive histogram equalization) | ✅ | `HistLocal(tileGridSize=8, clipLimit=3.0)` — Pizer/Zuiderveld 1994. Per-tile clipped+redistributed CDF, bilinear blend across 4 surrounding tile-CDFs at each pixel. UChar only. Per-band (no Lab conversion) |
| `hist_match` (histogram matching against reference) | ✅ | `HistMatch(reference)` — per-band CDF remap. Computes both histograms and the matching LUT, applies in one pass |
| `hist_plot` (visualise hist as image) | ✅ | `HistPlot(input, height)` — bar-chart visualisation; multi-band per-band rendering |
| `hist_ismonotonic` | ❌ |
| `case` (per-pixel select from band of LUTs) | ✅ | `Case(index, cases…)` — picks <c>cases[index]</c> per pixel; out-of-range falls back to last source |
| `percent` (find threshold for given percentage) | ✅ | `Percent(percent)` — bin at which percentile of aggregate histogram is reached |
| `stdif` (statistical differencing — local-contrast enhancement) | ✅ | `Stdif(window, sigmaTarget, meanTarget, a)` — summed-area-table per-pixel renormalisation |

---

## `morphology/` (~5 files in libvips)

| libvips op | Status |
| :--- | :---: |
| `morph` (dilate/erode dispatcher) | ✅ `Morph` with Float branch |
| `rank` | ✅ `Rank`/`Median` |
| `nearest` (distance to nearest non-zero pixel) | ✅ | `Nearest()` — exact Euclidean distance via Felzenszwalb-Huttenlocher separable EDT (UChar clamped) |
| `countlines` (count black-white transitions per scanline) | ✅ | `Countlines(direction)` — average transitions per row or column |
| `labelregions` (connected-component labeling) | ✅ | `LabelRegions()` — 4-connected union-find, two-pass; UInt label image (1..K, 0 = background) |

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
| `similarity` (uniform scale + rotate + translate) | ✅ | `Similarity(scale, angle, idx, idy)` — thin wrapper over `Affine` with the constrained parameter set |
| `mapim` (nonlinear remap via index image) | ✅ | `Mapim(input, index)` — Float 2-band coordinate image, bilinear sampling, configurable background fill |
| `quadratic` (quadratic transform — used in lens correction) | ✅ | `Quadratic(input, coefficients[12])` — second-order polynomial coordinate warp, bilinear sampling |
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
| `foreign/` | ~70 | 🟢 — most common formats (JPEG / PNG / TIFF / GIF-still / BMP / TGA / QOI / PNM / HDR / APNG / OpenEXR / FITS / NIfTI) decode pure-managed; WebP-lossless pure / lossy-via-Magick; HEIF / AVIF / SVG / PDF via native deps; gaps: WebP-VP8-lossy, JPEG XL/2K full decode, OpenSlide, dcraw, uhdr, DICOM, Analyze, Matlab v7.3 |

---

*Last updated: 2026-05-05. 1413 tests pass. Source files under `Core/`,
`Loaders/`, `Savers/`, `Operations/{Geometric,Color,Effects,Convolution,
Drawing,Analysis,Misc}/`. Upstream libvips counts from
`~/Downloads/libvips-master`.*
