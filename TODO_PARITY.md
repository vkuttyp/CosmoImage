# Parity TODO

Outstanding items, grouped by what would deliver the most user-visible value.
The original 13-item TODO is fully complete; this is the **next** layer of
work. Items already shipped are not listed — see `PARITY_MATRIX.md` for
current coverage.

---

## Tier 1 — production gaps (rare)

The mainline image-service workload is covered. These are the only Tier-1
items that real production pipelines might still hit.

- [ ] **`Image<TPixel>` strongly-typed pixel access**. Architectural surgery —
  every public op signature would need to become generic in pixel format.
  Doesn't translate cleanly to the lazy-pipeline model where each op produces
  a new image, not a mutated buffer. **Cost: weeks. Value: ergonomic for app
  authors who write loops over pixels; production code is fine without it.**

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
- [ ] **TGA / QOI / PBM formats**. Magick.NET supports all three; ~10 lines
  per loader/saver wrapping the existing Magick pipeline.
- [ ] **OpenEXR / Radiance HDR**. Scientific HDR formats; would need
  Float-format ops first to be meaningful.
- [ ] **FITS / NIfTI**. Scientific imaging.
- [ ] **Animated AVIF / HEIC sequences**. Encoder-dependent in libheif;
  Magick.NET surface is limited.
- [ ] **BokehBlur**. Aperture-shaped (typically hexagonal) blur kernel — niche
  vs Gaussian.
- [ ] **TIFF pyramidal / Tiled TIFF / OME-TIFF**. Deep-zoom and microscopy
  workflows. Each is a focused multi-resolution write extension.
- [ ] **`dzsave` (Deep Zoom)**. IIIF / OpenSeadragon-compatible tiled output.
  libvips has this; non-trivial to port.
- [ ] **CSV / Matrix / Matlab data loaders**. Niche scientific use.

---

## Tier 4 — architectural lifts

Each is significant work; defer until a concrete use case demands it.

- [ ] **Float-format ops throughout**. The big one. Every op (Resize, Conv,
  Linear, Affine, Composite, …) needs a Float band-format code path. Required
  for true high-precision linear-light processing. **Cost: months. Current
  workaround: UChar LUT-based `Linearize`/`Delinearize` covers the common
  resize-without-halos case.**

- [ ] **Proper PCS-based ICC color management**. Needs a Color Management
  Module (LittleCMS) native binding. Current `IccTransform` is a one-shot
  Magick.NET conversion; real CMM workflow keeps source profile attached
  through ops, transforms at sink boundary. **Cost: significant. Value: only
  hit in print-prep or color-graded video workflows.**

- [ ] **MemoryAllocator hooks**. Plumb `ArrayPool<byte>` (or caller-supplied
  pool) through `VipsRegion`, `MemorySink`, and the lazy materializers.
  **Cost: moderate. Value: production high-throughput services that need
  fine-grained GC control.**

- [ ] **Streaming load (no full-buffer-into-memory)**. All loaders currently
  read the source into a `byte[]` before decoding. Codecs that support
  progressive decoding (PNG, JPEG to some extent) could be wired to a
  byte stream. ImageSharp also buffers, so it's a parity item rather than a
  gap — but it would meaningfully improve memory profile on huge inputs.

- [ ] **Unified `VipsFields` metadata API**. Currently we have
  `Metadata` (`Dictionary<string, string>`) for parsed text and
  `MetadataBlobs` (`Dictionary<string, byte[]>`) for raw segments.
  libvips' `vips_image_get_*` family gives typed access to scalar tags
  (orientation as int, GPS as double[3], etc.). Would mostly be a
  convenience wrapper on top of what's there.

- [ ] **Drop more `Magick.NET` usage**. Magick.NET-Q8 is the only native
  dependency left. Every loader/saver could in principle use a format-specific
  managed library (JpegLibrary already does for JPEG, StbImageSharp for PNG).
  WebP/HEIF/AVIF/TIFF/SVG/BMP/GIF currently route through Magick. Replacing
  those would shrink the native footprint but is ~weeks of work and trades
  one large dep for several smaller ones.

---

## Status summary

| Tier | Items remaining | Median effort |
| :--- | :---: | :--- |
| 1 (production) | 1 | architectural (Image<TPixel>) |
| 2 (quality-of-life) | 0 | — |
| 3 (niche) | ~12 | small to medium |
| 4 (architectural) | 6 | weeks to months each |

**The original 13-item TODO is 100% complete.** What's listed here is the next
horizon. For typical web-image-service, document-processing, photo-editing,
and CDN-thumbnail workloads, none of these block shipping.
