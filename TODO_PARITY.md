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
- [ ] `add` / `subtract` / `multiply` / `divide` / `remainder`
  (image-image binary arithmetic). Pointwise; trivial Float code path.
- [ ] `linear` (a·x+b) — already shipped, but `linear_const`
  variant with broadcast scalar is missing.
- [ ] `sign` / `floor` / `ceil` / `rint` — extend `VipsMath` to cover
  the libvips full set.
- [ ] `clamp` (per-band clamp to range).
- [ ] `min` / `max` / `sum` reductions exposed as standalone ops
  (currently only via `Stats`).
- [ ] `maxpair` / `minpair` (per-pixel max/min of two images).
- [ ] `getpoint` (extract single pixel as values) — wraps existing
  `TypedImage<TPixel>.GetPixel`.

### `conversion/`
- [ ] `bandbool` / `bandfold` / `bandunfold` / `bandjoin` /
  `bandjoin_const` / `bandmean` / `bandrank` — band-axis ops.
- [ ] `addalpha` (force alpha channel).
- [ ] `flatten` (alpha-flatten against background colour).
- [ ] `premultiply` / `unpremultiply`.
- [ ] `embed` (place into larger canvas with extension mode).
- [ ] `gravity` (positional embed).
- [ ] `replicate` (tile to bigger size).
- [ ] `rot45` (45-degree rotate by lookup).
- [ ] `byteswap`.
- [ ] `falsecolour` (per-band LUT for visualisation).
- [ ] `ifthenelse` (per-pixel ternary).
- [ ] `switch` (case-style multi-image select).
- [ ] `wrap` (toroidal shift).
- [ ] `zoom` (integer scale-up by replication).
- [ ] `scale` (linear stretch to 0..255 — different from `Resize`).
- [ ] `extract_band`.
- [ ] `arrayjoin` / `join` / `grid` / `insert`.

### `convolution/`
- [ ] `sharpen` — distinct from `UnsharpMask`; libvips' version does
  Lab-space sharpening with shadow/highlight thresholds.
- [ ] `canny` (Canny edge detector).
- [ ] `compass` (compass-pattern edge response).
- [ ] `correlation` / `fastcor` / `spcor` (template matching).
- [ ] `conva` / `convasep` (approximate large-kernel via box-pass).

### `morphology/`
- [ ] `nearest` (distance to nearest non-zero pixel — distance transform).
- [ ] `labelregions` (connected-component labelling — useful for
  segmentation pipelines).
- [ ] `countlines` (count black-white transitions per scanline).

### `histogram/`
- [ ] `hist_local` (CLAHE — high-value adaptive equalisation).
- [ ] `hist_match` (histogram matching against reference).
- [ ] `hist_entropy`.
- [ ] `percent` (find threshold for given percentage).
- [ ] `stdif` (statistical differencing — local-contrast enhancement).
- [ ] `hist_plot` (visualise hist as image).
- [ ] `case` (per-pixel select from band of LUTs).

### `freqfilt/`
- [ ] `freqmult` (frequency-domain multiply with mask — apply a
  designed filter).
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
- [ ] Lab ↔ LabQ (8-bit packed), Lab ↔ LabS (16-bit signed).
- [ ] XYZ ↔ Oklab, Oklab ↔ Oklch — Björn Ottosson's perceptual space.
- [ ] sRGB ↔ HSV (we use HSV internally for `Lightness` but don't
  expose the converters).
- [ ] XYZ ↔ CMYK (print colourspace).
- [ ] CICP2scRGB (BT.2100 / Rec.2020 / PQ / HLG transfer functions —
  HDR / wide-gamut interop).
- [ ] uhdr2scRGB (Ultra HDR JPEG with gainmap).
- [ ] dE76 / dE00 / dECMC colour-difference metrics.
- [ ] Pipeline-aware ICC: profile attached to image metadata, transform
  applied at sink boundary rather than as a one-shot. Currently
  `IccTransform` is a one-shot Magick call.

### Image generators (`create/`)
Whole subsystem missing apart from `Text`. Each is a small standalone
generator that produces an image from parameters.

- [ ] `black` (constant 0).
- [ ] `xyz` (per-pixel coordinate image — input to `mapim`).
- [ ] `eye` / `grey` / `zone` (test-pattern generators).
- [ ] `gaussmat` / `logmat` / `gaussnoise` (filter-mask generators).
- [ ] Frequency-domain mask generators: `mask_butterworth` /
  `mask_gaussian` / `mask_ideal` × {plain, band, ring} = 9 ops.
- [ ] `mask_fractal` / `fractsurf` (fractal generators).
- [ ] `perlin` / `worley` / `sines` (procedural texture).
- [ ] `sdf` (signed distance field).
- [ ] `point` / `tonelut` / `buildlut` / `invertlut` / `identity`
  (LUT scaffolding).

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
