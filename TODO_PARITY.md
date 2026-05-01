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

- [ ] **PNG XMP via `iTXt` chunk**. The iTXt format is "keyword + null + flags
  + lang tag + null + translated keyword + null + UTF-8 text". We already
  read/write `eXIf` and `iCCP`; XMP is the missing third leg.
  **Cost: ~50 lines reader, ~50 lines writer. Value: completes PNG metadata round-trip.**

- [ ] **GIF / APNG metadata round-trip**. Magick.NET supports GIF/PNG comments
  and APNG ancillary chunks. Not a typical workflow concern but unblocks
  edge cases (license attribution embedded in animated GIFs).

---

## Tier 2 — quality-of-life

Useful additions that don't block anything but improve the library.

- [ ] **Math suite**: `abs`, `sin`, `cos`, `log`, `exp`, `pow`, `sqrt` as
  pointwise ops. ~50 lines. libvips has these as `vips_math`.

- [ ] **Boolean / Relational suite**: `and`, `or`, `xor`, `lt`, `le`, `gt`,
  `ge`, `eq`, `neq`. Useful for masking workflows.

- [ ] **Stats ops**: `avg`, `min`, `max`, `deviate`, `stats`. Analysis-side;
  often needed in scientific or auto-grading workflows.

- [ ] **Inverse FFT + spectrum**. Forward exists; inverse and magnitude/phase
  decomposition close out the frequency-domain story.

- [ ] **Open / Close / Rank morphology**. Dilate + Erode are present; their
  compositions and rank filters (median is rank 50%) are missing.

- [ ] **`Mutate(action)` block scoping API**. Pure ergonomic — wraps the
  existing fluent extensions in an action delegate. ImageSharp users who
  prefer that style would land softer.

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
| 1 (production) | 3 | small to large |
| 2 (quality-of-life) | 6 | small each |
| 3 (niche) | ~12 | small to medium |
| 4 (architectural) | 6 | weeks to months each |

**The original 13-item TODO is 100% complete.** What's listed here is the next
horizon. For typical web-image-service, document-processing, photo-editing,
and CDN-thumbnail workloads, none of these block shipping.
