# Parity TODO

Outstanding items, grouped by what would deliver the most user-visible value.
The original 13-item TODO is fully complete; this is the **next** layer of
work. Items already shipped are not listed — see `PARITY_MATRIX.md` for
current coverage.

---

## Tier 1 — production gaps (rare)

The mainline image-service workload is covered. These are the only Tier-1
items that real production pipelines might still hit.

- [x] ~~**`Image<TPixel>` typed pixel access**~~ — `TypedImage<TPixel>` ships
  with `L8` / `La16` / `Rgb24` / `Rgba32` pixel structs and zero-copy
  `RowSpan(y)`. Construct from a `VipsImage` (materializes once) or fresh
  `(width, height)` and call `AsVipsImage()` to feed back into the lazy op
  pipeline. **Remaining**: making every existing op signature generic in
  `TPixel` — that piece is still architectural and likely better as a
  parallel typed API rather than replacing the untyped one. Float-pixel
  variants land with Tier-4 Float-throughout.

- [x] ~~**PNG XMP via `iTXt` chunk**~~. Read+write landed; canonical
  `XML:com.adobe.xmp` keyword, uncompressed UTF-8 payload.

- [x] ~~**GIF / APNG metadata round-trip**~~. EXIF/XMP/ICC profiles plus the
  free-form `Metadata["comment"]` attribute round-trip via Magick.NET.

---

## Tier 2 — quality-of-life

All Tier-2 items shipped in this pass.

- [x] ~~**Math suite**~~ — `Abs/Sin/Cos/Tan/Log/Log10/Exp/Exp10/Sqrt/Pow`,
  LUT-driven on UChar.
- [x] ~~**Boolean / Relational suite**~~ — const-vs-image and image-vs-image
  variants for `And/Or/Xor/LShift/RShift` and `Equal/NotEqual/Less/LessEq/More/MoreEq`.
- [x] ~~**Stats ops**~~ — `Stats(image)` returns per-band + aggregate
  `Min/Max/Avg/Deviate`; convenience `Avg/Min/Max/Deviate(image)` shortcuts.
- [x] ~~**Inverse FFT + spectrum**~~ — `InvFft` reconstructs spatial UChar from
  DPComplex; `Spectrum` returns FFT-shifted, normalized log-magnitude.
- [x] ~~**Open / Close / Rank morphology**~~ — Open/Close as Erode↔Dilate
  compositions; `Rank(w, h, k)` with quickselect; `Median(window)` shortcut.
- [x] ~~**`Mutate(action)` block scoping API**~~ — `image.Mutate(im => im.Resize(...).Sepia())`.

---

## Tier 3 — niche

Targeted gaps. Each affects a specific workflow that most users never hit.

- [ ] **JPEG XL pixel decode**. We have the header stub; full decode needs an
  ANS-coded bitstream parser or a native libjxl binding.
- [ ] **JPEG 2000 pixel decode**. Header-only currently; full decode similar
  scope to JXL.
- [x] ~~**TGA / QOI / PBM formats**~~. All shipped via Magick.NET wrappers
  (`VipsTgaLoader`/`VipsTgaSaver`, `VipsQoi*`, `VipsPnm*` covering
  PBM/PGM/PPM/PAM with Auto variant detection). TGA passes a format hint
  since it has no magic bytes.
- [x] ~~**Radiance HDR (`.hdr`)**~~ shipped. Pure C# decoder + encoder
  (no Magick dependency); 3-band Float output in linear-light RGB,
  Greg Ward RGBE encoding with new-style per-component RLE. Float-throughout
  (rounds 5-8) is what made this loadable end-to-end. Header lines like
  `EXPOSURE=` round-trip via `Metadata["hdr:exposure"]`.

- [ ] **OpenEXR**. Same HDR niche as Radiance HDR but with EXR-specific
  multi-resolution / multi-channel layout. Now unblocked by the
  Float-throughout work, but the EXR codec itself is a substantial
  project — half-precision floats, multiple compression schemes, tile
  layout. Defer until concrete demand.
- [x] ~~**FITS**~~ shipped. Pure-C# loader/saver (no native deps). Decodes
  BITPIX 8 (UChar), 16 / 32 (signed integer → Float, BSCALE/BZERO applied),
  -32 (IEEE float), -64 (double → cast to Float). NAXIS=2 grayscale and
  NAXIS=3 with planar→interleaved transpose for RGB/RGBA. WCS / DATE-OBS /
  OBSERVER / etc. survive load → save via `Metadata["fits:*"]`. NAXIS≥4
  data cubes and additional HDUs (binary tables) intentionally out of scope.

- [ ] **NIfTI**. Neuroimaging format. Similar shape to FITS (header +
  raw pixel data) but the header is binary-fixed rather than ASCII cards,
  and the data layout is more complex (4D volumes, slope/intercept,
  qform/sform spatial transforms). Defer until a concrete use case shows up.
- [x] ~~**Animated AVIF / HEIC sequences (load)**~~ shipped. `VipsHeifLoader`
  enumerates frames via `MagickImageCollection`, stacks into a tall buffer
  with `n-pages` / `page-height` / `animation-delays` metadata (same
  convention as WebP/GIF). Two parsing fixes were needed:
  - The manual ISOBMFF parser in `LoadHeaderAsync` only handles the still
    image box layout (top-level `ispe`); sequence-brand files (`avis`,
    animated HEIC) use a movie-track layout. `LoadAsync` falls through to
    a Magick-based probe when the manual parser returns null.
  - `LoadStreamingAsync` now buffers to a seekable `MemoryStream` first —
    Magick.NET requires random access for ISOBMFF box parsing, and the
    forward-only `VipsSourceStream` can't provide it. Streaming win is
    preserved (encoded buffer goes out of scope after decode).

  **Encoding** of animated AVIF/HEIC is gated on the ImageMagick build:
  Magick.NET-Q8-arm64 ships only a single-frame HEIC encoder; AVIF
  sequence write does work via `MagickImageCollection.Write(MagickFormat.Avif)`.
  Sequence write through `VipsHeifSaver` is not yet exposed — defer until
  there's a concrete use case.
- [x] ~~**BokehBlur**~~. Hexagonal-aperture kernel composed with the existing
  `VipsConv`. `image.BokehBlur(radius)`.
- [x] ~~**TIFF pyramidal write**~~. `SaveTiffAsync(image, writer, pyramid:true)`
  emits Magick's `Ptif`. Tiled-TIFF (with explicit tile geometry control)
  still pending — that's a deeper libtiff knob.

- [x] ~~**OME-TIFF**~~ shipped at the metadata-round-trip level. TIFF
  ImageDescription (tag 270) is now a generic round-trippable field via
  `Metadata["tiff:image-description"]`; OME-shaped XML is also surfaced
  as `Metadata["ome:xml"]` and parsed by `VipsOmeTiff` for typed access
  to `<Pixels>` PhysicalSize and `<Channel>` records. The TIFF loader
  auto-populates `XRes`/`YRes` from PhysicalSizeX/Y with unit conversion
  (µm/mm/cm/m/nm/Å). N-D layout (Z, C, T) is intentionally out of scope
  — `VipsImage` is 2D / multi-page only; callers needing full N-D
  semantics work with the raw XML.
- [x] ~~**`dzsave` (Deep Zoom)**~~ shipped. `VipsDzSaver.SaveAsync(image, basePath, …)`
  emits the Microsoft DZI 2008 layout (`{basePath}.dzi` + `{basePath}_files/`).
  Supports JPEG and PNG tiles, configurable tile size and overlap.
  OpenSeadragon-compatible. Other layouts (Zoomify, IIIF) deferred —
  DZI is the most widely-supported and porting the full layout matrix is
  its own project. Unlike other savers this writes to a directory rather
  than a `PipeWriter`, so the entry point takes a base path string.
- [x] ~~**CSV / Matrix data loaders**~~. `VipsCsvLoader` and `VipsMatrixLoader`
  parse whitespace/comma-separated numeric grids; comments + header rows
  supported. Matlab `.mat` parsing still pending (binary format, separate
  effort).

---

## Tier 4 — architectural lifts

Each is significant work; defer until a concrete use case demands it.

- 🟢 **Float-format ops throughout** — mainline shipped.

  **Shipped**:
    - Keystone: `VipsCast` (UChar↔Float, libvips no-auto-normalize convention)
    - Pointwise: `VipsInvert`, `VipsLinear`, `VipsRecomb` (drives
      Saturate/Sepia/Hue/Greyscale), `VipsMath`, `VipsGamma`
    - Window: `VipsConv1D` (drives `GaussBlur`), `VipsConv` (2D mask),
      `VipsMorph` (Dilate/Erode with -∞/+∞ seeding), `VipsRank` (quickselect
      on floats; drives `Median`)
    - Color management: `VipsLinearize`/`VipsDelinearize` with full
      per-pixel sRGB transfer in double precision (measurably more precise
      than the UChar 256-entry LUT — covered by `LinearizePrecision_Float_BeatsUCharLut`)
    - Geometric: `VipsShrink`, `VipsResize1D` (X+Y), `VipsAffine`, driving
      `VipsResize`, `VipsRotate`, `VipsThumbnail` end-to-end
    - Composition/Effects: `VipsComposite` (alpha-over with Float alpha as
      nominal [0,1]), `VipsVignette`, `VipsGlow` (no clamp on the additive
      blend), `VipsPixelate` (inherits via Shrink + Resize composition)
    - Analysis: `VipsStats` (per-band Min/Max/Avg/Deviate over Float pixels)

  The mainline color-correct linear-light pipeline (Linearize → Resize →
  Composite → Glow → Vignette → Delinearize) runs end-to-end in Float
  with no UChar quantization.

  **Remaining UChar-only — by design or out of scope**:
    - Artistic effects (`VipsOilPaint`/`VipsCharcoal`/`VipsSketch`/`VipsPolaroid`):
      Magick.NET-backed; the underlying codec is UChar internally, so a
      Float branch would have to round-trip through Magick anyway. Wait
      until the broader "drop more Magick.NET" Tier-4 item lands.
    - `VipsDrawLine`/`VipsDrawRect`: niche; ink colour is a `byte[]` parameter.
      Adding a Float ink overload would touch the public API.
    - `VipsHistFind`: 256-bin LUT shape is fundamentally UChar-input. A Float
      version would need a binning policy (range + bin count); separate design call.
    - `VipsFwFft`/`VipsInvFft`: already work in DPComplex internally; the
      Float-input path would just save one Cast at the boundary.
    - `VipsMaplut`: UChar-input by design — the LUT is indexed by byte value.
      A Float-input variant would need fractional LUT interpolation, which
      changes the op's semantics rather than its precision.
    - Boolean suite (`And`/`Or`/`Xor`/`LShift`/`RShift`): bitwise on Float
      is not meaningful; intentionally UChar-only.

- [ ] **Proper PCS-based ICC color management**. Needs a Color Management
  Module (LittleCMS) native binding. Current `IccTransform` is a one-shot
  Magick.NET conversion; real CMM workflow keeps source profile attached
  through ops, transforms at sink boundary. **Cost: significant. Value: only
  hit in print-prep or color-graded video workflows.**

- [x] ~~**MemoryAllocator hooks (transient buffers)**~~. `IVipsAllocator` is
  plumbed through `VipsRegion` (working memory rented per Prepare, returned
  on Dispose) and `OrderedStripSink` (per-tile copy buffers rented before
  the consumer callback, returned after). Default is
  `ArrayPoolAllocator.Shared` wrapping `ArrayPool<byte>.Shared`; callers can
  set a custom `IVipsAllocator` per-image and it propagates through
  `SetPipeline`. Long-lived buffers (`PixelsLazy`, `MemorySink.Pixels`) still
  use plain `new byte[]` — pool ownership across an image lifetime needs
  explicit disposal semantics on `VipsImage`, which is a separate design call.

- 🟢 **Streaming load (no full-buffer-into-memory)** — mainline.

  **Shipped**:
    - `VipsSourceStream`: forward-only `Stream` adapter over `IVipsSource`.
      `IVipsSource.AsStream()` extension exposes it.
    - `LoadStreamingAsync` opt-in path on every Stream-capable loader:
      `VipsJpegLoader`, `VipsTiffLoader`, `VipsBmpLoader`, `VipsWebpLoader`
      (animated), `VipsGifLoader` (animated), `VipsHeifLoader` (HEIF/AVIF),
      `VipsSvgLoader`, plus `VipsMagickWrapLoader` (TGA/QOI/PBM-PAM).
    - The streaming variant decodes pixels eagerly and drops the encoded
      buffer immediately — trades laziness for not holding the encoded
      file alongside the decoded pixel buffer. Use when the caller knows
      pixels will be materialized; pure-metadata callers stick with
      `LoadHeaderAsync`.

  **Remaining** — gated on decoder API rather than loader work:
    - PNG: `StbImageSharp.ImageResult.FromMemory` takes `byte[]` only.
      Replacing the PNG decoder with one that accepts `Stream` (libpng
      via P/Invoke, or a managed PNG library with stream support)
      unlocks streaming PNG.
    - PDF: `Docnet.Core` takes `byte[]` only. Same shape — gated on the
      underlying decoder.

- [x] ~~**Unified `VipsFields` metadata API**~~. `Core/VipsFields.cs` adds
  `GetInt/GetDouble/GetDoubleArray/GetBlob` + matching setters, plus
  well-known shortcuts (`GetOrientation`, `GetComment`, `GetAnimationDelays`,
  `GetExif`, `GetXmp`, `GetIccProfile`).

- 🟡 **Drop more `Magick.NET` usage** — incremental. First format dropped:
  PBM/PGM/PPM (the standard P1-P6 Netpbm variants) are now pure-C# in
  `VipsPnmLoader` and `VipsPnmSaver`. PAM (P7) and 16-bit-per-sample
  variants still go through Magick because the parser inflation isn't
  worth it for the rare formats. Other formats still on Magick:
  WebP / HEIF / AVIF / TIFF / SVG / BMP / GIF / TGA / QOI. Each is a
  separate decoder port; expect days-to-weeks per format.

---

## Status summary

| Tier | Items remaining | Median effort |
| :--- | :---: | :--- |
| 1 (production) | 0 | typed access shipped; generic ops deferred |
| 2 (quality-of-life) | 0 | — |
| 3 (niche) | 7 | mostly format-codec heavy |
| 4 (architectural) | 4 | weeks to months each |

**The original 13-item TODO is 100% complete.** What's listed here is the next
horizon. For typical web-image-service, document-processing, photo-editing,
and CDN-thumbnail workloads, none of these block shipping.
