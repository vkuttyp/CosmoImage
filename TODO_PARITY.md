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
- [ ] `linear_const` variant with broadcast scalar (`linear` shipped).
- [ ] `sign` / `floor` / `ceil` / `rint` — extend `VipsMath` to cover
  the libvips full set.
- [ ] `clamp` (per-band clamp to range).
- [ ] `min` / `max` / `sum` reductions exposed as standalone ops
  (currently only via `Stats`).
- [ ] `maxpair` / `minpair` (per-pixel max/min of two images).
- [ ] `getpoint` (extract single pixel as values) — wraps existing
  `TypedImage<TPixel>.GetPixel`.

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
- [ ] `rot45` (45-degree rotate by lookup).
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
  [-1, 1] → [0, 255]. FFT-accelerated `fastcor` still missing.
- [ ] `conva` / `convasep` (approximate large-kernel via box-pass).

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
- [ ] `hist_plot` (visualise hist as image).

### `freqfilt/`
- [x] ~~`freqmult`~~ (round 32) — FwFft → real-mask multiply → InvFft;
  preserves Float output.
- [ ] `phasecor` (phase correlation — image registration / motion
  estimation).

### `resample/`
- [ ] `mapim` (nonlinear remap via index image — lens correction,
  warping).
- [ ] `quadratic` (quadratic transform).
- [ ] `similarity` (constrained scale + rotate + translate).
- [ ] Edge-preserving interpolators: `nohalo`, `lbb`, `vsqbs`.

### `draw/`
- [ ] `draw_circle`.
- [ ] `draw_flood` (flood fill).
- [ ] `draw_mask` (draw with alpha mask).
- [ ] `draw_smudge`.

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
- [ ] CICP2scRGB (BT.2100 / Rec.2020 / PQ / HLG transfer functions —
  HDR / wide-gamut interop).
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
- [ ] `eye` / `grey` / `zone` (test-pattern generators).
- [x] ~~`gaussmat`~~ (round 38) — Float matrix kernel image; auto-sized
  by `min_ampl` cutoff. `logmat` / `gaussnoise` still missing.
- [ ] Frequency-domain mask generators: `mask_butterworth` /
  `mask_gaussian` / `mask_ideal` × {plain, band, ring} = 9 ops.
- [ ] `mask_fractal` / `fractsurf` (fractal generators).
- [x] ~~`sines`~~ (round 38) — Float sinusoid pattern; frequencies in
  cycles per image. `perlin` / `worley` still missing.
- [ ] `sdf` (signed distance field).
- [x] ~~`buildlut`~~ (round 38) — piecewise-linear LUT from anchor
  points; multi-band when each anchor carries multiple y values.
- [x] ~~`identity`~~ (round 38) — identity LUT (256-wide UChar; or
  65536-wide UShort with `ushort_: true`).
- [ ] `point` / `tonelut` / `invertlut` (remaining LUT scaffolding).

### Composite mode parity
- [ ] Extend `VipsComposite` with the 19 PorterDuff modes libvips'
  `composite2` supports (over, in, out, atop, xor, dest-over, …).
  Currently we only do `over`.

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
- [ ] **Output target abstraction** (`vips_target_*`). Currently we
  only have one-shot `PipeWriter`-based saver entry points; libvips
  has a full `IVipsTarget` interface symmetric to `IVipsSource`.
  Lets savers write to memory / fd / custom callbacks uniformly.
- [ ] **Disc-backed sink** (`sinkdisc.c`). For images too big to
  materialize in memory, libvips writes a temporary tiled file and
  reads back per-tile. Closes the "what about a 50000×50000-pixel
  WSI?" use case.
- [ ] **Op-tree reordering** (`reorder.c`). Memory-locality-aware
  ordering of pipeline stages.
- [ ] **Profiling / gating** (`gate.c`). Built-in op-tree profiler
  for finding slow stages.
- [ ] **LRU operation cache** (`cache.c` upgrade). Currently
  count-based; libvips evicts based on resource use.

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
- [ ] **OpenEXR** (OpenEXR / OpenEXRCore — half-precision floats,
  multiple compression schemes, tile layouts).
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
- [ ] **Matlab v5 writer** (mirror of the v5 reader shipped in round 21).
- [ ] **PBM/PGM/PPM 16-bit variants** — currently fall through to
  Magick because of parser inflation; can be added as a follow-up.
- [ ] **PAM (P7)** — currently delegates to Magick; pure-C# parser
  doable but the WIDTH/HEIGHT/DEPTH/MAXVAL/TUPLTYPE header is more
  elaborate than P1-P6.
- [ ] **BMP**: paletted (1/4/8 bpp), 16bpp RGB555, RLE-compressed,
  BITFIELDS-masked, V4/V5 colour-space variants — currently
  fall through to Magick.
- [ ] **TGA**: paletted (types 1/9), 16bpp RGB555 — fall through.
- [ ] **dzsave**: Zoomify, IIIF, Google layouts (we ship DZI only).
- [ ] **APNG**: all-frames-animated variant (we ship single + simple
  multi-frame).
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
- [ ] **Cache LRU** — current `VipsCache` is count-based with simple
  eviction; libvips uses LRU + resource cost.

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
- Several niche format codecs (OpenEXR, JPEG XL/2K, OpenSlide, dcraw,
  DICOM).

Closing the full gap is hundreds of ops and several native bindings'
worth of work — multi-month at minimum. The matrix above is the map
for whoever picks it up.
