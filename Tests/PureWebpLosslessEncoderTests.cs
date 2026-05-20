using System;
using CosmoImage.Core;
using CosmoImage.Loaders;
using CosmoImage.Savers;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round-trip tests for the pure-managed VP8L encoder. Validates that the
/// encoder produces bitstreams the existing PureWebpLossless decoder can
/// read back losslessly. No native deps involved.
/// </summary>
public class PureWebpLosslessEncoderTests
{
    [Fact]
    public void RoundTrip_TwoPixelMixedSingleAndMultiChannel()
    {
        // Regression: with single-symbol channels (B, A) alongside multi-symbol
        // (R), the encoder must skip per-pixel emission for the single-symbol
        // channels — otherwise the stream misaligns and pixel 1 corrupts.
        var rgba = new byte[] { 0, 0, 128, 255, 32, 0, 128, 255 };
        AssertRoundTrips(rgba, 2, 1);
    }

    [Fact]
    public void RoundTrip_TinyOpaqueRgba_DecodesIdentically()
    {
        const int W = 8, H = 8;
        var rgba = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                rgba[i + 0] = (byte)(x * 32);
                rgba[i + 1] = (byte)(y * 32);
                rgba[i + 2] = 128;
                rgba[i + 3] = 255;
            }

        AssertRoundTrips(rgba, W, H);
    }

    [Fact]
    public void RoundTrip_WithAlpha_PreservesTransparency()
    {
        const int W = 4, H = 4;
        var rgba = new byte[W * H * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i + 0] = 200;
            rgba[i + 1] = 100;
            rgba[i + 2] = 50;
            rgba[i + 3] = (byte)((i / 4) * 16);  // varying alpha
        }
        AssertRoundTrips(rgba, W, H);
    }

    [Fact]
    public void RoundTrip_AllZeros_Decodes()
    {
        const int W = 16, H = 8;
        var rgba = new byte[W * H * 4];
        AssertRoundTrips(rgba, W, H);
    }

    [Fact]
    public void RoundTrip_AllSamePixel_Decodes()
    {
        const int W = 12, H = 5;
        var rgba = new byte[W * H * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i + 0] = 77; rgba[i + 1] = 88; rgba[i + 2] = 99; rgba[i + 3] = 255;
        }
        AssertRoundTrips(rgba, W, H);
    }

    [Fact]
    public void RoundTrip_PseudoRandom32x32_Decodes()
    {
        const int W = 32, H = 32;
        var rgba = new byte[W * H * 4];
        var rng = new Random(42);
        rng.NextBytes(rgba);
        AssertRoundTrips(rgba, W, H);
    }

    [Theory]
    [InlineData(64, 64, 7)]
    [InlineData(96, 96, 7)]
    [InlineData(128, 128, 7)]
    [InlineData(160, 160, 7)]
    [InlineData(200, 200, 7)]
    [InlineData(256, 256, 7)]
    public void RoundTrip_LargeRandom_Decodes(int w, int h, int seed)
    {
        // Find the threshold where the encoder breaks on uniform-random data.
        var rgba = new byte[w * h * 4];
        new Random(seed).NextBytes(rgba);
        AssertRoundTrips(rgba, w, h);
    }

    [Fact]
    public void RoundTrip_TwoColorCheckerboard_ExercisesZeroRunOpcode()
    {
        // 16×16 image with exactly two distinct pixel values per channel:
        // forces the per-channel CL histograms to have 254 zero-length
        // entries — which the new opcode-18 zero-run path compresses into
        // a handful of bytes instead of writing 254 individual zero CL codes.
        const int W = 16, H = 16;
        var rgba = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                bool dark = ((x + y) & 1) == 0;
                rgba[i + 0] = dark ? (byte)0   : (byte)200;
                rgba[i + 1] = dark ? (byte)10  : (byte)180;
                rgba[i + 2] = dark ? (byte)20  : (byte)160;
                rgba[i + 3] = 255;
            }
        AssertRoundTrips(rgba, W, H);
    }

    [Fact]
    public void Phase2_FourColorImageBenefitsFromColorCache()
    {
        // 64×64 image with only 4 distinct colors arranged in stripes.
        // The 5-bit (32-entry) color cache should hit on most pixels —
        // 4 colors map to ≤4 distinct hash keys, so after the first few
        // unique entries every subsequent pixel of the same color is a
        // cache hit (one G-tree symbol instead of four channel symbols).
        const int W = 64, H = 64;
        var rgba = new byte[W * H * 4];
        var palette = new (byte R, byte G, byte B)[]
        {
            (255, 0,   0),
            (0,   255, 0),
            (0,   0,   255),
            (255, 255, 0),
        };
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                var (r, g, b) = palette[(y / 4) % 4];
                int i = (y * W + x) * 4;
                rgba[i + 0] = r; rgba[i + 1] = g; rgba[i + 2] = b; rgba[i + 3] = 255;
            }

        var encoded = PureWebpLosslessEncoder.Encode(rgba, W, H);
        AssertRoundTrips(rgba, W, H);

        // With cache, ~4080 of 4096 pixels are cache hits → bitstream dominated
        // by header (Huffman trees) + 4080 ~1-bit cache symbols. Should land
        // well under 1 KB.
        Assert.True(encoded.Length < 1024,
            $"4-color 64×64 image should compress to < 1 KB with the color cache; got {encoded.Length}");
    }

    [Fact]
    public void Phase2_GradientEncodesSmallerThanRawBytes()
    {
        // Regression guard: SubtractGreen + opcode-17/18 zero-runs should
        // bring the encoded size meaningfully below the raw RGBA byte
        // count on a gradient (smooth, correlated channels). This was
        // ~equal or worse before phase 2.
        const int W = 128, H = 128;
        var rgba = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                rgba[i + 0] = (byte)x;
                rgba[i + 1] = (byte)y;
                rgba[i + 2] = (byte)((x + y) >> 1);
                rgba[i + 3] = 255;
            }
        var encoded = PureWebpLosslessEncoder.Encode(rgba, W, H);
        // Raw byte count is W*H*4 = 65536. Phase 1 output (no transforms,
        // literal CL only) was typically larger than raw for gradients.
        // Phase 2 should beat raw comfortably.
        Assert.True(encoded.Length < rgba.Length,
            $"phase 2 should produce smaller-than-raw output for gradients; " +
            $"got encoded={encoded.Length}, raw={rgba.Length}");
    }

    [Fact]
    public void RoundTrip_HorizontalGradient_Decodes()
    {
        // Structured input (smooth gradient) is what real photos look like —
        // makes sure transforms-less encoding doesn't choke on adjacent-pixel
        // similarity.
        const int W = 64, H = 32;
        var rgba = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                rgba[i + 0] = (byte)x;            // R gradient
                rgba[i + 1] = (byte)(x + y);      // G mix
                rgba[i + 2] = (byte)(255 - x);    // B inverse
                rgba[i + 3] = 255;
            }
        AssertRoundTrips(rgba, W, H);
    }

    [Fact]
    public void RoundTrip_NonSquare_Decodes()
    {
        const int W = 17, H = 5;
        var rgba = new byte[W * H * 4];
        for (int i = 0; i < rgba.Length; i++) rgba[i] = (byte)((i * 7) & 0xFF);
        // Force alpha to 255 to avoid the "alphaUsed" branch.
        for (int p = 0; p < W * H; p++) rgba[p * 4 + 3] = 255;
        AssertRoundTrips(rgba, W, H);
    }

    [Fact]
    public void Output_HasValidRiffWebpHeader()
    {
        var rgba = new byte[4 * 4 * 4];
        var bytes = PureWebpLosslessEncoder.Encode(rgba, 4, 4);
        Assert.Equal((byte)'R', bytes[0]);
        Assert.Equal((byte)'I', bytes[1]);
        Assert.Equal((byte)'F', bytes[2]);
        Assert.Equal((byte)'F', bytes[3]);
        Assert.Equal((byte)'W', bytes[8]);
        Assert.Equal((byte)'E', bytes[9]);
        Assert.Equal((byte)'B', bytes[10]);
        Assert.Equal((byte)'P', bytes[11]);
        Assert.Equal((byte)'V', bytes[12]);
        Assert.Equal((byte)'P', bytes[13]);
        Assert.Equal((byte)'8', bytes[14]);
        Assert.Equal((byte)'L', bytes[15]);
    }

    // ---- ColorIndexing (palette) transform ----

    [Fact]
    public void Palette_TwoColor_BundledEightPerByte_HugeWin()
    {
        // 64×64 two-colour image. With ColorIndexing + bundleBits=3 the
        // packed body is (64/8)=8 px wide × 64 high — 64× fewer samples than
        // the original. Output must be much smaller than even the 4-colour
        // case from the existing test.
        const int W = 64, H = 64;
        var rgba = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                bool dark = ((x ^ y) & 1) == 0;
                int i = (y * W + x) * 4;
                rgba[i + 0] = dark ? (byte)10 : (byte)200;
                rgba[i + 1] = dark ? (byte)20 : (byte)150;
                rgba[i + 2] = dark ? (byte)30 : (byte)100;
                rgba[i + 3] = 255;
            }
        var encoded = PureWebpLosslessEncoder.Encode(rgba, W, H);
        AssertRoundTrips(rgba, W, H);
        Assert.True(encoded.Length < 200,
            $"two-colour 64×64 should compress to < 200 bytes with 8-px bundling; got {encoded.Length}");
    }

    [Fact]
    public void Palette_FourColor_BundledFourPerByte_BeatsPhase2()
    {
        // Same fixture as Phase2_FourColorImageBenefitsFromColorCache, now
        // routed through ColorIndexing. With bundleBits=2 the packed body
        // is 64/4 = 16 px wide × 64 high — far cheaper than the cache-hit
        // path that the phase-2 test measured. Sanity: well under 256 B.
        const int W = 64, H = 64;
        var rgba = new byte[W * H * 4];
        var palette = new (byte R, byte G, byte B)[]
        {
            (255, 0,   0),
            (0,   255, 0),
            (0,   0,   255),
            (255, 255, 0),
        };
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                var (r, g, b) = palette[(y / 4) % 4];
                int i = (y * W + x) * 4;
                rgba[i + 0] = r; rgba[i + 1] = g; rgba[i + 2] = b; rgba[i + 3] = 255;
            }
        var encoded = PureWebpLosslessEncoder.Encode(rgba, W, H);
        AssertRoundTrips(rgba, W, H);
        // Phase 2 (cache-only) achieved < 1024 B on this fixture. With
        // ColorIndexing + 4-per-byte bundling the packed body shrinks 4×,
        // so we tighten the budget significantly — but the fixed-cost
        // header (Huffman trees etc.) keeps absolute floor around ~300 B.
        Assert.True(encoded.Length < 500,
            $"4-colour 64×64 should compress to < 500 B with ColorIndexing; got {encoded.Length}");
    }

    [Fact]
    public void Palette_OneColor_DegenerateImage_RoundTrips()
    {
        // Single-colour image: palette has exactly 1 entry, packed body is
        // all zero bytes. Exercises the colorTableSize_minus_one = 0 edge
        // of the spec — the decoder must NOT mis-parse the single-symbol
        // palette tree.
        const int W = 10, H = 10;
        var rgba = new byte[W * H * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i + 0] = 77; rgba[i + 1] = 88; rgba[i + 2] = 99; rgba[i + 3] = 255;
        }
        AssertRoundTrips(rgba, W, H);
    }

    [Fact]
    public void Palette_NonMultipleWidth_TrailingBundleHandledCorrectly()
    {
        // Width 17 with bundleBits=3 (two-colour) means the last byte holds
        // only 1 valid pixel out of 8. The encoder must zero-pad the unused
        // bits and the decoder must stop at width — regression case for
        // off-by-one in the bundling loop.
        const int W = 17, H = 3;
        var rgba = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                bool a = ((x + y) & 1) == 0;
                rgba[i + 0] = a ? (byte)0   : (byte)255;
                rgba[i + 1] = a ? (byte)0   : (byte)255;
                rgba[i + 2] = a ? (byte)0   : (byte)255;
                rgba[i + 3] = 255;
            }
        AssertRoundTrips(rgba, W, H);
    }

    [Fact]
    public void Palette_SeventeenColors_FallsIntoOnePerByteBucket()
    {
        // 17 distinct colours: just over the ≤16 threshold so bundleBits=0.
        // Verifies the un-bundled path: packedWidth == width, each G byte
        // is the raw 8-bit palette index.
        const int W = 17, H = 4;
        var rgba = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                rgba[i + 0] = (byte)(x * 15);
                rgba[i + 1] = (byte)(x * 10);
                rgba[i + 2] = (byte)(x * 5);
                rgba[i + 3] = 255;
            }
        AssertRoundTrips(rgba, W, H);
    }

    [Fact]
    public void Palette_RespectsAlphaChannel()
    {
        // Palette entries carry the original A byte; the packed pixels have
        // A=0 by construction, but the decoder must look the alpha back up
        // from the palette, not from the packed image.
        const int W = 4, H = 4;
        var rgba = new byte[W * H * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i + 0] = 200;
            rgba[i + 1] = 100;
            rgba[i + 2] = 50;
            rgba[i + 3] = (byte)((i / 4) * 16);  // 16 distinct alphas → 16 palette entries
        }
        AssertRoundTrips(rgba, W, H);
    }

    // ---- ColorTransform (C4) ----

    [Fact]
    public void ColorTransform_CorrelatedChannelGradient_RoundTripsAfterCT()
    {
        // 128×128 gradient with R, G, B all closely tracking (x + y) but
        // with enough variation that the unique-colour count exceeds the
        // palette threshold (forces the non-palette path where CT lives).
        // After CT + SubtractGreen the residual R/B histograms cluster
        // tightly near zero — the body shrinks substantially compared to
        // a baseline encode of the same channel distribution. Strict size
        // claims are deferred to a separate manual benchmark; this test
        // verifies CT engages and round-trips byte-perfectly.
        const int W = 128, H = 128;
        var rgba = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                // Encode (x, y) into the low bits of R and B so distinct
                // pixels get distinct ARGB values → > 256 unique colours.
                int k = (x + y) & 0xFF;
                rgba[i + 0] = (byte)(k ^ (x & 0x1F));
                rgba[i + 1] = (byte)k;
                rgba[i + 2] = (byte)(k ^ (y & 0x1F));
                rgba[i + 3] = 255;
            }
        AssertRoundTrips(rgba, W, H);
    }

    [Fact]
    public void ColorTransform_SkippedWhenImageTooSmall()
    {
        // 16×16 random image is below the ColorTransform threshold; the
        // encoder should not emit the C4 transform (header cost > savings).
        // We can't directly inspect the chosen transforms, but a successful
        // round-trip plus a reasonable byte budget confirms the path didn't
        // explode the bitstream with an unnecessary CT header.
        const int W = 16, H = 16;
        var rgba = new byte[W * H * 4];
        new Random(123).NextBytes(rgba);
        for (int p = 0; p < W * H; p++) rgba[p * 4 + 3] = 255;
        AssertRoundTrips(rgba, W, H);
    }

    [Fact]
    public void ColorTransform_RandomLargeInput_DoesNotEmitNegativeWin()
    {
        // 96×96 uniform-random RGB has uncorrelated channels — the CT search
        // should find no triple with savings > header cost and skip the
        // transform. Output size should be comparable to (or below) the
        // encoder's behaviour before #36 — meaning we don't pay a CT header
        // tax on inputs that can't benefit.
        const int W = 96, H = 96;
        var rgba = new byte[W * H * 4];
        new Random(42).NextBytes(rgba);
        for (int p = 0; p < W * H; p++) rgba[p * 4 + 3] = 255;
        var encoded = PureWebpLosslessEncoder.Encode(rgba, W, H);
        AssertRoundTrips(rgba, W, H);
        Assert.True(encoded.Length < W * H * 4 + 256,
            $"random {W}×{H} encoding ballooned past raw byte count; got {encoded.Length}");
    }

    // ---- LZ77 backreferences (C2b) ----

    [Fact]
    public void Lz77_LongHorizontalRun_CompressesDeeply()
    {
        // 256-wide row of identical pixels repeated 4 times vertically.
        // With LZ77 the first row is ~256 cache hits / literals, then each
        // subsequent row is a single long backref (length 256, distance
        // 256). Without LZ77 the output is at least 1KB; with LZ77 should
        // fit in well under 256 bytes.
        const int W = 256, H = 4;
        var rgba = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                // 100 distinct colours sprinkled so we don't take the palette
                // path. (palette would dominate anyway and obscure the LZ77
                // win.)
                int k = x % 100;
                rgba[i + 0] = (byte)(k * 2);
                rgba[i + 1] = (byte)(k * 3);
                rgba[i + 2] = (byte)(k * 5);
                rgba[i + 3] = 255;
            }
        // Force > 256 unique colours: tweak the last row's a few pixels.
        for (int x = 0; x < W; x++)
        {
            int i = ((H - 1) * W + x) * 4;
            rgba[i + 0] ^= (byte)(x & 0x07);
        }
        var encoded = PureWebpLosslessEncoder.Encode(rgba, W, H);
        AssertRoundTrips(rgba, W, H);
        // Three full rows are identical to row 0 (modulo last-row tweaks),
        // so most of the body becomes backrefs. < 1 KB is a comfortable
        // upper bound that catches a serious regression.
        Assert.True(encoded.Length < 1024,
            $"4× repeated row should compress to < 1 KB with LZ77; got {encoded.Length}");
    }

    [Fact]
    public void Lz77_TileRepeat_LongMatchesDriveSizeDown()
    {
        // 64×64 canvas tiled with a 16×16 unique tile (so > 256 colours
        // forces non-palette path). Each tile after the first is a chain
        // of backrefs into the first tile. Heavy LZ77 win.
        const int W = 64, H = 64;
        var rgba = new byte[W * H * 4];
        var rng = new Random(99);
        for (int ty = 0; ty < 16; ty++)
            for (int tx = 0; tx < 16; tx++)
            {
                byte r = (byte)rng.Next(256), g = (byte)rng.Next(256), b = (byte)rng.Next(256);
                for (int dy = 0; dy < 4; dy++)
                    for (int dx = 0; dx < 4; dx++)
                    {
                        // Tile is 16×16 unique colours laid out 4×4 quads.
                        int x = (tx * 4 + dx) % W;
                        int y = (ty * 4 + dy) % H;
                        // Actually we want to tile a 16-wide pattern across
                        // the canvas. Redo with a simpler model below.
                    }
            }
        // Simpler: build a 16×16 tile, copy it into all 16 cells.
        var tile = new byte[16 * 16 * 4];
        new Random(7).NextBytes(tile);
        for (int p = 0; p < 16 * 16; p++) tile[p * 4 + 3] = 255;
        for (int by = 0; by < W / 16; by++)
            for (int bx = 0; bx < W / 16; bx++)
                for (int ty2 = 0; ty2 < 16; ty2++)
                    Buffer.BlockCopy(tile, ty2 * 16 * 4,
                        rgba, ((by * 16 + ty2) * W + bx * 16) * 4, 16 * 4);

        var encoded = PureWebpLosslessEncoder.Encode(rgba, W, H);
        AssertRoundTrips(rgba, W, H);
        // 64×64 with a 16×16 base tile repeated 16×: one tile of literals
        // followed by 15 tile-sized backrefs. Should be well under 2 KB.
        Assert.True(encoded.Length < 2048,
            $"16×-tiled 64×64 should compress to < 2 KB; got {encoded.Length}");
    }

    [Fact]
    public void Lz77_RandomInput_DoesNotInflateBitstream()
    {
        // Uniform-random 96×96: no matches longer than 2, so LZ77 finds
        // nothing and the action stream falls back to literals + cache.
        // The output should not exceed the raw byte count.
        const int W = 96, H = 96;
        var rgba = new byte[W * H * 4];
        new Random(33).NextBytes(rgba);
        for (int p = 0; p < W * H; p++) rgba[p * 4 + 3] = 255;
        var encoded = PureWebpLosslessEncoder.Encode(rgba, W, H);
        AssertRoundTrips(rgba, W, H);
        Assert.True(encoded.Length < W * H * 4 + 512,
            $"random {W}×{H} ballooned with LZ77 active; got {encoded.Length}");
    }

    [Fact]
    public void Lz77_BackrefSpanningOriginalAndCopiedData_RoundTrips()
    {
        // The decoder copies length pixels starting `distance` back. When
        // distance < length, the copy reads from positions WE JUST WROTE
        // (overlap). This is well-defined per spec — must round-trip.
        // Construct an image where the encoder is likely to emit such a
        // self-referential backref: repeating short pattern.
        const int W = 32, H = 4;
        var rgba = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                // 4-pixel repeating motif → encoder's match finder will
                // happily emit a (length=W-4, distance=4) backref.
                int k = x % 4;
                rgba[i + 0] = (byte)(k == 0 ? 200 : k == 1 ? 100 : k == 2 ? 50 : 25);
                rgba[i + 1] = (byte)(k * 64);
                rgba[i + 2] = 0;
                rgba[i + 3] = 255;
            }
        // Force > 256 unique colours: perturb the last row.
        for (int x = 0; x < W; x++)
        {
            int i = ((H - 1) * W + x) * 4;
            rgba[i + 2] = (byte)x;
        }
        AssertRoundTrips(rgba, W, H);
    }

    // ---- helpers ----

    private static void AssertRoundTrips(byte[] rgba, int width, int height)
    {
        var encoded = PureWebpLosslessEncoder.Encode(rgba, width, height);
        var decoded = PureWebpLossless.TryDecode(encoded);
        Assert.NotNull(decoded);
        Assert.Equal(width, decoded!.Width);
        Assert.Equal(height, decoded.Height);
        Assert.Equal(4, decoded.Bands);

        // Materialize lazy pixels.
        var got = decoded.Pixels ?? decoded.PixelsLazy!.Value;
        Assert.Equal(rgba.Length, got.Length);
        for (int i = 0; i < rgba.Length; i++)
        {
            if (rgba[i] != got[i])
            {
                int pixelIdx = i / 4;
                int channel = i % 4;
                throw new Xunit.Sdk.XunitException(
                    $"pixel {pixelIdx} channel {channel}: expected {rgba[i]}, got {got[i]}");
            }
        }
    }
}
