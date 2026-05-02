# CosmoImage

A C# / .NET 10 image-processing library with the architecture of
[libvips](https://www.libvips.org) and the surface area of
[ImageSharp](https://github.com/SixLabors/ImageSharp), under a
permissive-only dependency stack.

- **Lazy demand-driven pipeline** — operations don't compute pixels until a
  sink (saver, materialization, or export) consumes them.
- **Sink-driven multi-stage parallelism** — one threadpool drains the entire
  pipeline; parallelism scales with consumer demand, not per-op fork-join.
- **Float-throughout** — the mainline color-correct pipeline (Linearize →
  Resize → Composite → Glow → Vignette → Delinearize) runs end-to-end in
  Float with no UChar quantization at intermediate stages. Pointwise ops,
  convolution, morphology, geometric, and analysis all have Float
  branches.
- **Zero-copy memory-image dtype** — loaders expose pixels via shared
  buffers; downstream `Prepare` aliases instead of allocating per tile.
- **Typed pixel access** — `TypedImage<TPixel>` with `L8` / `La16` /
  `Rgb24` / `Rgba32` packed structs and zero-copy `RowSpan(y)` for tight
  loops. Round-trips back into the lazy pipeline via `AsVipsImage()`.
- **Pool-backed transient buffers** — `IVipsAllocator` plumbed through
  `VipsRegion` working memory and per-tile copy buffers; defaults to
  `ArrayPool<byte>.Shared`.
- **Opt-in streaming load** — `LoadStreamingAsync` on every Stream-capable
  decoder skips the encoded-bytes-in-memory step.
- **Modern web formats** — JPEG, PNG, WebP, TIFF (incl. pyramidal Ptif),
  BMP, GIF, HEIF, AVIF (incl. animated sequences), APNG, PDF render,
  SVG raster.
- **Scientific formats** — Radiance HDR, FITS, NIfTI-1 (single-file +
  paired), OME-TIFF metadata, Matlab `.mat` v5 numeric arrays, CSV /
  Matrix numeric grids — all pure-C#.
- **Modern container formats** — TGA, QOI, PBM/PGM/PPM/PAM, dzsave
  Deep Zoom (DZI / OpenSeadragon).
- **Metadata-aware** — EXIF, XMP, and ICC blobs round-trip across every
  format conversion. AutoOrient patches the EXIF orientation tag so your
  thumbnails don't double-rotate. Typed accessors via `VipsFields`
  (`GetOrientation`, `GetComment`, `GetAnimationDelays`, etc.).
- **Permissive licensing only** — no Six Labors split-license dependency.

For honest feature-by-feature comparisons against the two parents see
[`PARITY_MATRIX.md`](./PARITY_MATRIX.md) (vs upstream **libvips** —
the architecture parent) and [`IMAGESHARP_PARITY.md`](./IMAGESHARP_PARITY.md)
(vs **SixLabors.ImageSharp** — the surface-area parent). For the
outstanding work list see [`TODO_PARITY.md`](./TODO_PARITY.md).

---

## Quick start

```csharp
using System.IO.Pipelines;
using CosmoImage;

// Load from any PipeReader / Stream
await using var fs = File.OpenRead("input.jpg");
await using var source = new PipeVipsSource(PipeReader.Create(fs));
var image = await VipsJpegLoader.LoadAsync(source);
if (image is null) return;

// Fluent transforms — lazy until the saver pulls
var thumb = image
    .AutoOrient()                              // EXIF orientation, blob-patched
    .Resize(0.5, kernel: VipsKernel.Lanczos3)  // separable Lanczos3 resize
    .Saturate(1.1);                            // 3×3 luma-mix matrix

// Save anywhere a PipeWriter exists
await using var output = File.Create("thumb.jpg");
var writer = PipeWriter.Create(output);
await thumb.SaveJpegAsync(writer, quality: 85);
```

That's the canonical path: load → orient → resize → save. EXIF / XMP / ICC
blobs travel through the chain unchanged and end up embedded in the output
JPEG.

---

## Architecture

CosmoImage ports libvips' demand-driven IO model. There's no central
"image buffer" that ops mutate; instead, each `VipsImage` is a header
(dimensions, bands, format) plus a `GenerateFn` callback that produces
pixels for a requested rectangle on demand.

```
┌────────────────────────────────────────────────┐
│  Saver (a sink) opens a threadpool             │
│  ┌────────┐  ┌────────┐ ... ┌────────┐         │
│  │worker 0│  │worker 1│     │worker N│         │
│  └───┬────┘  └───┬────┘     └───┬────┘         │
│      │           │              │              │
│  Each worker pulls a tile rect from a channel  │
│  and calls outputRegion.Prepare(tile)          │
└──────┬─────────────────────────────────────────┘
       ▼
┌────────────────────────────────────────────────┐
│  Prepare runs the pipeline backwards:          │
│  saver ← Resize ← AutoOrient ← LoadJpeg        │
│  Each stage runs single-threaded on the worker │
│  (parallelism comes from N workers, not stages)│
└────────────────────────────────────────────────┘
```

Key consequence: **chained ops fuse**. `image.Resize().Saturate().Linear()`
doesn't materialize three intermediate buffers — it materializes one tile
at a time, with each stage consuming directly from the previous.

The same machinery handles:
- **Demand hints** — ops declare preferred tile geometry
  (SmallTile/FatStrip/ThinStrip/Any) and the sink picks tile shape from the
  most restrictive hint along the chain.
- **Memory-image dtype** — when an op produces a fully materialized buffer
  (e.g., a loader or `Quantize`), `Prepare` aliases the buffer with no copy.
- **Per-worker `seq`** — input regions are allocated once per worker and
  reused for every tile, mirroring libvips' `vips_start_one`.

---

## Format support

### Modern web

| Format | Load | Save | Animated / multi-page | Notes |
| :--- | :---: | :---: | :---: | :--- |
| JPEG | ✅ | ✅ | n/a | full + EXIF/XMP via APP1, ICC via multi-segment APP2 |
| PNG | ✅ | ✅ | n/a | full-color + palette PNG-8 + iTXt for XMP |
| WebP | ✅ | ✅ | ✅ animated | EXIF/XMP/ICC via Magick |
| TIFF | ✅ | ✅ | ✅ multi-page + pyramidal Ptif | EXIF/XMP/ICC + ImageDescription (incl. OME-XML) |
| BMP | ✅ pure-C# fast path + Magick fallback | ✅ pure-C# (24/32 bpp) | n/a | paletted/RLE via Magick |
| GIF | ✅ | ✅ | ✅ animated | EXIF/XMP/ICC + Comment via Magick |
| HEIF / AVIF | ✅ | ✅ single-frame | ✅ animated load (sequence brand `avis` + animated HEIC) | EXIF/XMP/ICC via Magick |
| APNG | n/a | ✅ | ✅ animated | profile + Comment on first frame |
| PDF | ✅ | n/a | ✅ multi-page render | via Docnet |
| SVG | ✅ raster | n/a | n/a | via Magick |

### Scientific / niche (all pure-C#)

| Format | Load | Save | Notes |
| :--- | :---: | :---: | :--- |
| Radiance HDR (`.hdr`) | ✅ | ✅ | RLE-encoded RGBE; 3-band Float in linear-light RGB |
| FITS | ✅ | ✅ | BITPIX 8/16/32/-32/-64; BSCALE/BZERO; 2D + 3D planar transpose |
| NIfTI-1 single-file (`.nii`) | ✅ | ✅ | datatypes 2/16/64; auto-endian detect; pixdim → XRes/YRes |
| NIfTI-1 paired (`.hdr/.img`) | ✅ via `LoadPairedAsync` | ❌ | ni1 magic; shared decoder with single-file form |
| OME-TIFF | ✅ | ✅ | OME-XML in TIFF ImageDescription; typed `VipsOmeTiff` accessors |
| Matlab `.mat` v5 | ✅ numeric arrays | ❌ | tagged binary; miCOMPRESSED zlib-inflate; column-major transpose |
| CSV / Matrix | ✅ | n/a | numeric-text grids; `#` comments |

### Container / utility

| Format | Load | Save | Notes |
| :--- | :---: | :---: | :--- |
| TGA | ✅ pure-C# (types 2/3/10/11) | ✅ pure-C# | paletted via Magick |
| QOI | ✅ pure-C# | ✅ pure-C# | full QOI v1.0 spec; lossless |
| PBM/PGM/PPM | ✅ pure-C# (P1-P6) | ✅ pure-C# (P4/P5/P6) | PAM (P7) via Magick |
| PAM | ✅ via Magick | ✅ via Magick | — |
| dzsave Deep Zoom (DZI) | n/a | ✅ directory tree | Microsoft DZI 2008; OpenSeadragon-compatible |

### Stub / partial

| Format | Status |
| :--- | :--- |
| JPEG XL | 🟡 header stub only — full pixel decode needs libjxl |
| JPEG 2000 | 🟡 header only — needs libjp2k |
| OpenEXR | ❌ — needs OpenEXR binding |

EXIF / XMP / ICC blobs round-trip on JPEG, PNG, WebP, TIFF, HEIF, AVIF,
GIF, and APNG. Cross-format conversion (e.g., JPEG → AVIF) preserves all
three blob types.

---

## Loading

Every loader takes an `IVipsSource` (typically wrapped over a `PipeReader`)
and returns a `VipsImage?`. The standard pattern:

```csharp
await using var source = new PipeVipsSource(PipeReader.Create(stream));

// Single-page load
var jpeg = await VipsJpegLoader.LoadAsync(source);

// Multi-page load (PDF — explicit n=-1 means "all pages")
var allPages = await VipsPdfLoader.LoadAsync(source, page: 0, n: -1, dpi: 150);
```

Loaders also expose a header-only path that parses the file's structural
header without decoding pixels — useful for dimensions / metadata probes:

```csharp
var header = await VipsJpegLoader.LoadHeaderAsync(source);
Console.WriteLine($"{header.Width} × {header.Height}, {header.Bands} bands");
```

For animated / multi-page formats, frames are stacked vertically into a
tall buffer with `Metadata["n-pages"]` and `Metadata["page-height"]`
indicating the layout. The same convention propagates through ops and is
consumed by the matching savers, so multi-page input round-trips end-to-end.

---

## Operations

The full catalog organized by category. Every op is available either as a
fluent extension (`image.Foo(...)`) or as a static method
(`VipsImageOps.Foo(image, ...)`). The fluent form is shown here.

### Geometric

```csharp
image.Resize(0.5, kernel: VipsKernel.Lanczos3);   // separable, 10 kernels available
image.Resize1D(0.5, vertical: false);              // single-axis pass
image.Shrink(2, 2);                                // integer box-average
image.Rotate(VipsAngle.D90);                       // orthogonal rotation
image.Rotate(15.5, kernel: VipsKernel.Lanczos3);   // arbitrary angle, computes new bbox
image.Flip(VipsDirection.Horizontal);
image.Affine(a, b, c, d, idx, idy, kernel: VipsKernel.Mitchell);
image.ExtractArea(left, top, width, height);       // alias: Crop(...)
image.EntropyCrop(width, height);                  // content-aware "smartcrop"
image.AutoOrient();                                // EXIF orientation + blob patch
```

**Resampling kernels available**: `Nearest`, `Linear`, `Cubic` (Catmull-Rom),
`Mitchell`, `Lanczos2/3/5`, `Hermite`, `BicubicSharper`, `BicubicSmoother`.

### Color & pointwise

```csharp
image.Invert();
image.Linear(new[] { 1.5, 1.5, 1.5 }, new[] { 0.0, 0.0, 0.0 });   // a·x + b per band
image.Gamma(2.2);
image.Brightness(1.2);    // multiplicative; alpha-preserving
image.Contrast(1.3);      // around midpoint 128
image.Lightness(0.1);     // HSL L-axis shift, range -1..+1
image.Saturate(0.8);      // 0 = grey, 1 = identity, >1 = boost
image.Greyscale();        // = Saturate(0); preserves band count
image.Hue(30);            // degrees of rotation around the gray axis
image.Sepia();            // standard 3×3 sepia matrix
image.Recomb(matrix);     // arbitrary NxN band-recombine matrix

// Math suite — pointwise transcendentals
image.Abs(); image.Sin(); image.Cos(); image.Tan();
image.Log(); image.Log10(); image.Exp(); image.Exp10();
image.Sqrt(); image.Pow(2.2);

// Boolean / Relational suite — image-vs-image and image-vs-const
image.AndConst(0xF0); image.OrConst(0x0F); image.XorConst(0xFF);
left.And(right); left.Or(right); left.Xor(right);
image.LessConst(127);    // → mask: 255 where in < 127, else 0
image.MoreConst(200); image.EqualConst(0); image.NotEqualConst(0);

// Color-correct linear-light pipeline (full Float-throughout)
image
    .CastFloat()                          // UChar → Float
    .Linearize()                          // sRGB → linear (per-pixel transfer)
    .Resize(0.25, kernel: VipsKernel.Lanczos3)
    .GaussBlur(0.7)
    .Delinearize()                        // linear → sRGB
    .CastUChar();                         // Float → UChar

image.Quantize(colors: 64, dither: true); // Wu/median-cut palette reduction
image.Maplut(lutImage);

// Color management
image.Colourspace(VipsInterpretation.Lab);
image.IccTransform(targetIccProfile, inputIccProfile);  // via Magick.NET

// Format conversion
image.Cast(VipsBandFormat.Float);
image.CastFloat(); image.CastUChar();    // shortcuts
```

### Convolution & morphology

```csharp
image.Conv(mask);                          // 2D mask
image.Conv1D(kernel, vertical: false);     // separable building block
image.GaussBlur(2.0);                      // 2-pass separable
image.UnsharpMask(sigma: 1.0, amount: 0.8);
image.BokehBlur(radius: 5);                // hexagonal aperture (photographic)

// Morphology
image.Dilate(structuringElement);
image.Erode(structuringElement);
image.Morph(mask, VipsMorphMethod.Dilate);
image.Open(mask);                          // erode → dilate (remove specks)
image.Close(mask);                         // dilate → erode (fill gaps)

// Rank / Median (order-statistic over k×k window)
image.Rank(windowWidth: 3, windowHeight: 3, index: 4);  // index=4 of 9 = median
image.Median(windowSize: 3);                            // shortcut
```

### Drawing & composition

```csharp
// Antialiased line (Xiaolin Wu); axis-aligned lines stay full-coverage
image.DrawLine(x1: 10, y1: 20, x2: 100, y2: 80, ink: new byte[] { 255, 0, 0 });

image.DrawRect(left: 50, top: 50, width: 200, height: 100,
               ink: new byte[] { 0, 255, 0 }, fill: false);

// Sub-pixel composite — fractional offsets pre-shift the overlay via Affine
baseImage.Composite(overlay, x: 100.5, y: 50.25);
```

### Effects

```csharp
image.Vignette(strength: 0.4);             // quadratic radial darkening
image.Pixelate(blockSize: 8);              // box-average + nearest upscale
image.Glow(sigma: 5.0, strength: 0.3);     // bloom-style halo
image.BokehBlur(radius: 6);                // hex-aperture blur

// Magick.NET-backed artistic effects
image.OilPaint(radius: 3.0, sigma: 1.0);
image.Charcoal(radius: 1.0, sigma: 0.5);
image.Sketch(radius: 1.0, sigma: 0.5, angle: 0);
image.Polaroid(angle: -5);                 // RGBA output, sized to rotated bbox
```

### Analysis & frequency

```csharp
// Histograms
var hist = image.HistFind();           // per-band 256-bin histogram
var cum = hist.HistCum();              // cumulative
var norm = cum.HistNorm();             // normalized for use as a LUT
var equalized = image.HistEqual();     // = Maplut(image, HistNorm(HistCum(HistFind(image))))

// Stats — per-band + aggregate Min/Max/Avg/Deviate in one materializing scan
var stats = image.Stats();
double overallAvg = stats.Avg[image.Bands];   // last index = aggregate
// Convenience shortcuts:
double avg = image.Avg();
double min = image.Min();
double max = image.Max();
double dev = image.Deviate();

// FFT — forward, inverse, log-magnitude spectrum
var fft = image.FwFft();               // DPComplex output
var back = fft.InvFft();               // → UChar magnitude
var spectrum = fft.Spectrum();         // FFT-shifted log-magnitude UChar
```

---

## Mutate / Clone

ImageSharp-style block-scoped chaining alongside the fluent form:

```csharp
// Block-scoped — reads top-down
var thumb = source.Mutate(im => im
    .AutoOrient()
    .Resize(0.25, kernel: VipsKernel.Lanczos3)
    .Saturate(1.05));

// Equivalent fluent form
var thumb2 = source
    .AutoOrient()
    .Resize(0.25, kernel: VipsKernel.Lanczos3)
    .Saturate(1.05);
```

The lazy pipeline is unchanged — `Mutate` is purely ergonomic.

---

## Typed pixel access

`TypedImage<TPixel>` layers compile-time pixel types (`L8` / `La16` /
`Rgb24` / `Rgba32`) over the lazy pipeline. Materialize once for
direct read/write, or build a fresh buffer that flows back into the
pipeline:

```csharp
// Read finished pipeline pixels typed
var typed = vipsImage.ToTypedImage<Rgba32>();
foreach (ref Rgba32 px in typed.RowSpan(y))
    px.A = 255;

// One-shot read
Rgba32 px = vipsImage.GetPixel<Rgba32>(100, 50);

// Build typed → feed into pipeline
var fresh = new TypedImage<Rgba32>(1024, 768);
for (int y = 0; y < fresh.Height; y++)
    foreach (ref Rgba32 p in fresh.RowSpan(y))
        p = new Rgba32(255, 0, 0, 255);

await fresh.AsVipsImage()                 // memory-backed VipsImage
    .Resize(0.5).Sepia()
    .SavePngAsync(writer);
```

`RowSpan(y)` reinterprets the underlying byte buffer via
`MemoryMarshal.Cast<byte, TPixel>` — zero-copy.

---

## Streaming load

Every Stream-capable decoder has an opt-in `LoadStreamingAsync`
variant. It eagerly decodes pixels from the source and drops the
encoded bytes immediately, trading laziness for not holding the
encoded file alongside the decoded buffer. Use when you know pixels
will be materialized:

```csharp
// Lazy variant — encoded bytes held until PixelsLazy is consumed
var lazy = await VipsTiffLoader.LoadAsync(source);

// Streaming variant — encoded bytes drop after decode
var eager = await VipsTiffLoader.LoadStreamingAsync(source);
// For a 50 MB TIFF that decodes to 200 MB of pixels:
// lazy keeps ~250 MB, streaming keeps ~200 MB.
```

Available on JPEG, TIFF, BMP, WebP (animated), GIF (animated), HEIF/AVIF
(animated sequences), SVG, plus the Magick wrapper (TGA / QOI / PBM /
PAM). PNG and PDF still byte-buffer because their decoders take
`byte[]` only.

---

## Float pipelines

The mainline color-correct pipeline runs end-to-end in Float — every
op (Linearize, Resize, Affine, Composite, Conv, Morph, Glow, Vignette,
Stats, Linear, Recomb, Math, Cast) has a Float code path. No UChar
quantization at intermediate stages:

```csharp
var output = image
    .CastFloat()
    .Linearize()                          // proper sRGB transfer in double precision
    .Resize(0.5, kernel: VipsKernel.Lanczos3)
    .GaussBlur(1.5)
    .Composite(overlay.CastFloat(), x: 100, y: 200)
    .Glow(sigma: 6.0, strength: 0.3)
    .Vignette(strength: 0.2)
    .Delinearize()
    .CastUChar();
```

`Cast` follows the libvips no-auto-normalize convention (UChar 100 →
Float 100.0). Multiply by `1/255` if you want `[0, 1]` semantics.

---

## Saving

```csharp
// Modern web
await image.SaveJpegAsync(writer, quality: 85);
await image.SavePngAsync(writer);                       // full-color
await image.SavePngAsync(writer, palette: 64);          // palette PNG-8 with quantization
await image.SaveWebpAsync(writer, quality: 75, lossless: false);
await image.SaveTiffAsync(writer);                      // multi-page if n-pages metadata set
await image.SaveTiffAsync(writer, pyramid: true);       // pyramidal Ptif for deep-zoom viewers
await image.SaveHeifAsync(writer, quality: 75);
await image.SaveAvifAsync(writer, quality: 50);
await image.SaveGifAsync(writer);                       // single or animated
await image.SaveApngAsync(writer);                      // single or animated
await image.SaveBmpAsync(writer);                       // 24bpp BGR / 32bpp BGRA, BI_RGB
await image.SaveTgaAsync(writer);                       // type 2/3 uncompressed top-to-bottom
await image.SaveQoiAsync(writer);                       // QOI v1.0 lossless
await image.SavePnmAsync(writer);                       // Auto: picks PBM/PGM/PPM/PAM by bands

// Scientific / niche
await image.SaveHdrAsync(writer);                       // Radiance HDR (RLE-encoded RGBE)
await image.SaveFitsAsync(writer);                      // FITS — UChar→BITPIX 8, Float→-32
await image.SaveNiftiAsync(writer);                     // NIfTI-1 single-file (.nii)

// Multi-resolution output (writes a directory tree, not a stream)
await image.SaveDeepZoomAsync("/tmp/img");              // /tmp/img.dzi + /tmp/img_files/
```

---

## Animation & multi-page

Loading an animated GIF, transforming each frame, and saving back works
without any manual frame iteration:

```csharp
await using var source = new PipeVipsSource(PipeReader.Create(File.OpenRead("anim.gif")));
var animated = await VipsGifLoader.LoadAsync(source);
// animated.Height == originalFrameHeight × nPages
// animated.Metadata["n-pages"]      = "12"
// animated.Metadata["page-height"]  = "240"
// animated.Metadata["animation-delays"] = "10,10,10,..."

var resized = animated
    .Resize(0.5, kernel: VipsKernel.Lanczos3)
    .Saturate(1.2);
// page-height scales proportionally; the saver re-derives layout from metadata

await resized.SaveGifAsync(outWriter);
```

Cross-format works too. PDF → multi-page TIFF:

```csharp
var pdf = await VipsPdfLoader.LoadAsync(source, page: 0, n: -1, dpi: 150);
await pdf.AutoOrient().SaveTiffAsync(tiffWriter);  // multi-page TIFF
```

---

## Metadata round-trip

EXIF / XMP / ICC blobs are captured at load time and embedded at save time
across every supported format pair. The library doesn't decode the
metadata into structured fields — it preserves the raw byte segments, so
nothing is lost in translation:

```csharp
var jpeg = await VipsJpegLoader.LoadAsync(source);
// jpeg.MetadataBlobs["exif"]  = raw TIFF EXIF bytes
// jpeg.MetadataBlobs["xmp"]   = raw XMP packet
// jpeg.MetadataBlobs["icc"]   = ICC profile bytes (concatenated from multi-segment APP2)

var avif = jpeg.AutoOrient().Resize(0.5, kernel: VipsKernel.Lanczos3);
// All three blobs ride along through the pipeline (CopyMetadataFrom on every op)

await avif.SaveAvifAsync(writer);
// Output AVIF carries the same EXIF/XMP/ICC; the EXIF orientation tag is
// patched to "1" because AutoOrient already rotated the pixels.
```

Cross-format conversion preserves all three blob types between JPEG, PNG,
WebP, TIFF, HEIF, and AVIF. PNG XMP via `iTXt` and animated-format
metadata are TODOs.

---

## Common recipes

### Phone-photo thumbnail (the canonical workflow)

```csharp
await using var src = new PipeVipsSource(PipeReader.Create(uploadStream));
var jpeg = await VipsJpegLoader.LoadAsync(src);
if (jpeg is null) return;

var thumb = jpeg
    .AutoOrient()                                       // sideways → upright
    .Resize(0.25, kernel: VipsKernel.Lanczos3)          // sharp Lanczos
    .Saturate(1.05);                                    // mild pop

await thumb.SaveJpegAsync(thumbWriter, quality: 80);
// EXIF datetime, GPS, camera info preserved; orientation tag = 1
```

### Multi-format `<picture>` srcset

```csharp
var resized = original.AutoOrient().Resize(0.5, kernel: VipsKernel.Lanczos3);

await resized.SaveJpegAsync(jpegWriter, quality: 80);
await resized.SaveWebpAsync(webpWriter, quality: 75);
await resized.SaveAvifAsync(avifWriter, quality: 50);
// Same source, same transform chain, three modern formats.
```

### Avatar via content-aware crop

```csharp
var avatar = original
    .AutoOrient()
    .EntropyCrop(512, 512)                              // focus on the subject
    .Resize(0.25, kernel: VipsKernel.Lanczos3);

await avatar.SaveJpegAsync(writer, quality: 85);
```

### Color-correct downscale (avoids resize halos)

```csharp
var output = source
    .Linearize()                                        // gamma → linear
    .Resize(0.25, kernel: VipsKernel.Lanczos3)
    .GaussBlur(0.5)
    .Delinearize();                                     // linear → gamma
```

### Privacy redaction

```csharp
var redacted = photo
    .ExtractArea(faceLeft, faceTop, faceWidth, faceHeight)
    .Pixelate(blockSize: 24);
```

### Watermark with sub-pixel positioning

```csharp
var watermarked = baseImage.Composite(logo, x: 100.5, y: 50.25);  // smooth offset
```

### Style chain

```csharp
var styled = portrait
    .Lightness(-0.05)
    .Saturate(0.85)
    .Vignette(0.3)
    .Glow(sigma: 6.0, strength: 0.2)
    .Sepia();
```

### High-precision linear-light resize (full Float)

```csharp
// No UChar quantization at any intermediate stage. The Float Linearize
// uses the per-pixel sRGB transfer (IEC 61966-2-1) instead of a 256-entry
// LUT — measurably more accurate in the low range.
var output = source
    .CastFloat()
    .Linear(new[] { 1.0 / 255 }, new[] { 0.0 })   // normalize to [0, 1]
    .Linearize()
    .Resize(0.25, kernel: VipsKernel.Lanczos3)
    .GaussBlur(0.5)
    .Delinearize()
    .Linear(new[] { 255.0 }, new[] { 0.0 })       // back to UChar range
    .CastUChar();

await output.SaveJpegAsync(writer, quality: 85);
```

### Pixel-level edits via typed access

```csharp
var typed = imageRgba32.ToTypedImage<Rgba32>();   // materializes once

// Tight loop over a row — zero-copy via MemoryMarshal.Cast
for (int y = 0; y < typed.Height; y++)
{
    foreach (ref Rgba32 px in typed.RowSpan(y))
    {
        // e.g. set non-opaque pixels to red
        if (px.A < 128) px = new Rgba32(255, 0, 0, 255);
    }
}

await typed.AsVipsImage().SavePngAsync(writer);
```

### Microscopy / pathology workflow

```csharp
// Load a slide, read OME-XML metadata, resize, write back with metadata intact
var slide = await VipsTiffLoader.LoadAsync(slideSource);
if (VipsOmeTiff.IsOmeTiff(slide))
{
    var sz = VipsOmeTiff.GetOmePhysicalSize(slide);
    Console.WriteLine($"PhysicalSize: {sz?.X}×{sz?.Y} {sz?.Unit}");
}

var thumb = slide.Resize(0.1).SaveDeepZoomAsync("/tmp/slide");
// /tmp/slide.dzi + /tmp/slide_files/{0,1,...,N}/{c}_{r}.jpg — OpenSeadragon-ready
```

### Astronomy: HDR-range Float pipeline

```csharp
// Load a FITS image with raw Float pixel values (no [0,255] clamp).
var fits = await VipsFitsLoader.LoadAsync(source);

// Resize while preserving the HDR range — the Float pipeline keeps
// out-of-[0,255] values intact through every op.
var thumb = fits.Resize(0.5);

// Save as HDR for use by tone-mapping tools
await thumb.SaveHdrAsync(hdrWriter);
```

### Multi-page document round-trip

```csharp
var doc = await VipsPdfLoader.LoadAsync(pdfSrc, page: 0, n: -1, dpi: 200);

var processed = doc
    .Greyscale()
    .Resize(0.5, kernel: VipsKernel.Lanczos3);

await processed.SaveTiffAsync(tiffWriter);              // multi-page TIFF, deflate compressed
```

---

## Project layout

```
CosmoImage/
├── Core/                   Image / Region / Sink / Cache / Kernels / Operation base / fluent extensions
│                           IPixel + TypedImage<TPixel>, IVipsAllocator, VipsFields, VipsOmeTiff,
│                           VipsSourceStream
├── Loaders/                JPEG / PNG / WebP / TIFF / BMP / GIF / HEIF / JP2K / JXL / PDF / SVG
│                           + HDR / FITS / NIfTI / Matlab / CSV / TGA / QOI / PBM-PAM
├── Savers/                 JPEG / PNG / WebP / TIFF / HEIF/AVIF / GIF / APNG
│                           + BMP / TGA / QOI / PNM / HDR / FITS / NIfTI / DeepZoom
└── Operations/
    ├── Geometric/          Resize, Resize1D, Affine, Rotate, Flip, Shrink, Crop, EntropyCrop,
    │                       AutoOrient, Thumbnail
    ├── Color/              Brightness, Contrast, Lightness, Saturate, Hue, Sepia, Greyscale,
    │                       Linearize, Delinearize, Recomb, Linear, Gamma, Colourspace, IccTransform
    ├── Convolution/        Conv, Conv1D, GaussBlur, UnsharpMask, Morph (Dilate/Erode/Open/Close),
    │                       Rank/Median, BokehBlur
    ├── Drawing/            Composite (sub-pixel), DrawLine (AA), DrawRect, Text
    ├── Effects/            Vignette, Pixelate, Glow, OilPaint, Charcoal, Sketch, Polaroid
    ├── Analysis/           HistFind/Cum/Norm/Equal, FwFft, InvFft, Spectrum, Stats
    └── Misc/               Cast (UChar↔Float), Math suite, Boolean/Relational, Maplut, Quantize
```

All source files use `namespace CosmoImage;` regardless of folder — folder
structure is for code organization, not API surface.

---

## Build & test

```bash
dotnet build CosmoImage.csproj
dotnet test  Tests/CosmoImage.Tests.csproj
```

Target framework: **net10.0**. 233 tests cover loaders, savers, ops,
the typed-pixel layer, the allocator path, and round-trip equivalence
between lazy and streaming load.

---

## Dependencies

Every package is permissive (MIT / Apache-2.0 / BSD / public domain):

| Package | Role |
| :--- | :--- |
| `Magick.NET-Q8-arm64` | HEIF/AVIF/WebP/TIFF/GIF/SVG/BMP codecs + profile API; quantization |
| `JpegLibrary` | JPEG codec |
| `StbImageSharp` | PNG decode (lightweight) |
| `Docnet.Core` | PDF render |
| `MetadataExtractor` | JPEG EXIF parsing |
| `MathNet.Numerics` | FFT |
| `System.IO.Hashing` | PNG CRC32 |

There is **no Six Labors / ImageSharp** dependency. The library was
explicitly migrated off it to escape the Six Labors Split License for
commercial use.

---

## What's missing

CosmoImage covers the mainline image-service, document-processing, and
photo-editing workloads end-to-end (load → orient → resize → composite
→ save with full Float-throughout colour-correct intermediates). The
honest gap against either parent is *much* bigger than that:

- vs **libvips** (300+ ops across 12 subsystems): whole subsystems
  missing (mosaicing/, create/), a severe colour-management graph
  gap (only sRGB↔linear, missing Lab/Oklab/CMYK/etc.), many
  band-manipulation ops, several niche format codecs that need native
  bindings (OpenEXR, JPEG XL/2K, OpenSlide, dcraw, DICOM).
- vs **ImageSharp** (~25 pixel structs, full vector-graphics drawing,
  typed metadata profiles): pixel-format zoo (4 of 25 shipped), no
  generic op surface, no vector-graphics drawing layer (paths,
  brushes, gradients, glyph shaping), raw-bytes-only metadata.

See [`PARITY_MATRIX.md`](./PARITY_MATRIX.md) and
[`IMAGESHARP_PARITY.md`](./IMAGESHARP_PARITY.md) for honest matrices,
and [`TODO_PARITY.md`](./TODO_PARITY.md) for the actionable work map.

---

## License

See repository root for license terms.
