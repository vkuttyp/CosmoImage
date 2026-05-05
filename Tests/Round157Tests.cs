using System;
using CosmoImage.Loaders;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 157 — DCT primitives (8×8 forward / inverse DCT, JPEG
/// zigzag scan). Foundational pieces for the upcoming DWA
/// LOSSY_DCT channel path. Validates each primitive in isolation
/// before integration; the integration round assembles them with
/// Huffman + un-RLE + toLinear.
/// </summary>
public class Round157Tests
{
    [Fact]
    public void DctRoundTrip_RandomBlock_ReturnsOriginalWithinFloatPrecision()
    {
        // Forward DCT then inverse DCT should reconstruct the spatial
        // block within float-arithmetic precision (~1e-4 absolute).
        var rng = new Random(12345);
        var block = new float[64];
        var original = new float[64];
        for (int i = 0; i < 64; i++)
        {
            block[i] = (float)(rng.NextDouble() * 256.0 - 128.0);
            original[i] = block[i];
        }

        ExrDct.Forward8x8InPlace(block);
        ExrDct.Inverse8x8InPlace(block);

        for (int i = 0; i < 64; i++)
            Assert.True(Math.Abs(block[i] - original[i]) < 1e-3f,
                $"sample {i}: original={original[i]} reconstructed={block[i]}");
    }

    [Fact]
    public void DcOnlyBlock_IDCT_ProducesUniformPixels()
    {
        // DC-only block (only the [0,0] coefficient nonzero) inverse-DCTs
        // to a uniform spatial block. The output value is the DC
        // coefficient scaled by ½·(1/√2)·(1/√2) = ¼ — wait, actually
        // ½ · (1/√2) for each pass × 1 (no further sum since only one
        // nonzero) = ½ · (1/√2). Two passes squared = 1/8. The DC
        // coefficient of a uniform block of value v is 8v (forward DCT
        // sum over 8 ones), so a coefficient of D produces uniform D/8.
        var block = new float[64];
        block[0] = 800f;
        ExrDct.Inverse8x8InPlace(block);

        float expected = 800f / 8f;  // = 100
        for (int i = 0; i < 64; i++)
            Assert.True(Math.Abs(block[i] - expected) < 1e-3f,
                $"sample {i}: got {block[i]}, expected {expected}");
    }

    [Fact]
    public void Zigzag_AllPositionsCoveredExactlyOnce()
    {
        // The zigzag scan must be a bijection over {0..63}.
        var seen = new bool[64];
        Assert.Equal(64, ExrDct.ZigzagToRowMajor.Length);
        foreach (int p in ExrDct.ZigzagToRowMajor)
        {
            Assert.InRange(p, 0, 63);
            Assert.False(seen[p], $"position {p} appears more than once");
            seen[p] = true;
        }
        for (int i = 0; i < 64; i++)
            Assert.True(seen[i], $"position {i} missing from zigzag");
    }

    [Fact]
    public void Zigzag_StartsAtDcThenDiagonalSweep()
    {
        // First few zigzag positions per JPEG convention:
        // 0 → (0,0)  -- DC
        // 1 → (0,1)
        // 2 → (1,0)
        // 3 → (2,0)
        // 4 → (1,1)
        // 5 → (0,2)
        Assert.Equal(0,  ExrDct.ZigzagToRowMajor[0]);   // (0,0)
        Assert.Equal(1,  ExrDct.ZigzagToRowMajor[1]);   // (0,1)
        Assert.Equal(8,  ExrDct.ZigzagToRowMajor[2]);   // (1,0)
        Assert.Equal(16, ExrDct.ZigzagToRowMajor[3]);   // (2,0)
        Assert.Equal(9,  ExrDct.ZigzagToRowMajor[4]);   // (1,1)
        Assert.Equal(2,  ExrDct.ZigzagToRowMajor[5]);   // (0,2)
        Assert.Equal(63, ExrDct.ZigzagToRowMajor[63]);  // (7,7)
    }

    [Fact]
    public void DctRoundTrip_UniformBlock_ConcentratesEnergyAtDc()
    {
        // Forward DCT of a uniform-value block: all energy at [0,0].
        var block = new float[64];
        for (int i = 0; i < 64; i++) block[i] = 50.0f;
        ExrDct.Forward8x8InPlace(block);

        // DC coefficient = 8 * value (per the orthonormal scaling).
        Assert.Equal(8 * 50.0f, block[0], 1e-3f);
        for (int i = 1; i < 64; i++)
            Assert.True(Math.Abs(block[i]) < 1e-3f,
                $"AC[{i}] should be ~0 for uniform input, got {block[i]}");
    }

    [Fact]
    public void ExpandAc_AllLiterals_FillsBlocksDirectly()
    {
        // Three blocks × 63 AC = 189 literal tokens. Each token's
        // value is its position so we can verify placement.
        var tokens = new ushort[189];
        for (int i = 0; i < tokens.Length; i++) tokens[i] = (ushort)(0x1000 + i);

        var blocks = new ushort[3 * 64];
        for (int b = 0; b < 3; b++) blocks[b * 64] = (ushort)(0xC000 + b);  // DC sentinels

        Assert.True(ExrDct.ExpandAcTokens(tokens, 3, blocks));
        // DC positions untouched.
        Assert.Equal(0xC000, blocks[0]);
        Assert.Equal(0xC001, blocks[64]);
        Assert.Equal(0xC002, blocks[128]);
        // ACs in block 0 fill positions 1..63 with tokens 0..62.
        for (int i = 0; i < 63; i++) Assert.Equal((ushort)(0x1000 + i), blocks[1 + i]);
        // Block 1 ACs from tokens 63..125.
        for (int i = 0; i < 63; i++) Assert.Equal((ushort)(0x1000 + 63 + i), blocks[64 + 1 + i]);
    }

    [Fact]
    public void ExpandAc_OneLargeZeroRun_FillsAllBlocksWithZeros()
    {
        // 2 blocks × 63 AC = 126 zero coefficients. One token: 0xFF7E
        // means "insert 126 zeros". A full run can't actually fit in
        // one token (low byte caps at 0xFF = 255 zeros, but we only
        // need 126 here so a single token suffices).
        var tokens = new ushort[] { 0xFF7E };
        var blocks = new ushort[2 * 64];
        Assert.True(ExrDct.ExpandAcTokens(tokens, 2, blocks));
        for (int b = 0; b < 2; b++)
            for (int i = 1; i < 64; i++)
                Assert.Equal(0, blocks[b * 64 + i]);
    }

    [Fact]
    public void ExpandAc_MixedRunsAndLiterals_PlacesCorrectly()
    {
        // Block layout: 5 zeros, then literal 0x4200, then 57 zeros.
        // Total: 5 + 1 + 57 = 63 AC slots. Tokens: 0xFF05, 0x4200, 0xFF39.
        var tokens = new ushort[] { 0xFF05, 0x4200, 0xFF39 };
        var blocks = new ushort[64];

        Assert.True(ExrDct.ExpandAcTokens(tokens, 1, blocks));
        // Positions 1..5 zero, 6 = 0x4200, 7..63 zero.
        for (int i = 1; i <= 5; i++) Assert.Equal(0, blocks[i]);
        Assert.Equal(0x4200, blocks[6]);
        for (int i = 7; i < 64; i++) Assert.Equal(0, blocks[i]);
    }

    [Fact]
    public void ExpandAc_RunSpansBlockBoundary_DoesNotOverrun()
    {
        // 2 blocks × 63 AC = 126 AC slots. A 70-zero run partially
        // fills block 0's 63 positions (1..63) and continues into
        // block 1 (positions 1..7). Then a literal at block 1 position 8,
        // then 55 zeros to fill the rest.
        var tokens = new ushort[]
        {
            0xFF46,  // 70 zeros
            0xABCD,  // literal at block 1 position 8
            0xFF37,  // 55 zeros
        };
        var blocks = new ushort[2 * 64];
        Assert.True(ExrDct.ExpandAcTokens(tokens, 2, blocks));
        // Block 0 AC positions all zero.
        for (int i = 1; i < 64; i++) Assert.Equal(0, blocks[i]);
        // Block 1 AC positions 1..7 zero (from spillover), position 8 literal, 9..63 zero.
        for (int i = 1; i <= 7; i++) Assert.Equal(0, blocks[64 + i]);
        Assert.Equal(0xABCD, blocks[64 + 8]);
        for (int i = 9; i < 64; i++) Assert.Equal(0, blocks[64 + i]);
    }

    [Fact]
    public void ExpandAc_TokenStreamUnderflow_ReturnsFalse()
    {
        // Tokens fill only block 0 partially. The expander should
        // signal failure rather than leaving block 1 uninitialised.
        var tokens = new ushort[] { 0xFF20 };  // 32 zeros — short of 63
        var blocks = new ushort[64];
        Assert.False(ExrDct.ExpandAcTokens(tokens, 1, blocks));
    }

    [Fact]
    public void ExpandAc_EobMarker_AdvancesEarlyToNextBlock()
    {
        // Block 0 has one literal at position 1, then EOB (0xFF00).
        // Remaining positions 2..63 of block 0 stay zero. Block 1's
        // first token (next in the stream) goes to AC[1] of block 1.
        var tokens = new ushort[]
        {
            0xCAFE,  // block 0 position 1
            0xFF00,  // EOB → block 0 done
            0xBEEF,  // block 1 position 1
            0xFF3E,  // block 1: 62 zeros (positions 2..63)
        };
        var blocks = new ushort[2 * 64];
        Assert.True(ExrDct.ExpandAcTokens(tokens, 2, blocks));
        Assert.Equal(0xCAFE, blocks[1]);                    // block 0 [1]
        for (int i = 2; i < 64; i++) Assert.Equal(0, blocks[i]);
        Assert.Equal(0xBEEF, blocks[64 + 1]);               // block 1 [1]
        for (int i = 2; i < 64; i++) Assert.Equal(0, blocks[64 + i]);
    }

    [Fact]
    public void PlaceBlock_AlignedAtOrigin_WritesAllPixels()
    {
        // 8×8 image, block at (0,0). Each block sample i writes
        // HALF((i+1)/64) to the corresponding pixel.
        var block = new float[64];
        for (int i = 0; i < 64; i++) block[i] = (i + 1) / 64.0f;
        var dst = new byte[8 * 8 * 2];
        ExrDct.PlaceBlock(block, dst, 8, 8, 0, 0, applyToLinear: false);

        for (int i = 0; i < 64; i++)
        {
            ushort bits = (ushort)(dst[i * 2] | (dst[i * 2 + 1] << 8));
            Half h = BitConverter.UInt16BitsToHalf(bits);
            Assert.Equal((float)(Half)((i + 1) / 64.0f), (float)h, 1e-3f);
        }
    }

    [Fact]
    public void PlaceBlock_RightEdge_ClipsExtraColumns()
    {
        // 6-wide image, block at (0,0): only the leftmost 6 columns
        // of each row are valid. Mark all 64 dst bytes 0xFF first to
        // verify we don't touch the trailing region.
        var block = new float[64];
        for (int i = 0; i < 64; i++) block[i] = 1.0f;
        var dst = new byte[6 * 8 * 2];
        for (int i = 0; i < dst.Length; i++) dst[i] = 0xAA;  // sentinel

        ExrDct.PlaceBlock(block, dst, 6, 8, 0, 0, applyToLinear: false);

        // First 6 pixels of each of 8 rows touched; trailing untouched.
        ushort halfOne = BitConverter.HalfToUInt16Bits((Half)1.0f);
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 6; x++)
            {
                int o = (y * 6 + x) * 2;
                ushort bits = (ushort)(dst[o] | (dst[o + 1] << 8));
                Assert.Equal(halfOne, bits);
            }
        }
    }

    [Fact]
    public void PlaceBlock_BottomEdge_ClipsExtraRows()
    {
        // 8-wide × 4-tall image, block at (0,0): only top 4 rows valid.
        var block = new float[64];
        for (int i = 0; i < 64; i++) block[i] = 0.5f;
        var dst = new byte[8 * 4 * 2];
        ExrDct.PlaceBlock(block, dst, 8, 4, 0, 0, applyToLinear: false);

        ushort halfHalf = BitConverter.HalfToUInt16Bits((Half)0.5f);
        for (int i = 0; i < 32; i++)
        {
            ushort bits = (ushort)(dst[i * 2] | (dst[i * 2 + 1] << 8));
            Assert.Equal(halfHalf, bits);
        }
    }

    [Fact]
    public void PlaceBlock_OffsetBlock_LandsAtRightCorner()
    {
        // 16×16 image, block at (8, 8). Verifies the block lands in
        // the bottom-right quadrant; corners of the other 3 quadrants
        // stay zero.
        var block = new float[64];
        for (int i = 0; i < 64; i++) block[i] = 2.0f;
        var dst = new byte[16 * 16 * 2];
        ExrDct.PlaceBlock(block, dst, 16, 16, 8, 8, applyToLinear: false);

        ushort halfTwo = BitConverter.HalfToUInt16Bits((Half)2.0f);

        // Pixel (8, 8): start of placed block.
        int p = (8 * 16 + 8) * 2;
        Assert.Equal(halfTwo, (ushort)(dst[p] | (dst[p + 1] << 8)));
        // Pixel (15, 15): last pixel of block.
        p = (15 * 16 + 15) * 2;
        Assert.Equal(halfTwo, (ushort)(dst[p] | (dst[p + 1] << 8)));
        // Pixel (0, 0): outside the placed block, stays zero.
        Assert.Equal(0, dst[0]);
        Assert.Equal(0, dst[1]);
        // Pixel (7, 8): just left of block, stays zero.
        p = (8 * 16 + 7) * 2;
        Assert.Equal(0, dst[p]);
    }

    [Fact]
    public void PlaceBlock_ToLinearTrueSquaresHalfValues()
    {
        // applyToLinear squares the HALF — input 2.0 → output 4.0.
        var block = new float[64];
        for (int i = 0; i < 64; i++) block[i] = 2.0f;
        var dst = new byte[8 * 8 * 2];
        ExrDct.PlaceBlock(block, dst, 8, 8, 0, 0, applyToLinear: true);

        ushort halfFour = BitConverter.HalfToUInt16Bits((Half)4.0f);
        for (int i = 0; i < 64; i++)
        {
            ushort bits = (ushort)(dst[i * 2] | (dst[i * 2 + 1] << 8));
            Assert.Equal(halfFour, bits);
        }
    }

    [Fact]
    public void DecodeDcStream_RoundTripsKnownValues()
    {
        // Encode a known sequence of ushorts via zlib then decode through
        // ExrDct.DecodeDcStream. Validates the wire format we expect:
        // little-endian shorts, zlib-wrapped.
        ushort[] expected = { 0x1234, 0x5678, 0xDEAD, 0xBEEF, 0x0001, 0xFFFF };
        var raw = new byte[expected.Length * 2];
        for (int i = 0; i < expected.Length; i++)
        {
            raw[i * 2]     = (byte)expected[i];
            raw[i * 2 + 1] = (byte)(expected[i] >> 8);
        }
        var compressed = ZlibWrap(raw);

        var got = ExrDct.DecodeDcStream(compressed, 0, compressed.Length, expected.Length);
        Assert.NotNull(got);
        Assert.Equal(expected, got);
    }

    [Fact]
    public void DecodeDcStream_BadZlib_ReturnsNull()
    {
        // Random bytes won't decompress — should fail cleanly with null.
        var garbage = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 };
        var got = ExrDct.DecodeDcStream(garbage, 0, garbage.Length, 8);
        Assert.Null(got);
    }

    [Fact]
    public void DecodeDcStream_TruncatedPayload_ReturnsNull()
    {
        // Compress 8 ushorts, then ask the decoder for 16 — short by half.
        ushort[] encoded = new ushort[8];
        for (int i = 0; i < 8; i++) encoded[i] = (ushort)(i * 0x101);
        var raw = new byte[encoded.Length * 2];
        for (int i = 0; i < encoded.Length; i++)
        {
            raw[i * 2]     = (byte)encoded[i];
            raw[i * 2 + 1] = (byte)(encoded[i] >> 8);
        }
        var compressed = ZlibWrap(raw);

        var got = ExrDct.DecodeDcStream(compressed, 0, compressed.Length, 16);
        Assert.Null(got);
    }

    private static byte[] ZlibWrap(byte[] data)
    {
        using var ms = new System.IO.MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(ms,
            System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            z.Write(data, 0, data.Length);
        }
        return ms.ToArray();
    }
}
