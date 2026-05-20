# Contributing to CosmoImage

This file captures the dependency and testing policies that aren't obvious
from reading the source. For feature parity goals see
[`PARITY_MATRIX.md`](./PARITY_MATRIX.md); for what's outstanding see
[`TODO_PARITY.md`](./TODO_PARITY.md).

## Dependency policy

**Rule: zero `using ImageMagick;` in production source.** Enforced today —
the `Loaders/`, `Savers/`, `Operations/`, `Core/` trees contain no
ImageMagick references and `CosmoImage.csproj` has no `Magick.NET`
`PackageReference`. CosmoImage's identity is "permissive-only dependency
stack" — Magick.NET in the ship surface contradicts that.

### Magick.NET is allowed *only* in `Tests/`

The test project (`Tests/CosmoImage.Tests.csproj`) keeps Magick.NET as an
**oracle**: it synthesises reference fixtures (e.g. golden ICC-transformed
images, comparison encodes) so our pure-managed encoder/decoder output
can be verified against an authoritative reference. This dependency is
acceptable because:

- Test projects are not packed (`IsPackable=false`).
- Downstream consumers of the `CosmoImage` NuGet package never see the
  Magick.NET transitive reference.
- Replacing the oracle with committed binary fixtures would bloat the repo
  by tens of MB and obscure intent — a fixture file is opaque; a
  Magick-generated reference is reproducible from code.

The reference is declared explicitly in `Tests/CosmoImage.Tests.csproj`
with an inline comment naming the policy.

### HEIF / AVIF — *not implemented*

`VipsHeifLoader` recognises the format and parses ISOBMFF dimensions but
its `LoadAsync` / `LoadStreamingAsync` return `null` (the dispatch layer
treats null as "I can't decode this"). `VipsHeifSaver.SaveHeifAsync` /
`SaveAvifAsync` throw `NotSupportedException` with a clear message. A
real implementation requires pure-managed HEVC + AV1 codecs, which is a
multi-year project — see Phase D in the implementation plan.

### Review rule

Any new production code introducing `using ImageMagick;` or a Magick.NET
`PackageReference` outside `Tests/` should be rejected in review.

## Adding a new pure-managed codec

The pattern established by WebP (this session) and PureJpegDecoder /
PureTiffDecoder (earlier work) is:

1. **Decoder first**: build a `PureXxxDecoder` class that parses the byte
   stream into a `VipsImage`. Returns `null` for malformed or
   out-of-scope inputs. Aim for spec compliance over performance for the
   first cut.
2. **Encoder second**: build a `PureXxxEncoder` mirroring the decoder's
   structure. Hand-write a round-trip test that encodes a known input
   then re-decodes through the decoder.
3. **Wire the loader/saver**: `Loaders/VipsXxxLoader.cs` and
   `Savers/VipsXxxSaver.cs` delegate to the Pure classes. Drop any
   `using ImageMagick;` import. Throw `NotSupportedException` with a
   feature-specific message for capabilities not yet covered — never
   silently degrade.
4. **Register in `Loaders/BuiltInImageFormats.cs`** if it isn't already.

## Tests

The test project mirrors production global usings (see
`Tests/CosmoImage.Tests.csproj`). Both Debug and Release builds enable
`AllowUnsafeBlocks=true` for `Span<T>` and pointer interop in some
loaders.

When a test depends on Magick.NET, add a doc comment explaining what role
the dependency plays (oracle, fixture generator, etc.) so future cleanup
passes can categorize it correctly.
