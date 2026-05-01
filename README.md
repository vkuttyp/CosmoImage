# CosmoImage

A C# / .NET 10 image-processing library with the architecture of
[libvips](https://www.libvips.org) and the surface area of
[ImageSharp](https://github.com/SixLabors/ImageSharp), under a
permissive-only dependency stack.

- **Lazy demand-driven pipeline** — operations don't compute pixels until a
  sink (saver, materialization, or export) consumes them.
- **Sink-driven multi-stage parallelism** — one threadpool drains the entire
  pipeline; parallelism scales with consumer demand, not per-op fork-join.
- **Zero-copy memory-image dtype** — loaders expose pixels via shared
  buffers; downstream `Prepare` aliases instead of allocating per tile.
- **Modern format support** — JPEG, PNG, WebP, TIFF, BMP, GIF, HEIF, AVIF,
  PDF render, SVG raster — all single-frame and (where applicable) animated
  / multi-page round-trip.
- **Metadata-aware** — EXIF, XMP, and ICC blobs round-trip across every
  format conversion. AutoOrient patches the EXIF orientation tag so your
  thumbnails don't double-rotate.
- **Permissive licensing only** — no Six Labors split-license dependency.

For a feature-by-feature comparison see
[`PARITY_MATRIX.md`](./PARITY_MATRIX.md). For the outstanding work list see
[`TODO_PARITY.md`](./TODO_PARITY.md).

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

| Format | Load (header) | Load (pixels) | Animated/multi-page | Save | Multi-frame save |
| :--- | :---: | :---: | :---: | :---: | :---: |
| JPEG | ✅ | ✅ | n/a | ✅ | n/a |
| PNG | ✅ | ✅ | n/a | ✅ (full + palette PNG-8) | n/a |
| WebP | ✅ | ✅ | ✅ | ✅ | ✅ |
| TIFF | ✅ | ✅ | ✅ multi-page | ✅ | ✅ multi-page |
| BMP | ✅ | ✅ | n/a | ❌ | n/a |
| GIF | ✅ | ✅ | ✅ | ✅ | ✅ |
| HEIF / AVIF | ✅ | ✅ | ❌ sequences | ✅ | ❌ |
| APNG | n/a | n/a | n/a | ✅ | ✅ |
| PDF | ✅ | ✅ render | ✅ multi-page render | n/a | n/a |
| SVG | ✅ | ✅ raster | n/a | n/a | n/a |
| JPEG XL | 🟡 stub | ❌ | n/a | ❌ | n/a |
| JPEG 2000 | 🟡 header only | ❌ | n/a | ❌ | n/a |

EXIF / XMP / ICC blobs round-trip on JPEG, PNG, WebP, TIFF, HEIF, and AVIF.

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

// Color-correct linear-light pipeline
image
    .Linearize()                          // sRGB → linear via IEC 61966-2-1
    .Resize(0.25, kernel: VipsKernel.Lanczos3)
    .GaussBlur(0.7)
    .Delinearize();                       // linear → sRGB

image.Quantize(colors: 64, dither: true); // Wu/median-cut palette reduction
image.Maplut(lutImage);

// Color management
image.Colourspace(VipsInterpretation.Lab);
image.IccTransform(targetIccProfile, inputIccProfile);  // round-trip via Magick.NET
```

### Convolution & morphology

```csharp
image.Conv(mask);                          // 2D mask
image.Conv1D(kernel, vertical: false);     // separable building block
image.GaussBlur(2.0);                      // 2-pass separable
image.UnsharpMask(sigma: 1.0, amount: 0.8);

image.Dilate(structuringElement);
image.Erode(structuringElement);
image.Morph(mask, VipsMorphMethod.Dilate);
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

// Magick.NET-backed artistic effects
image.OilPaint(radius: 3.0, sigma: 1.0);
image.Charcoal(radius: 1.0, sigma: 0.5);
image.Sketch(radius: 1.0, sigma: 0.5, angle: 0);
image.Polaroid(angle: -5);                 // RGBA output, sized to rotated bbox
```

### Analysis & frequency

```csharp
var hist = image.HistFind();          // per-band 256-bin histogram
var cum = hist.HistCum();              // cumulative
var norm = cum.HistNorm();             // normalized for use as a LUT
var equalized = image.HistEqual();     // = Maplut(image, HistNorm(HistCum(HistFind(image))))

var fft = image.FwFft();               // forward 2D FFT (DPComplex output)
```

---

## Saving

```csharp
await image.SaveJpegAsync(writer, quality: 85);
await image.SavePngAsync(writer);                       // full-color
await image.SavePngAsync(writer, palette: 64);          // palette PNG-8 with quantization
await image.SaveWebpAsync(writer, quality: 75, lossless: false);
await image.SaveTiffAsync(writer);                      // multi-page if n-pages metadata set
await image.SaveHeifAsync(writer, quality: 75);
await image.SaveAvifAsync(writer, quality: 50);
await image.SaveGifAsync(writer);                       // single or animated
await image.SaveApngAsync(writer);                      // single or animated
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
├── Loaders/                One file per format: JPEG, PNG, WebP, TIFF, BMP, GIF, HEIF, JP2K, JXL, PDF, SVG
├── Savers/                 JPEG, PNG, WebP, TIFF, HEIF/AVIF, GIF, APNG
└── Operations/
    ├── Geometric/          Resize, Affine, Rotate, Flip, Crop, EntropyCrop, AutoOrient, Thumbnail, …
    ├── Color/              Brightness, Contrast, Lightness, Saturate, Hue, Sepia, Linearize, Recomb, …
    ├── Convolution/        Conv, GaussBlur, UnsharpMask, Morph
    ├── Drawing/            Composite (sub-pixel), DrawLine (AA), DrawRect, Text
    ├── Effects/            Vignette, Pixelate, Glow, OilPaint, Charcoal, Sketch, Polaroid
    ├── Analysis/           HistFind/Cum/Norm/Equal, FwFft
    └── Misc/               Maplut, Quantize
```

All source files use `namespace CosmoImage;` regardless of folder — folder
structure is for code organization, not API surface.

---

## Build & test

```bash
dotnet build CosmoImage.csproj
dotnet test  Tests/CosmoImage.Tests.csproj
```

Target framework: **net10.0**. 54 tests cover all loaders, savers, and core
ops.

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

The library covers the mainline image-service, document-processing, and
photo-editing workloads end-to-end. Remaining gaps fall into four
categories:

- **Architectural lifts** — `Image<TPixel>` strong typing, Float-format
  ops throughout, a proper PCS-based ICC color management module, caller-
  supplied `MemoryAllocator` hooks.
- **Niche formats** — JPEG XL/JPEG 2000 pixel decode, OpenEXR / Radiance
  HDR, FITS / NIfTI, TGA / QOI / PBM.
- **Niche features** — animated AVIF / HEIC sequences, BokehBlur, TIFF
  pyramidal output (`dzsave`), proper glyph shaping for `Text`.
- **Quality-of-life** — pointwise math/boolean/relational ops, stats
  (`avg`/`min`/`max`), inverse FFT, morphology compositions
  (open/close/rank).

See [`TODO_PARITY.md`](./TODO_PARITY.md) for the full prioritized list and
[`PARITY_MATRIX.md`](./PARITY_MATRIX.md) for the detailed coverage table.

---

## License

See repository root for license terms.
