# Parity TODO (vs upstream libvips)

Honest accounting of remaining work to reach upstream libvips parity.
Reorganised by libvips' own subsystem layout after surveying the
reference source at `~/Downloads/libvips-master`. The earlier "13-item
TODO" framing reflected an internal-flavor checklist — the real gap is
hundreds of ops across a dozen subsystems.

What this document is for: a structured map of remaining work, grouped
so each section is independently actionable and honest about size.
Tier numbers are gone — they over-promised. Replaced with **scope
classes** (mechanical / op-set / subsystem / native-binding) that
describe what kind of work each gap is.

For what's already shipped, see `PARITY_MATRIX.md`. The section
headers below mirror libvips' subsystem directories.

---

## Mechanical follow-ups (per-op, days each)

Single ops or tight clusters that fit the existing dispatch pattern.
Each lands in a single PR.

### `arithmetic/`
- [x] ~~`add` / `subtract` / `multiply` / `divide` / `remainder`~~
  (round 26) — `VipsArithmetic2` covers all five. UChar clamps and
  treats multiply as fraction-of-255; Float unclamped, direct multiply.
- [x] ~~`linear_const`~~ (round 46) — broadcast-scalar wrapper around
  `Linear` for the common single-value case.
- [x] ~~`sign` / `floor` / `ceil` / `rint`~~ (round 45) — extended
  `VipsMath` enum + UChar / Float branches. Exposed as
  `Sign`/`Floor`/`Ceil`/`Rint` on `VipsImageOps`.
- [x] ~~`complex` / `complex2` / `complexform` / `complexget`~~
  (round 45) — full DPComplex op surface. `Complex` (Polar / Rect /
  Conj), `CrossPhase` (= `Complex2`), `ComplexForm`(re, im) and
  `ComplexGet` (Real / Imag / Magnitude / Phase).
- [x] ~~`clamp`~~ (round 46) — per-band clamp to [min, max] (UChar +
  Float branches).
- [x] ~~`atan` / `math2` (Pow / Wop / Atan2)~~ (round 46) — single-arg
  arctan in `VipsMath`; pixel-wise binary math on two images via
  `VipsMath2`.
- [x] ~~`measure`~~ (round 46) — patch-grid mean sampler for
  colour-chart calibration; samples middle 80% of each cell.
- [x] ~~`sum`~~ (round 44) — pixel-wise sum across N images; UChar
  branch clamps at 255, Float branch is unclamped.
- [x] ~~`maxpair` / `minpair`~~ (round 44) — `MinImage` / `MaxImage`
  accept N inputs, not just two. Per-band, per-pixel reduction.
- [x] ~~`project`~~ (round 44) — per-axis sum reduction (column-sums,
  row-sums) → 1D Float images.
- [x] ~~`find_trim`~~ (round 44) — auto-find non-background bbox;
  defaults to top-left pixel as background.
- [x] ~~`getpoint`~~ (round 48) — `Getpoint(input, x, y)` returns
  `double[]`; supports UChar/UShort/Short/UInt/Int/Float/DPComplex.
- [x] ~~`hist_find_ndim`~~ (round 48) — N-dim histogram for 1/2/3-band
  UChar inputs; UInt accumulator output.
- [x] ~~`profile`~~ (round 48) — per-axis first-non-zero coordinate
  profile (Columns + Rows UInt images).

### `conversion/`
- [x] ~~`bandjoin`~~ (round 26) — `Bandjoin(other, …)` for N inputs.
- [x] ~~`bandbool`~~ (round 28) — AND/OR/XOR fold across bands (UChar).
- [x] ~~`bandmean`~~ (round 28) — average bands; UChar (rounded) + Float.
- [x] ~~`bandfold` / `bandunfold`~~ (round 29) — pure metadata reshape;
  default factor folds the whole row.
- [x] ~~`bandjoin_const`~~ (round 29) — append per-band constants.
- [x] ~~`bandrank`~~ (round 30) — rank-statistic across N inputs;
  default index = N/2 (median).
- [x] ~~`addalpha`~~ (round 27) — synthesise constant-fill alpha plane and
  bandjoin. Pass-through if input already has alpha.
- [x] ~~`flatten`~~ (round 27) — composes RGBA/GA over an opaque background
  colour, drops alpha. UChar + Float branches.
- [x] ~~`premultiply` / `unpremultiply`~~ (round 26) — alpha-correct
  compositing primitives. UChar normalizes alpha by 255; Float treats
  alpha as nominal [0,1].
- [x] ~~`embed`~~ (round 26) — Black/White/Copy/Repeat/Mirror/Background
  extension modes; per-band background colour for the
  `Background` mode.
- [x] ~~`gravity`~~ (round 27) — `Pad(width, height, background, position)`
  with `VipsCompass` (Centre/N/E/S/W/NE/SE/SW/NW). Plus `BackgroundColor`
  for flatten-onto-fill while keeping alpha.
- [x] ~~`replicate`~~ (round 28) — tile across×down. Scanline-slab copy
  across tile seams.
- [x] ~~`rot45`~~ (round 171) — 8-angle (D0..D315) rotation around
  the centre of a square odd-sided image. Axis-aligned angles match
  `VipsRotate`; diagonals use rotation-matrix sampling with nearest-
  neighbour, zero-fill out-of-bounds.
- [x] ~~`byteswap`~~ (round 30) — reverse multi-byte sample bytes;
  UChar pass-through.
- [x] ~~`falsecolour`~~ (round 28) — built-in jet ramp, 1-band UChar → RGB.
- [x] ~~`ifthenelse`~~ (round 28) — per-pixel ternary; UChar condition
  broadcasts or selects per-band, UChar + Float then/else.
- [x] ~~`switch`~~ (round 32) — index of first non-zero test, N if none.
- [x] ~~`case`~~ (round 32) — pick from N source images by UChar index;
  out-of-range falls back to last source.
- [x] ~~`wrap`~~ (round 29) — toroidal shift; default offset centres the
  image, scanline-slab copy across the seam.
- [x] ~~`zoom`~~ (round 29) — integer scale-up by replication
  (nearest-neighbour pel→block).
- [x] ~~`scale`~~ (round 29) — linear or log-scale stretch to UChar
  0..255; aggregate min/max via `VipsStats`.
- [x] ~~`extract_band`~~ (round 28) — pull N consecutive bands from offset.
- [x] ~~`grid`~~ (round 30) — tall N×tile stack → 2D grid; trailing
  cells zero-filled.
- [x] ~~`arrayjoin` / `join` / `insert`~~ (round 37) — `Arrayjoin` lays
  N inputs out into a grid; `Join` pastes two images side-by-side
  with optional linear-blend seam; `Insert` pastes sub into base at
  (x, y), with optional `expand` to grow the output to the union
  bounding box.

### `convolution/`
- [x] ~~`sharpen`~~ (round 31) — luminance-only unsharp with separate
  shadow/highlight gains (m1/m2) and dead-band (x1).
- [x] ~~`sobel`~~ (round 31) — 3×3 Gx/Gy magnitude (UChar in/out).
- [x] ~~`canny`~~ (round 31) — full pipeline: Gaussian blur, Sobel,
  non-max suppression, double-threshold, hysteresis.
- [x] ~~`compass`~~ (round 31) — 8 Kirsch rotations, max absolute
  response.
- [x] ~~`spcor`~~ (round 32) — Pearson NCC (UChar 1-band), result mapped
  [-1, 1] → [0, 255].
- [x] ~~`fastcor`~~ (round 174) — FFT-accelerated cross-correlation
  via the convolution theorem (<c>FFT(in)·conj(FFT(ref))→IFFT</c>).
  UInt 1-band output, raw <c>Σ in·ref</c> (not normalised). Use over
  `spcor` when speed matters and brightness/contrast match.
- [x] ~~box-pass approximation~~ (round 49) — `BoxBlur(radius, passes)`
  via running-sum: O(W·H) per pass regardless of radius; 3+ passes
  approximate a Gaussian (central-limit theorem). Arbitrary-mask
  `conva` line-segment approximation still missing.
- [x] ~~`convsep`~~ (round 49) — separable two-axis convolution
  via composed Conv1D.
- [x] ~~`edge`~~ (round 49) — generic edge-detector dispatcher
  (Sobel / Compass / Canny).

### `morphology/`
- [x] ~~`nearest`~~ (round 31) — exact Euclidean distance transform
  via Felzenszwalb-Huttenlocher 1D parabola-envelope (separable).
- [x] ~~`labelregions`~~ (round 31) — 4-connected union-find,
  two-pass; UInt label image (1..K, 0 = background).
- [x] ~~`countlines`~~ (round 32) — average black/white transitions per
  row (or column).

### `histogram/`
- [x] ~~`hist_local`~~ (round 27) — CLAHE (Pizer/Zuiderveld 1994).
  Per-tile clipped+redistributed CDF, bilinear blend across 4
  surrounding tile-CDFs at each pixel. UChar only, per-band.
- [x] ~~`hist_match`~~ (round 30) — per-band CDF remap; computes both
  histograms, builds the matching LUT, applies in one pass.
- [x] ~~`hist_entropy`~~ (round 30) — per-band Shannon entropy +
  aggregate, in bits.
- [x] ~~`percent`~~ (round 30) — threshold below which a given percentile
  of the aggregate histogram lies.
- [x] ~~`stdif`~~ (round 32) — local-contrast renormalisation via
  summed-area tables; targets a configurable mean and sigma.
- [x] ~~`hist_plot`~~ (round 47) — bar-chart visualisation; per-band
  rendering for multi-band histograms.
- [x] ~~`hist_find_indexed`~~ (round 47) — per-bin reduction
  (Sum / Mean / Min / Max) keyed by an index image.
- [x] ~~`hough_line` / `hough_circle`~~ (round 47) — line / circle
  Hough transforms; Bresenham-pre-computed circle offsets share work
  across edge pixels.

### `freqfilt/`
- [x] ~~`freqmult`~~ (round 32) — FwFft → real-mask multiply → InvFft;
  preserves Float output.
- [x] ~~`phasecor`~~ (round 173) — whitened cross-power spectrum
  <c>FFT(a)·conj(FFT(b))/|…|</c>, IFFT'd; peak coordinate is the
  translation aligning the inputs (modulo image size). Brightness/
  contrast invariant via the whitening step.

### `resample/`
- [x] ~~`mapim`~~ (round 42) — Float 2-band coordinate index image,
  bilinear sampling, configurable background fill.
- [x] ~~`quadratic`~~ (round 42) — 2D quadratic-polynomial coordinate
  warp; coefficients = [a0..a5, b0..b5].
- [x] ~~`similarity`~~ (round 42) — uniform scale + rotate + translate;
  thin wrapper over `Affine`.
- [ ] Edge-preserving interpolators: `nohalo`, `lbb`, `vsqbs`.

### `draw/`
- [x] ~~`draw_circle`~~ (round 43) — Bresenham outline or span-fill.
- [x] ~~`draw_flood`~~ (round 43) — 4-connected scanline flood
  (Smith 1979).
- [x] ~~`draw_image`~~ (round 43) — paste sub-image at point, output
  stays input-sized.
- [x] ~~`draw_mask`~~ (round 43) — UChar single-band alpha mask;
  per-pixel blend.
- [x] ~~`draw_smudge`~~ (round 43) — 3×3 local-average soft erase
  over a rectangular region.

---

## Op-set work (multi-day, structured ports)

Coherent op clusters that belong together. Each is days-to-a-week.

### Colour-management graph (`colour/`)
The biggest single op-set gap. libvips supports the full graph between
sRGB ↔ scRGB ↔ Lab ↔ LabQ ↔ LabS ↔ LCh ↔ UCS ↔ XYZ ↔ Yxy ↔ HSV ↔ CMYK
↔ Oklab ↔ Oklch, plus CICP and uhdr at the edges. We have only
sRGB↔linear and a few RGB-space matrix manipulations.

- [ ] XYZ ↔ Lab, Lab ↔ LCh, LCh ↔ UCS — the CIE colourimetry chain.
- [x] ~~Lab ↔ LabQ~~ (round 34) — libvips 4-byte layout
  (10-bit L + 11-bit signed a + 11-bit signed b + extension byte).
- [x] ~~Lab ↔ LabS~~ (round 34) — 3-band Short
  high-precision intermediate (L · 327.67, a/b · 256).
- [x] ~~XYZ ↔ Yxy~~ (round 34) — chromaticity coordinates.
- [x] ~~XYZ ↔ Oklab, Oklab ↔ Oklch~~ (round 35) — Ottosson 2020;
  D65 white maps to (1, 0, 0).
- [x] ~~sRGB ↔ HSV~~ (round 35) — libvips UChar packing
  (H ∈ [0, 255] for 0–360°).
- [x] ~~XYZ ↔ CMYK~~ (round 36) — naïve no-profile transform via
  sRGB-from-K. ICC-based path remains via `IccTransform`.
- [x] ~~XYZ ↔ scRGB~~ (round 36) — standard sRGB-primary 3×3 matrix.
- [x] ~~CICP2scRGB~~ (round 175) — `VipsCicp2scRGB` with
  `VipsCicpPrimaries` (BT.709, BT.2020) × `VipsCicpTransfer`
  (BT.709, Linear, BT.2020, PQ, HLG). Float scRGB output. PQ EOTF
  scales /100 so SDR diffuse white aligns with scRGB ≈ 1.0; HLG
  inverse-OETF is scene-referred.
- [ ] uhdr2scRGB (Ultra HDR JPEG with gainmap).
- [x] ~~dE76 / dE00~~ (round 33) — `DE76` Euclidean Lab and `DE2000`
  CIEDE2000 (Sharma reference vectors verified). Also exposed as
  per-triplet `DE2000(L1, a1, b1, L2, a2, b2)`.
- [x] ~~dECMC~~ (round 36) — CMC(l:c) acceptability/perceptibility ΔE
  with reference-weighted SL/SC/SH; image + per-triplet APIs.
- [x] ~~Lab ↔ XYZ~~ (round 33) — D65 white point.
- [x] ~~Lab ↔ LCh~~ (round 33) — polar form.
- [ ] Pipeline-aware ICC: profile attached to image metadata, transform
  applied at sink boundary rather than as a one-shot. Currently
  `IccTransform` is a one-shot Magick call.

### Image generators (`create/`)
Whole subsystem missing apart from `Text`. Each is a small standalone
generator that produces an image from parameters.

- [x] ~~`black`~~ (round 38) — all-zero image of any size/bands/format.
- [x] ~~`xyz`~~ (round 38) — UInt 2-band (or more, with C/D/E sizes)
  coordinate image; input to `mapim`-style remap.
- [x] ~~`eye`~~ (round 40) — horizontal frequency chirp × vertical
  amplitude ramp.
- [x] ~~`zone`~~ (round 40) — concentric cos(r²) zone-plate, the
  canonical resize-aliasing diagnostic.
- [x] ~~`grey`~~ (round 48) — horizontal 0..1 (Float) or 0..255 (UChar)
  ramp; constant down columns.
- [x] ~~`gaussmat`~~ (round 38) — Float matrix kernel image; auto-sized
  by `min_ampl` cutoff.
- [x] ~~`logmat`~~ (round 39) — Laplacian-of-Gaussian Float kernel.
- [x] ~~`gaussnoise`~~ (round 39) — Box-Muller; deterministic seed.
- [x] ~~`mask_ideal`~~ (round 40) — `MaskIdealLowpass` /
  `MaskIdealHighpass` (centred Float masks for use with `Freqmult`).
- [x] ~~`mask_gaussian`~~ (round 41) — `MaskGaussianLowpass` /
  `MaskGaussianHighpass` / `MaskGaussianRing`. Smooth fall-off
  avoids the spatial-domain ringing of ideal masks.
- [x] ~~`mask_butterworth`~~ (round 41) — `MaskButterworthLowpass` /
  `MaskButterworthHighpass` / `MaskButterworthRing`. Adjustable
  rolloff via the `order` parameter.
- [x] ~~`mask_fractal`~~ (round 41) — 1/fᵅ centred mask; pair with
  `Gaussnoise` + `Freqmult` for spectral fractal-noise synthesis.
- [x] ~~`fractsurf`~~ (round 40) — sum of Perlin octaves at successive
  frequencies; configurable fractal dimension.
- [x] ~~`mask_*_band`~~ (round 172) — directional band-pass variants
  for Gaussian / Butterworth / Ideal mask families. Two symmetric peaks
  at <c>(±frequencyX·W/2, ±frequencyY·H/2)</c> preserve real-FFT
  conjugate symmetry; pair with `Freqmult` for orientation-selective
  filtering.
- [x] ~~`sines`~~ (round 38) — Float sinusoid pattern; frequencies in
  cycles per image.
- [x] ~~`perlin`~~ (round 39) — Perlin 2002 fade curve;
  deterministic seed.
- [x] ~~`worley`~~ (round 39) — F1 distance, deterministic per-cell hash.
- [x] ~~`sdf`~~ (round 39) — `SdfCircle` / `SdfBox` / `SdfRoundedBox`
  Float distance fields.
- [x] ~~`buildlut`~~ (round 38) — piecewise-linear LUT from anchor
  points; multi-band when each anchor carries multiple y values.
- [x] ~~`identity`~~ (round 38) — identity LUT (256-wide UChar; or
  65536-wide UShort with `ushort_: true`).
- [x] ~~`invertlut`~~ (round 39) — invert a monotonic 1D LUT.
- [x] ~~`tonelut`~~ (round 40) — three-knob photographer-friendly tone
  curve (shadows / midtones / highlights).
- [ ] `point` (remaining LUT scaffolding).

### Composite mode parity
- [x] ~~Porter-Duff family on `VipsComposite`~~ (round 170) — Clear,
  Source, Dest, Over, DestOver, In, DestIn, Out, DestOut, Atop,
  DestAtop, Xor, plus Plus/Add. Premultiplied per-pixel math, both
  UChar and Float paths. The W3C colour-modulation modes
  (Multiply, Screen, Overlay, …) live on `VipsBlend` separately.

---

## Subsystem-scale work (week-to-month each)

Bigger than a week. Each is its own focused project.

### Mosaicing (`mosaicing/`, ~22 files)
Whole subsystem missing. Image stitching and panorama assembly:
control-point detection (`match`), pair merging (`lrmerge` / `tbmerge`),
recursive mosaicing (`lrmosaic` / `tbmosaic`), global luminosity balance
(`global_balance`), matrix-inversion-based remosaic. Substantial own
project — corresponding to libvips' early scientific-imaging heritage.

### `iofuncs/` engine extensions
- [x] ~~**Output target abstraction**~~ (round 185) — `IVipsTarget`
  interface symmetric to `IVipsSource`, plus three implementations:
  `MemoryVipsTarget` (collects into a buffer; <c>ToArray()</c>),
  `StreamVipsTarget` (wraps any writable Stream), and
  `CallbackVipsTarget` (forwards each write to a user delegate —
  the libvips "custom write callback" target). Existing savers
  keep their PipeWriter signatures unchanged; callers using
  IVipsTarget bridge via <c>target.AsPipeWriter()</c>.
- [ ] **Disc-backed sink** (`sinkdisc.c`). For images too big to
  materialize in memory, libvips writes a temporary tiled file and
  reads back per-tile. Closes the "what about a 50000×50000-pixel
  WSI?" use case.
- [ ] **Op-tree reordering** (`reorder.c`). Memory-locality-aware
  ordering of pipeline stages.
- [x] ~~**Profiling / gating**~~ (round 184) — `VipsProfiler` is a
  per-op-type runtime profiler wired into <c>VipsImageOps.Run</c>.
  Off by default; toggle <c>Enabled</c> to accumulate
  <c>(CallCount, TotalElapsed)</c> per <c>VipsOperation</c> type.
  <c>Snapshot()</c> returns a defensive copy with derived
  <c>TotalMilliseconds</c> / <c>AverageMilliseconds</c> for ranking
  slow stages.
- [x] ~~**LRU operation cache**~~ (round 177) — `VipsCache` now uses
  proper LRU (LinkedList + Dictionary) with cost-based eviction.
  Cost estimated as `W·H·SizeOfPel`; cap defaults to 256 MiB,
  tunable via `SetMaxCost`. Setting cap to 0 disables caching.

### Native-format pure-C# ports
Each is days-to-weeks per format, replacing the corresponding
Magick.NET dependency:
- [ ] **TIFF** — vast variant matrix; libtiff is huge. Probably
  weeks. Most-used "drop Magick" target after PNG.
- [ ] **GIF** — LZW + GCE + animation extension blocks. ~600-700
  lines.
- [ ] **WebP** — VP8 / VP8L bitstream parsers. Significant.
- [ ] **HEIF / AVIF** — ISOBMFF box parser + AV1 / HEVC bitstream
  decoder. Out of reach without libheif / libaom; gated on managed
  AV1 decoder availability.
- [ ] **SVG** — full vector renderer, not a codec. Likely permanent
  Magick dep (out of scope to port the rendering engine).

### `Image<TPixel>` generic op surface
The `TypedImage<TPixel>` access layer is shipped. Making *every* op
signature generic in `TPixel` is the architectural piece. Doesn't
translate cleanly to the lazy-pipeline model where ops produce new
images, so likely better as a parallel typed API rather than
replacing the existing one. Substantial undertaking — touches every
op signature.

---

## Native-dependency-bound (months, or never)

Items that genuinely can't be done in pure-C# without a native binding
the .NET ecosystem doesn't have.

- [ ] **Proper ICC color management** (LittleCMS binding). Current
  `IccTransform` uses Magick.NET as a one-shot transform; real CMM
  workflow keeps source profile attached through ops, transforms at
  sink boundary. Needs LittleCMS via P/Invoke.
- [ ] **JPEG XL** full pixel decode (libjxl).
- [ ] **JPEG 2000** full pixel decode (libjp2k or OpenJPEG).
- [x] **OpenEXR** — substantially complete pure-managed
  `PureExrDecoder` after rounds 127–164: scanline + tiled (ONE_LEVEL
  / MIPMAP / RIPMAP); multi-part (first image part); compressors
  NO_COMPRESSION / RLE / ZIPS / ZIP / PIZ / PXR24 / B44 / B44A /
  DWAA-DWAB-partial; HALF / FLOAT / UINT pixel types; arbitrary
  1–4 channel sets. DWA RGB-CSC + libimf-compatible DC encoding
  outstanding; deep data still missing.
- [ ] **OpenSlide** (whole-slide microscopy: SVS / NDPI / MRXS / VMS
  / VMU / SCN / MIRAX).
- [ ] **dcraw** (camera RAW formats — Bayer demosaic, 1000+ camera
  body matrices).
- [ ] **uhdr** (Ultra HDR JPEG with gainmap — libuhdr).
- [ ] **DICOM** (medical imaging — libvips delegates to Magick).
- [ ] **Matlab v7.3** (HDF5-based, completely different format from
  v5; needs HDF5 dependency).
- [ ] **Streaming PNG/PDF load** — gated on byte[]-only decoders we
  use today (StbImageSharp, Docnet) being replaced.
- [ ] **Live preview sink** (`sinkscreen.c`) — niche, used by
  libvips' own GUI. Probably never relevant for a library port.

---

## Format-specific narrow gaps

Holes inside formats we already handle, that would close edge cases.

- [ ] **NIfTI**: 4D+ time-series (fMRI volumes — needs N-D semantics
  `VipsImage` doesn't model), paired-form save (multi-stream saver
  API needed), signed-int datatypes (int16/int32 are common in raw
  scanner output), full qform/sform quaternion-based spatial
  transforms.
- [ ] **FITS**: NAXIS≥4 data cubes, additional HDUs (binary tables,
  ASCII tables), WCS coordinate-system reconstruction beyond the
  raw card preservation we do today.
- [x] ~~**Matlab v5 writer**~~ (round 178) — `VipsMatSaver` mirrors the
  v5 reader: 128-byte ASCII descriptor, `miMATRIX` top element with
  ArrayFlags / Dimensions / Name / RealPart sub-elements. UChar →
  mxUInt8, Float / others → mxSingle. 1-band as 2D matrix, multi-band
  as 3D with planes as the last dim. Row-major → column-major
  transpose during write so the loader's reverse-transpose recovers
  original pixels.
- [x] ~~**PBM/PGM/PPM 16-bit variants**~~ (round 176) — UShort inputs
  emit native 16-bit P5/P6 binary with maxval=65535 and big-endian
  samples per spec. Loader already handled 16-bit on read; saver
  no longer narrows to UChar.
- [x] ~~**PAM (P7)**~~ — pure-C# both directions. Loader handles P7
  via the in-file `ParsePam` helper (round 145). Saver added in
  round 180: line-oriented WIDTH/HEIGHT/DEPTH/MAXVAL/TUPLTYPE/ENDHDR
  header, 8-bit or 16-bit binary samples, big-endian for 16-bit per
  spec. Auto mode now keeps RGBA inputs entirely on the pure-C#
  path (was the last loader-side Magick fallback in PNM).
- [x] ~~**BMP**: paletted (1/4/8 bpp), 16bpp RGB555, RLE-compressed,
  BITFIELDS-masked, V4/V5 colour-space variants~~ — pure-C# fast
  path already handles all of these (BI_RGB at 1/4/8/16/24/32 bpp,
  BI_RLE8, BI_RLE4, BI_BITFIELDS at 16/32 bpp; V4/V5 headers via
  the `dibSize >= 40` check). Round 179 added explicit
  hand-crafted regression tests for the paletted + BITFIELDS paths.
- [x] ~~**TGA**: paletted (types 1/9), 16bpp RGB555~~ — pure-C# fast
  path covers types 1 (raw paletted), 9 (RLE paletted), 2 (raw RGB),
  10 (RLE RGB), 3/11 (greyscale), depths 8/15/16/24/32. Round 179
  added explicit hand-crafted regression tests for the paletted +
  16bpp paths.
- [x] ~~**dzsave**: Zoomify, Google layouts~~ (round 181) —
  `VipsDzLayout` enum on `VipsDzSaver`. Zoomify emits
  `TileGroup{N}/{level}-{col}-{row}.{ext}` with cumulative
  256-tile-per-group numbering plus an `ImageProperties.xml`
  descriptor. Google emits flat `{level}/{col}/{row}.{ext}` with no
  descriptor. DZ stays the default. IIIF still deferred — its
  region-addressed URL scheme is fundamentally different from
  fixed-tile-grid layouts.
- [x] ~~**APNG**: pure-C# saver~~ (round 182) — composes per-frame
  PNGs (from `VipsPngSaver`) into APNG `acTL` / `fcTL` / `fdAT`
  chunks directly. All-frames-animated variant: every frame
  (including frame 0) participates in the animation; loops infinitely.
  Drops the last Magick dependency in the APNG path. Round-trip via
  `PureApngDecoder` verifies frame content. <c>dispose_op=NONE</c>,
  <c>blend_op=SOURCE</c> for full-canvas frames.
- [ ] **Animated AVIF/HEIC save** — gated on Magick.NET-Q8 HEIC
  encoder availability.
- [ ] **TIFF**: full Tiled-TIFF with explicit tile geometry control;
  16-bit-per-sample throughput; OME-TIFF Z/C/T full N-D mapping
  (we surface OME-XML metadata only).

---

## Misc / quality items

- [ ] **Real glyph shaping for `Text`** (HarfBuzz binding or pure-managed
  text shaper). Currently Magick.NET fallback with rudimentary kerning.
- [ ] **`vector.cpp` SIMD IR equivalent** — libvips compiles per-op
  SIMD at runtime via Orc; we have ad-hoc `Vector<T>` use in a few
  hot paths. A systematic IR isn't on the radar but would close the
  "SIMD pervasive" gap.
- [ ] **Pool ownership across image lifetime** — `MemorySink.Pixels`
  and loader `PixelsLazy` currently allocate via `new byte[]`;
  pooling them needs explicit disposal semantics on `VipsImage`,
  which is a separate design call.
- [x] ~~**Cache LRU**~~ — see "LRU operation cache" entry under iofuncs/
  (round 177).

---

## Where this leaves the project

CosmoImage covers the **mainline web-image-service / document /
photo-editing / CDN-thumbnail** workloads completely:

- Lazy demand-driven pipeline, sink-driven threadpool, full
  Float-throughout pipeline (Linearize → Resize → Composite → Glow →
  Vignette → Delinearize end-to-end in Float).
- All popular web formats (JPEG, PNG, WebP, HEIF/AVIF, GIF, SVG) plus
  scientific (HDR, FITS, NIfTI, Matlab v5) plus deep-zoom output.
- Typed pixel access, pool-backed transient buffers, opt-in streaming
  load on every Stream-capable format.

It does **not** cover:

- The full libvips colour-management graph (Lab / Oklab / CMYK / etc.).
- The mosaicing / panorama subsystem.
- Most generators (`create/`).
- Many band-manipulation conversion ops.
- Several niche format codecs (JPEG XL/2K, OpenSlide, dcraw, DICOM —
  OpenEXR landed in rounds 127–164).

Closing the full gap is hundreds of ops and several native bindings'
worth of work — multi-month at minimum. The matrix above is the map
for whoever picks it up.
