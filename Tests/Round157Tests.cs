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
}
