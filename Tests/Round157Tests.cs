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
}
